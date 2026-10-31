using System.IO;
using System.Text;
using PGMS.Desktop;

internal static class SqlReadChecks
{
    internal sealed class SlowStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; init; }
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public int LargestRequest { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            LargestRequest = Math.Max(LargestRequest, buffer.Length);
            if (++Calls > 1)
            {
                Entered.TrySetResult();
                if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (Fail) throw new IOException("Synthetic read failure");
            }
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], cancellationToken);
        }
    }

    private sealed class DeclaredStream(byte[] bytes, long length) : MemoryStream(bytes)
    {
        public override long Length => length;
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        int uiThread = Environment.CurrentManagedThreadId;
        int preparationThread = uiThread;
        var prepared = await Task.Run(() => { preparationThread = Environment.CurrentManagedThreadId; return PreparedQueryDocument.Create("SELECT '漢字';\r\n"); });
        var document = prepared.TakeOwnership();
        check(preparationThread != uiThread && document.Text == "SELECT '漢字';\r\n" && document.LineCount == 2, "Prepared document builds on worker and transfers complete text and line index to UI");
        document.Insert(document.TextLength, "-- edit"); document.UndoStack.Undo();
        check(document.Text == "SELECT '漢字';\r\n", "Transferred document supports Undo on owner thread");
        bool reused = false;
        try { prepared.TakeOwnership(); } catch (InvalidOperationException) { reused = true; }
        check(reused, "Prepared document cannot be attached twice");
        bool wrongThread = await Task.Run(() => { try { document.VerifyAccess(); return false; } catch (InvalidOperationException) { return true; } });
        check(wrongThread, "Transferred document rejects worker access after UI ownership");
        var firstView = new QueryView(); var secondView = new QueryView();
        var firstEditor = (ICSharpCode.AvalonEdit.TextEditor)firstView.FindName("Editor");
        var secondEditor = (ICSharpCode.AvalonEdit.TextEditor)secondView.FindName("Editor");
        check(ReferenceEquals(firstEditor.SyntaxHighlighting, secondEditor.SyntaxHighlighting), "SQL editors reuse one parsed highlighting definition");
        firstEditor.Text = "SELECT 1;"; secondEditor.Text = "SELECT 2;"; firstEditor.AppendText("-- first");
        firstEditor.Undo();
        check(firstEditor.Text == "SELECT 1;" && secondEditor.Text == "SELECT 2;" && firstView.IsDirty && secondView.IsDirty, "Shared highlighting preserves independent text Undo and dirty state");
        await firstView.DisposeAsync(); await secondView.DisposeAsync();
        string root = Path.GetFullPath(Path.Combine("artifacts", "read-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string boundaryPath = Path.Combine(root, "boundary.sql");
        byte[] block = Enumerable.Repeat((byte)'a', 65536).ToArray();
        using (var writer = File.Create(boundaryPath))
            for (int i = 0; i < SqlFileStore.MaximumBytes / block.Length; i++) writer.Write(block);
        var boundary = await SqlFileStore.ReadAsync(boundaryPath);
        check(boundary.Text.Length == SqlFileStore.MaximumBytes && boundary.Text.All(c => c == 'a') && boundary.Version == SqlFileStore.CaptureVersion(boundaryPath), "Async SQL read preserves exact 16 MiB boundary and fingerprint");
        using (var writer = new FileStream(boundaryPath, FileMode.Append)) writer.WriteByte((byte)'a');
        bool boundaryRejected = false;
        try { await SqlFileStore.ReadAsync(boundaryPath); } catch (IOException) { boundaryRejected = true; }
        check(boundaryRejected && new FileInfo(boundaryPath).Length == SqlFileStore.MaximumBytes + 1L, "Async SQL read refuses 16 MiB plus one byte without modifying source");
        foreach (var format in Enum.GetValues<SqlFileFormat>())
        {
            string path = Path.Combine(root, format + ".sql");
            SqlFileStore.Write(path, "SELECT 'ação 漢字 😀';\r\n", format);
            var actual = await SqlFileStore.ReadAsync(path);
            check(actual == SqlFileStore.Read(path), "Async disk reader matches strict snapshot for " + format);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            check(exclusive.CanWrite, "Async reader releases disk handle for " + format);
        }
        using var shortStream = new DeclaredStream([65, 66], 4);
        bool shortRejected = false;
        try { await SqlFileStore.ReadStreamAsync(shortStream, "short.sql", CancellationToken.None); } catch (EndOfStreamException) { shortRejected = true; }
        check(shortRejected, "Async reader rejects premature EOF without returning partial SQL");
        using var oversize = new DeclaredStream([], SqlFileStore.MaximumBytes + 1L);
        bool sizeRejected = false;
        try { await SqlFileStore.ReadStreamAsync(oversize, "large.sql", CancellationToken.None); } catch (IOException) { sizeRejected = true; }
        check(sizeRejected && oversize.Position == 0, "Async reader rejects oversize declared file before reading bytes");
        using var empty = new MemoryStream();
        check((await SqlFileStore.ReadStreamAsync(empty, "empty.sql", CancellationToken.None)).Text == "", "Async reader preserves an empty SQL file");
        var queue = new RecoveryIoQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int completed = 0;
        var first = queue.RunAwaitedAsync(async () => { entered.SetResult(); await release.Task; completed = 1; return 1; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = queue.RunAsync(() => completed == 1);
        check(!second.IsCompleted, "Awaited IO queue does not overtake an asynchronous predecessor");
        release.SetResult();
        check(await first == 1 && await second, "Awaited and synchronous IO retain submission order");
        var failure = queue.RunAwaitedAsync<int>(() => Task.FromException<int>(new IOException("synthetic")));
        try { await failure; } catch (IOException) { }
        check(await queue.RunAwaitedAsync(() => Task.FromResult(7)) == 7, "Awaited IO queue recovers after asynchronous failure");
        byte[] bytes = Encoding.UTF8.GetBytes("SELECT 'ação 漢字';\r\n");
        using var partial = new SlowStream(bytes);
        var result = await SqlFileStore.ReadStreamAsync(partial, "synthetic.sql", CancellationToken.None);
        check(result.Text == Encoding.UTF8.GetString(bytes) && partial.Calls > 1 && partial.LargestRequest <= 65536, "Async reader joins short reads before strict Unicode decoding");
        check(result.Version.Sha256 == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), "Async short-read fingerprint covers exact original bytes");
        using var token = new CancellationTokenSource();
        using var slow = new SlowStream(bytes) { Block = true };
        var reading = SqlFileStore.ReadStreamAsync(slow, "slow.sql", token.Token);
        await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        token.Cancel();
        bool canceled = false;
        try { await reading.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { canceled = true; }
        check(canceled && slow.Position == 3, "Cancellation interrupts an awaited synthetic read after partial bytes without producing snapshot");
        using var broken = new SlowStream(bytes) { Fail = true };
        bool failed = false;
        try { await SqlFileStore.ReadStreamAsync(broken, "broken.sql", CancellationToken.None); } catch (IOException) { failed = true; }
        check(failed && broken.Position == 3, "Read failure after partial bytes propagates without partial SQL snapshot");
        using var retry = new MemoryStream(bytes);
        check((await SqlFileStore.ReadStreamAsync(retry, "retry.sql", CancellationToken.None)).Text == result.Text, "A failed read does not poison a later complete snapshot");
    }
}

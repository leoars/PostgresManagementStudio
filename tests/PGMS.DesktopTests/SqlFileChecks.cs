using System.IO;
using System.Text;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using PGMS.Desktop;

internal static class SqlFileChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine("artifacts", "sql-file-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        const string text = "SELECT 'ação 😀';\r\n-- mixed\nSELECT 2;\r";
        foreach (var format in Enum.GetValues<SqlFileFormat>())
        {
            string path = Path.Combine(root, format + ".sql");
            var encoding = SqlFileStore.GetEncoding(format);
            File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes(text)]);
            var read = SqlFileStore.Read(path);
            check(read.Text == text && read.Format == format && read.Path == path, "SQL open preserves Unicode/newlines and detects " + format);
        }
        foreach (var format in Enum.GetValues<SqlFileFormat>())
        {
            string path = Path.Combine(root, format + ".sql");
            byte[] original = File.ReadAllBytes(path);
            SqlFileStore.Write(path, text, format, SqlFileStore.CaptureVersion(path));
            check(File.ReadAllBytes(path).SequenceEqual(original), "SQL save preserves exact bytes/BOM for " + format);
        }
        string protectedFile = Path.Combine(root, "protected.sql");
        string repeatedPath = Path.Combine(root, "repeated.sql");
        var repeated = SqlFileStore.Write(repeatedPath, "initial", SqlFileFormat.Utf8);
        for (int i = 0; i < 100; i++)
            repeated = SqlFileStore.Write(repeatedPath, "SELECT " + i + ";", SqlFileFormat.Utf8, repeated.Version);
        check(SqlFileStore.Read(repeatedPath).Text == "SELECT 99;" && !Directory.EnumerateFiles(root, "*.partial").Any(),
            "Repeated guarded replacement retains exact latest text without staging files");
        SqlFileStore.Write(protectedFile, "original", SqlFileFormat.Utf8);
        var protectedVersion = SqlFileStore.CaptureVersion(protectedFile);
        bool refused = false;
        try { SqlFileStore.Write(protectedFile, "changed", SqlFileFormat.Utf8); } catch (IOException) { refused = true; }
        check(refused && File.ReadAllText(protectedFile) == "original", "SQL save refuses unapproved overwrite");
        using (var locked = new FileStream(protectedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            refused = false;
            try { SqlFileStore.Write(protectedFile, "changed", SqlFileFormat.Utf8, protectedVersion); } catch (IOException) { refused = true; }
            check(refused, "SQL save reports locked destination");
        }
        check(File.ReadAllText(protectedFile) == "original" && Directory.GetFiles(root, "*.partial").Length == 0, "Failed SQL save preserves original and removes staging file");
        refused = false;
        try { SqlFileStore.Write(protectedFile, "\uD800", SqlFileFormat.Utf8, protectedVersion); } catch (InvalidDataException) { refused = true; }
        check(refused && File.ReadAllText(protectedFile) == "original", "SQL save refuses invalid Unicode before touching destination");
        refused = false;
        try { SqlFileStore.Write(protectedFile, new string('x', SqlFileStore.MaximumBytes + 1), SqlFileFormat.Utf8, protectedVersion); } catch (IOException) { refused = true; }
        check(refused && File.ReadAllText(protectedFile) == "original", "SQL save enforces encoded byte budget");
        string conflict = Path.Combine(root, "conflict.sql");
        var opened = SqlFileStore.Write(conflict, "SELECT 1;", SqlFileFormat.Utf8);
        DateTime timestamp = File.GetLastWriteTimeUtc(conflict);
        File.WriteAllText(conflict, "SELECT 2;"); File.SetLastWriteTimeUtc(conflict, timestamp);
        refused = false;
        try { SqlFileStore.Write(conflict, "SELECT 3;", opened.Format, opened.Version); } catch (IOException) { refused = true; }
        check(refused && File.ReadAllText(conflict) == "SELECT 2;", "SQL save detects same-size external change even with restored timestamp");
        File.Delete(conflict);
        refused = false;
        try { SqlFileStore.Write(conflict, "SELECT 3;", opened.Format, opened.Version); } catch (IOException) { refused = true; }
        check(refused && !File.Exists(conflict), "SQL save does not recreate externally deleted file silently");
        var recovered = SqlFileStore.Write(conflict, "SELECT 4;", SqlFileFormat.Utf8);
        SqlFileStore.Write(conflict, "SELECT 5;", recovered.Format, recovered.Version);
        check(File.ReadAllText(conflict) == "SELECT 5;", "SQL save accepts explicitly captured current version");
        refused = false;
        try { SqlFileStore.Write(protectedFile, "other", SqlFileFormat.Utf8, recovered.Version); } catch (IOException) { refused = true; }
        check(refused && File.ReadAllText(protectedFile) == "original", "SQL save rejects a version from another path");
        File.SetAttributes(protectedFile, FileAttributes.ReadOnly);
        try
        {
            refused = false;
            try { SqlFileStore.Write(protectedFile, "changed", SqlFileFormat.Utf8, protectedVersion); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { refused = true; }
            check(refused && File.ReadAllText(protectedFile) == "original", "SQL save preserves read-only destination");
        }
        finally { File.SetAttributes(protectedFile, FileAttributes.Normal); }
        refused = false;
        try { SqlFileStore.Write(protectedFile, "SELECT '\0';", SqlFileFormat.Utf8, protectedVersion); } catch (InvalidDataException) { refused = true; }
        check(refused && File.ReadAllText(protectedFile) == "original", "SQL save refuses NUL without changing disk");
        foreach (var format in Enum.GetValues<SqlFileFormat>())
        {
            string bomOnly = Path.Combine(root, "empty-" + format + ".sql");
            SqlFileStore.Write(bomOnly, "", format);
            var snapshot = SqlFileStore.Read(bomOnly);
            check(snapshot.Text == "" && snapshot.Format == format, "Empty SQL roundtrip preserves " + format);
        }
        check(Directory.GetFiles(root, "*.partial").Length == 0, "All rejected SQL saves leave no partial files");
        string invalid = Path.Combine(root, "invalid.sql");
        void Rejected(byte[] bytes, string name)
        {
            File.WriteAllBytes(invalid, bytes);
            bool rejected = false;
            try { SqlFileStore.Read(invalid); } catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllBytes(invalid).SequenceEqual(bytes), name);
        }
        Rejected([0xC3, 0x28], "SQL open rejects malformed UTF-8 without modifying file");
        Rejected([0xE9], "SQL open refuses legacy encoding instead of replacing characters");
        Rejected([0xFF, 0xFE, 0x00, 0xD8], "SQL open rejects unpaired UTF-16 surrogate");
        Rejected([0x00, 0x00, 0xFE, 0xFF, 0x00, 0x11, 0x00, 0x00], "SQL open rejects invalid UTF-32 code point");
        Rejected([0x00], "SQL open rejects NUL/binary content");
        string large = Path.Combine(root, "large.sql");
        using (var stream = File.Create(large)) stream.SetLength(SqlFileStore.MaximumBytes + 1L);
        bool bounded = false;
        try { SqlFileStore.Read(large); } catch (IOException) { bounded = true; }
        check(bounded, "SQL open rejects file above 16 MiB before decoding");
        string empty = Path.Combine(root, "empty.sql"); File.WriteAllBytes(empty, []);
        check(SqlFileStore.Read(empty).Text == "", "SQL open accepts empty UTF-8 file");
        var window = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            var query = window.OpenQueryFile(Path.Combine(root, "Utf16LittleEndian.sql"));
            var manager = (DockingManager)window.FindName("DockManager");
            check(((ICSharpCode.AvalonEdit.TextEditor)query.FindName("Editor")).Text == text && !query.IsDirty && !query.HasSession && query.FileFormat == SqlFileFormat.Utf16LittleEndian, "SQL open binds clean WPF document without connection");
            var editor = (ICSharpCode.AvalonEdit.TextEditor)query.FindName("Editor");
            editor.Text = text + "-- edited";
            string savedPath = Path.Combine(root, "saved.sql");
            window.SaveQueryFile(query, savedPath);
            check(!query.IsDirty && query.FilePath == savedPath && SqlFileStore.Read(savedPath).Format == SqlFileFormat.Utf16LittleEndian, "WPF save updates path/clean state only after success and preserves format");
            editor.AppendText("-- pending");
            using (var locked = new FileStream(savedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool failed = false;
                try { window.SaveQueryFile(query, savedPath, true); } catch (IOException) { failed = true; }
                check(failed && query.IsDirty && query.FilePath == savedPath && editor.Text.EndsWith("-- pending"), "Failed WPF save retains dirty text and original path");
            }
            File.WriteAllText(savedPath, "external version");
            bool conflictRejected = false;
            try { window.SaveQueryFile(query, savedPath, true); } catch (IOException) { conflictRejected = true; }
            check(conflictRejected && query.IsDirty && editor.Text.EndsWith("-- pending") && File.ReadAllText(savedPath) == "external version", "WPF external conflict preserves both disk version and unsaved edits");
            string recoveryPath = Path.Combine(root, "recovered.sql");
            window.SaveQueryFile(query, recoveryPath);
            check(!query.IsDirty && query.FilePath == recoveryPath && SqlFileStore.Read(recoveryPath).Text == editor.Text, "WPF Save As recovers conflicting edits into a new file");
            editor.AppendText("-- retained");
            var duplicate = window.OpenQueryFile(Path.Combine(root, ".", "RECOVERED.SQL"));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            check(ReferenceEquals(duplicate, query) && query.IsDirty && editor.Text.EndsWith("-- retained"), "Opening the same normalized/case-insensitive path retains dirty tab");
            check(manager.ActiveContent == query && editor.IsKeyboardFocusWithin, "Opening an existing SQL file activates and focuses its editor");
            var other = window.OpenQueryFile(empty);
            var otherEditor = (ICSharpCode.AvalonEdit.TextEditor)other.FindName("Editor");
            otherEditor.Text = "SELECT 'other';";
            byte[] recoveryBefore = File.ReadAllBytes(recoveryPath);
            bool duplicateTarget = false;
            try { window.SaveQueryFile(other, recoveryPath, true); } catch (IOException) { duplicateTarget = true; }
            check(duplicateTarget && other.IsDirty && other.FilePath == empty && File.ReadAllBytes(recoveryPath).SequenceEqual(recoveryBefore), "Save As refuses another open tab's destination without changing either file");
            window.OpenQueryFile(recoveryPath);
            System.Windows.Input.ApplicationCommands.Save.Execute(null, window);
            await window.SaveCompletion;
            check(!query.IsDirty && SqlFileStore.Read(recoveryPath).Text.EndsWith("-- retained"), "Routed Save uses guarded file writer and clears dirty state");
            var savedDocument = manager.Layout.Descendents().OfType<LayoutDocument>().Single(d => d.Content == query);
            check(savedDocument.Title == "recovered.sql", "Successful routed Save clears the title marker");
            System.Windows.Input.ApplicationCommands.New.Execute(null, window);
            var fresh = (QueryView)manager.ActiveContent;
            ((ICSharpCode.AvalonEdit.TextEditor)fresh.FindName("Editor")).Text = "SELECT 'new 😀';";
            string freshPath = Path.Combine(root, "fresh.sql");
            window.SaveQueryFile(fresh, freshPath);
            check(fresh.FileFormat == SqlFileFormat.Utf8 && File.ReadAllBytes(freshPath).SequenceEqual(new UTF8Encoding(false).GetBytes("SELECT 'new 😀';")), "New query saves UTF-8 without BOM");
            savedDocument.Close();
            await window.TabCloseCompletion;
            var reopened = window.OpenQueryFile(recoveryPath);
            check(!ReferenceEquals(reopened, query) && !reopened.IsDirty && SqlFileStore.Read(recoveryPath).Text == ((ICSharpCode.AvalonEdit.TextEditor)reopened.FindName("Editor")).Text, "Closed saved query can be reopened from disk with fresh version");
            var secondWindow = new MainWindow { ShowInTaskbar = false, Opacity = 0 };
            secondWindow.Show();
            QueryView? secondQuery = null;
            try
            {
                secondQuery = secondWindow.OpenQueryFile(recoveryPath);
                var secondEditor = (ICSharpCode.AvalonEdit.TextEditor)secondQuery.FindName("Editor");
                secondEditor.AppendText("-- second window");
                ((ICSharpCode.AvalonEdit.TextEditor)reopened.FindName("Editor")).AppendText("-- first window");
                window.SaveQueryFile(reopened, recoveryPath, true);
                bool secondRejected = false;
                try { secondWindow.SaveQueryFile(secondQuery, recoveryPath, true); } catch (IOException) { secondRejected = true; }
                check(secondRejected && secondQuery.IsDirty && secondEditor.Text.EndsWith("-- second window") && SqlFileStore.Read(recoveryPath).Text.EndsWith("-- first window"), "Two windows cannot silently overwrite an earlier saved version");
            }
            finally
            {
                if (secondQuery is not null) secondQuery.IsDirty = false;
                secondWindow.Close(); await secondWindow.CloseCompletion;
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            other.IsDirty = false;
            query.IsDirty = false; // fixture cleanup must not display a close confirmation
            check(!window.TryOpenQueryFile(invalid, out var openError) && openError is InvalidDataException, "Open command path reports invalid encoding instead of throwing through WPF");
            var freshEditor = (ICSharpCode.AvalonEdit.TextEditor)fresh.FindName("Editor");
            freshEditor.Text = "SELECT '\0';";
            byte[] freshBefore = File.ReadAllBytes(freshPath);
            check(!window.TrySaveQueryFile(fresh, freshPath, out var saveError, true) && saveError is InvalidDataException && fresh.IsDirty && File.ReadAllBytes(freshPath).SequenceEqual(freshBefore), "Save command path reports invalid text and retains unsaved edits");
            freshEditor.Text = "SELECT 'retry';";
            check(window.TrySaveQueryFile(fresh, freshPath, out saveError, true) && saveError is null && !fresh.IsDirty && File.ReadAllText(freshPath) == "SELECT 'retry';", "Save command path retries successfully after correcting invalid text");
            int count = manager.Layout.Descendents().OfType<LayoutDocument>().Count();
            try { window.OpenQueryFile(invalid); } catch (InvalidDataException) { }
            check(manager.Layout.Descendents().OfType<LayoutDocument>().Count() == count, "Failed SQL open creates no blank tab or replaces current text");
        }
        finally
        {
            var manager = (DockingManager)window.FindName("DockManager");
            foreach (var document in manager.Layout.Descendents().OfType<LayoutDocument>())
                if (document.Content is QueryView query) query.IsDirty = false;
            window.Close(); await window.CloseCompletion; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }
        await SqlReadChecks.RunAsync(check);
        await QueryCloseCommandChecks.RunAsync(check);
        await SqlFileAsyncChecks.RunAsync(check);
        await SqlOpenAsyncChecks.RunAsync(check);
    }
}

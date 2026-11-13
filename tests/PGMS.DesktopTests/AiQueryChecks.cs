using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Npgsql;
using PGMS.Desktop;

internal static class AiQueryChecks
{
    private const string AnthropicKey = "sk-ant-query-test-0123", OpenAiKey = "sk-openai-query-test-4567";

    internal static async Task RunAsync(string connection, Action<bool, string> check)
    {
        check(AiQueryGenerator.ExtractSql("Here:\n```sql\nSELECT 1;\n```\nDone") == "SELECT 1;" && AiQueryGenerator.ExtractSql("```\nSELECT 2;\n```") == "SELECT 2;"
            && AiQueryGenerator.ExtractSql("SELECT 3;") == "SELECT 3;" && AiQueryGenerator.ExtractSql("```postgresql\r\nSELECT 4;\r\n```") == "SELECT 4;", "SQL is taken from the first fenced block, or the whole reply without one");

        using var fake = new FakeChat();
        var original = AiQueryGenerator.IsClaudeApi;
        try
        {
            AiQueryGenerator.IsClaudeApi = _ => true;
            var anthropic = AiSettings.Default.For(AiProvider.Anthropic).WithKey(AnthropicKey) with { Endpoint = fake.Url };
            var result = await AiQueryGenerator.GenerateAsync(anthropic, "<schema>\nTABLE public.t\n</schema>\n\n<request>\ncount rows\n</request>");
            var body = fake.LastBody!.RootElement;
            check(result.Sql == "SELECT count(*) FROM public.t;" && body.GetProperty("model").GetString() == "claude-opus-5-5" && body.GetProperty("system").GetString()!.Contains("Use only schemas, tables", StringComparison.Ordinal)
                && body.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("<request>\ncount rows", StringComparison.Ordinal), "Anthropic request carries the instructions, schema and request and returns the SQL");
            check(body.TryGetProperty("fallbacks", out var fallbacks) && fallbacks.GetString() == "default" && fake.LastBeta.Contains("server-side-fallback-2026-07-01", StringComparison.Ordinal), "Claude Opus 5.5 requests opt into the default refusal fallback");
            await AiQueryGenerator.GenerateAsync(anthropic with { Model = "claude-haiku-5-5" }, "<request>x</request>");
            check(!fake.LastBody!.RootElement.TryGetProperty("fallbacks", out _), "Models without server-side fallback are sent without it");
            string? refusal = null;
            try { await AiQueryGenerator.GenerateAsync(anthropic, "<request>refuse</request>"); } catch (AiProviderException ex) { refusal = ex.Message; }
            check(refusal is not null && refusal.Contains("declined", StringComparison.Ordinal), "A refusal is reported instead of inserting text");

            var openAi = AiSettings.Default.For(AiProvider.OpenAI).WithKey(OpenAiKey) with { Endpoint = fake.Url + "/v1", Model = "gpt-test" };
            var openResult = await AiQueryGenerator.GenerateAsync(openAi, "<request>count rows</request>");
            var openBody = fake.LastBody!.RootElement;
            check(openResult.Sql == "SELECT 42;" && openBody.GetProperty("messages")[0].GetProperty("role").GetString() == "system" && fake.LastAuthorization == "Bearer " + OpenAiKey, "OpenAI chat request sends system and user messages with the key");
            var ollama = AiSettings.Default.For(AiProvider.Ollama) with { Endpoint = fake.Url + "/ollama", Model = "sqlcoder:7b" };
            var ollamaResult = await AiQueryGenerator.GenerateAsync(ollama, "<request>count rows</request>");
            check(ollamaResult.Sql == "SELECT 7;" && fake.LastBody!.RootElement.GetProperty("stream").GetBoolean() == false && fake.LastAuthorization.Length == 0, "Ollama chat request asks for one answer and sends no key");
            string? noModel = null;
            try { await AiQueryGenerator.GenerateAsync(ollama with { Model = "" }, "x"); } catch (AiProviderException ex) { noModel = ex.Message; }
            check(noModel is not null && noModel.Contains("Choose a model", StringComparison.Ordinal), "Generation without a model explains what to configure");

            // End to end against the isolated cluster: structure only, never rows, and nothing inserted until Insert.
            string schema = "ai_dialog_" + Guid.NewGuid().ToString("N")[..8];
            const string Secret = "dialog-secret-row-91c2";
            await using var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync();
            async Task Sql(string sql) { await using var command = new NpgsqlCommand(sql, admin); await command.ExecuteNonQueryAsync(); }
            await Sql($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.invoices (id int PRIMARY KEY, note text); INSERT INTO {schema}.invoices VALUES (1, '{Secret}');");
            try
            {
                string? inserted = null;
                var dialog = new AiQueryDialog(ollama, connection, "SELECT * FROM invoices", sql => inserted = sql) { ShowInTaskbar = false, Opacity = 0 };
                T Find<T>(string name) where T : FrameworkElement => (T)dialog.FindName(name);
                dialog.Show();
                try
                {
                    check(!Find<Button>("GenerateButton").IsEnabled && Find<CheckBox>("IncludeSelection").IsChecked == true, "Generate waits for a request; the selection is offered and checked");
                    Find<TextBox>("Request").Text = "count invoices";
                    await dialog.GenerateAsync();
                    string sent = fake.LastBody!.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                    check(Find<TextBox>("Result").Text == "SELECT 7;" && Find<Button>("InsertButton").IsEnabled && inserted is null, "Generated SQL is shown for review and not inserted yet");
                    check(sent.Contains($"TABLE {schema}.invoices", StringComparison.Ordinal) && sent.Contains("<current_sql>\nSELECT * FROM invoices", StringComparison.Ordinal) && !sent.Contains(Secret, StringComparison.Ordinal) && dialog.SentContext!.Contains(sent, StringComparison.Ordinal),
                        "The provider receives the structure and the selected SQL, never table rows, and View Sent Context shows exactly that");
                    check(Find<TextBlock>("ContextInfo").Text.Contains("No table data", StringComparison.Ordinal), "The dialog states what was sent");
                    dialog.InsertResult();
                    check(dialog.WasInserted && inserted == "-- Generated by AI (Ollama, sqlcoder:7b). Review before running.\nSELECT 7;", "Insert hands the SQL to the editor with a review comment");
                }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }
            finally { await Sql($"DROP SCHEMA {schema} CASCADE"); }
            check(fake.SelectionPrompts.Count == 0, "A database that fits is sent whole, without a request to choose relations");

            // A database over the context limit: the provider picks from the names first, then gets those relations in detail.
            check(AiQueryGenerator.ParseSelection("```\n- `public.a`\n2. public.b -- why\n* \"S\".\"T\",\n\n</relations>\n```").SequenceEqual(["public.a", "public.b", "\"S\".\"T\""]), "Chosen names are read without list markers, backticks, comments or fences");
            string large = "ai_large_" + Guid.NewGuid().ToString("N")[..8];
            await Sql($"""
                CREATE SCHEMA {large};
                DO $$ BEGIN FOR i IN 0..59 LOOP EXECUTE format('CREATE TABLE {large}.t_%s (a int, b text)', lpad(i::text, 2, '0')); END LOOP; END $$;
                CREATE TABLE {large}.zz_wanted_customers (wanted_col int PRIMARY KEY, note text);
                COMMENT ON TABLE {large}.zz_wanted_customers IS 'People who buy';
                INSERT INTO {large}.zz_wanted_customers VALUES (1, '{Secret}');
                """);
            try
            {
                var dialog = new AiQueryDialog(ollama with { ContextCharacters = 2000 }, connection, null, _ => { }) { ShowInTaskbar = false, Opacity = 0 };
                dialog.Show();
                try
                {
                    ((TextBox)dialog.FindName("Request")).Text = "contar clientes";
                    await dialog.GenerateAsync();
                    string sent = fake.LastBody!.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
                    string asked = fake.SelectionPrompts.Count == 1 ? fake.SelectionPrompts[0] : "";
                    check(asked.Contains($"{large}.zz_wanted_customers -- People who buy\n", StringComparison.Ordinal) && asked.Contains($"{large}.t_59\n", StringComparison.Ordinal) && asked.Contains("<request>\ncontar clientes", StringComparison.Ordinal)
                        && !asked.Contains("wanted_col", StringComparison.Ordinal) && !asked.Contains(Secret, StringComparison.Ordinal), "The first request carries only names, table comments and the request");
                    check(sent.Contains($"TABLE {large}.zz_wanted_customers -- People who buy\n  wanted_col integer NOT NULL", StringComparison.Ordinal) && sent.Contains("more relations omitted for size", StringComparison.Ordinal) && !sent.Contains("made.up", StringComparison.Ordinal)
                        && !sent.Contains(Secret, StringComparison.Ordinal) && sent.Length < 2600 && ((TextBox)dialog.FindName("Result")).Text == "SELECT 7;", "The relation the provider chose is sent in detail although no word of the request matches it; invented names are dropped");
                    check(dialog.SentContext!.Contains("-- First request: choosing the relations", StringComparison.Ordinal) && dialog.SentContext.Contains(asked, StringComparison.Ordinal) && dialog.SentContext.Contains(sent, StringComparison.Ordinal)
                        && ((TextBlock)dialog.FindName("ContextInfo")).Text.Contains("Ollama chose 1 of them", StringComparison.Ordinal), "View Sent Context shows both requests and the dialog says the provider chose");
                }
                finally { if (dialog.IsVisible) dialog.Close(); }
            }
            finally { await Sql($"DROP SCHEMA {large} CASCADE"); }

            // The context limit is a setting of each provider.
            string settingsDirectory = Path.Combine(Path.GetTempPath(), "pgms-ai-limit-" + Guid.NewGuid().ToString("N")), settingsFile = Path.Combine(settingsDirectory, "ai-settings.json");
            try
            {
                Directory.CreateDirectory(settingsDirectory);
                File.WriteAllText(settingsFile, """{"Version":1,"ActiveProvider":"Ollama","Providers":[{"Provider":"Ollama","Model":"m","Endpoint":"http://localhost:11434"}]}""");
                var store = new AiSettingsStore(settingsFile);
                bool refused = false;
                try { (AiSettings.Default.For(AiProvider.Ollama) with { ContextCharacters = 100 }).Validate(); } catch (ArgumentException) { refused = true; }
                check(AiSettings.Default.For(AiProvider.Anthropic).ContextCharacters == 200_000 && store.Load().For(AiProvider.Ollama).ContextCharacters == 200_000 && refused, "The context limit defaults to 200,000, also for a settings file written before it existed, and has a minimum");
                var limits = new AiSettingsDialog(settingsFile) { ShowInTaskbar = false, Opacity = 0 };
                limits.Show();
                try
                {
                    var box = (TextBox)limits.FindName("ContextLimit");
                    bool shown = box.Text == "200000";
                    box.Text = "abc";
                    bool invalid = !limits.SaveSettings() && ((TextBlock)limits.FindName("Feedback")).Text.Contains("context limit between", StringComparison.Ordinal);
                    box.Text = "80.000";
                    check(shown && invalid && limits.SaveSettings() && store.Load().For(AiProvider.Ollama).ContextCharacters == 80_000 && store.Load().For(AiProvider.OpenAI).ContextCharacters == 200_000, "AI Assistant Settings shows, validates and saves the context limit of the provider");
                }
                finally { limits.Close(); }
            }
            finally { try { Directory.Delete(settingsDirectory, true); } catch (IOException) { } }

            var view = new QueryView { };
            var window = new Window { Content = view, ShowInTaskbar = false, Opacity = 0, Width = 600, Height = 400 }; window.Show();
            try
            {
                var editor = (ICSharpCode.AvalonEdit.TextEditor)view.FindName("Editor");
                editor.Text = "SELECT 1;"; editor.CaretOffset = editor.Text.Length;
                view.InsertGeneratedSql("SELECT 2;\nSELECT 3;");
                bool appended = editor.Text == "SELECT 1;" + Environment.NewLine + "SELECT 2;" + Environment.NewLine + "SELECT 3;" + Environment.NewLine && editor.SelectedText == "SELECT 2;" + Environment.NewLine + "SELECT 3;";
                editor.Select(0, "SELECT 1;".Length);
                view.InsertGeneratedSql("SELECT 9;");
                check(appended && editor.Text.StartsWith("SELECT 9;" + Environment.NewLine + "SELECT 2;", StringComparison.Ordinal) && editor.SelectedText == "SELECT 9;", "Insert goes on its own line at the caret or replaces the selection, and stays selected for review");
            }
            finally { window.Close(); }
        }
        finally { AiQueryGenerator.IsClaudeApi = original; }
    }

    // Stand-ins for the three chat endpoints; they record the last request.
    private sealed class FakeChat : IDisposable
    {
        private readonly HttpListener listener = new();
        public string Url { get; }
        public JsonDocument? LastBody { get; private set; }
        public string LastBeta { get; private set; } = "";
        public string LastAuthorization { get; private set; } = "";
        public List<string> SelectionPrompts { get; } = [];
        public FakeChat()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = $"http://localhost:{port}";
            listener.Prefixes.Add(Url + "/"); listener.Start();
            _ = Task.Run(LoopAsync);
        }
        private static string Message(string text, string stop) => JsonSerializer.Serialize(new
        {
            id = "msg_test", type = "message", role = "assistant", model = "claude-opus-5-5",
            content = new[] { new { type = "text", text } }, stop_reason = stop, stop_sequence = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 5 }
        });
        private async Task LoopAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception) { return; }
                string path = context.Request.Url!.AbsolutePath;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) LastBody = JsonDocument.Parse(await reader.ReadToEndAsync());
                LastBeta = context.Request.Headers["anthropic-beta"] ?? ""; LastAuthorization = context.Request.Headers["Authorization"] ?? "";
                var messages = LastBody.RootElement.GetProperty("messages");
                string request = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() ?? "";
                // The request that chooses relations: answer with the wanted table as listed, plus a name that does not exist.
                bool selecting = path == "/ollama/api/chat" && (messages[0].GetProperty("content").GetString() ?? "").Contains("decide which part", StringComparison.Ordinal);
                if (selecting) SelectionPrompts.Add(request);
                string wanted = System.Text.RegularExpressions.Regex.Match(request, @"^\S+\.zz_wanted_customers", System.Text.RegularExpressions.RegexOptions.Multiline).Value;
                (int status, string body) = path switch
                {
                    "/ollama/api/chat" when selecting => (200, JsonSerializer.Serialize(new { message = new { role = "assistant", content = $"- `{wanted}`\n- made.up" }, done = true })),
                    "/v1/messages" when request.Contains("refuse", StringComparison.Ordinal) => (200, Message("", "refusal")),
                    "/v1/messages" => (200, Message("```sql\nSELECT count(*) FROM public.t;\n```", "end_turn")),
                    "/v1/chat/completions" => (200, """{"choices":[{"message":{"role":"assistant","content":"```sql\nSELECT 42;\n```"}}]}"""),
                    "/ollama/api/chat" => (200, """{"message":{"role":"assistant","content":"```sql\nSELECT 7;\n```"},"done":true}"""),
                    _ => (404, "{}")
                };
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status; context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
        public void Dispose() { listener.Stop(); listener.Close(); }
    }
}

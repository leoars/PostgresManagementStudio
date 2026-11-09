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
                (int status, string body) = path switch
                {
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

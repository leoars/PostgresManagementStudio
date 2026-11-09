using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using PGMS.Desktop;

internal static class AiSettingsChecks
{
    private const string AnthropicKey = "sk-ant-test-0123456789", OpenAiKey = "sk-openai-test-9876543210";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pgms-ai-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(directory, "ai-settings.json");
        using var fake = new FakeProviders();
        try
        {
            var store = new AiSettingsStore(file);
            var defaults = store.Load();
            check(defaults.ActiveProvider == AiProvider.Anthropic && defaults.Providers.Count == 3 && defaults.Providers.All(p => !p.HasKey) && defaults.For(AiProvider.Anthropic).Model == "claude-opus-5-5" && defaults.For(AiProvider.Ollama).Endpoint == "http://localhost:11434", "Missing AI settings file reads as defaults");

            var anthropic = AiSettings.Default.For(AiProvider.Anthropic).WithKey(AnthropicKey);
            store.Save(AiSettings.Default.With(anthropic));
            string text = File.ReadAllText(file);
            var loaded = store.Load().For(AiProvider.Anthropic);
            check(!text.Contains(AnthropicKey, StringComparison.Ordinal) && !text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(AnthropicKey)), StringComparison.Ordinal) && loaded.RevealKey() == AnthropicKey, "API key is stored only as a DPAPI blob and reads back for this user");
            bool moved = false;
            try { (AiSettings.Default.For(AiProvider.OpenAI) with { ProtectedKey = loaded.ProtectedKey }).RevealKey(); } catch (CryptographicException) { moved = true; }
            check(moved, "A key blob copied to another provider does not decrypt");

            bool Refused(Func<object> action) { try { action(); return false; } catch (ArgumentException) { return true; } }
            check(Refused(() => (AiSettings.Default.For(AiProvider.OpenAI) with { Endpoint = "http://example.com/v1" }).Validate())
                && Refused(() => (AiSettings.Default.For(AiProvider.OpenAI) with { Endpoint = "https://example.com/v1?x=1" }).Validate())
                && Refused(() => AiSettings.Default.For(AiProvider.Anthropic).WithKey("has space"))
                && (AiSettings.Default.For(AiProvider.Ollama) with { Endpoint = "http://gpu-box:11434" }).Validate().Endpoint == "http://gpu-box:11434", "Keys never go over plain HTTP to another computer; Ollama may use a LAN address");

            // Other credentials of this Windows user must never replace the key saved in PGMS.
            string? envKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"), envToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
            IReadOnlyList<string> models;
            try
            {
                Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-environment-leak");
                Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", "environment-token-leak");
                models = await AiProviderClient.ListModelsAsync(loaded with { Endpoint = fake.Url });
            }
            finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", envKey); Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", envToken); }
            check(!fake.Seen.Any(s => s.Contains("leak", StringComparison.Ordinal)), "Environment credentials never replace the key saved in PGMS");
            check(models.SequenceEqual(["claude-opus-5-5", "claude-sonnet-5-5"]) && fake.Seen.Contains("anthropic:" + AnthropicKey), "Anthropic test lists models through the SDK with the saved key and no other credential");
            var openAi = AiSettings.Default.For(AiProvider.OpenAI).WithKey(OpenAiKey) with { Endpoint = fake.Url + "/v1" };
            check((await AiProviderClient.ListModelsAsync(openAi)).SequenceEqual(["gpt-test-a", "gpt-test-b"]) && fake.Seen.Contains("openai:Bearer " + OpenAiKey), "OpenAI test lists models with a bearer key");
            check((await AiProviderClient.ListModelsAsync(AiSettings.Default.For(AiProvider.Ollama) with { Endpoint = fake.Url + "/ollama" })).SequenceEqual(["llama-test:8b", "sqlcoder:7b"]), "Ollama test lists installed models without a key");
            string? rejected = null, anthropicRejected = null;
            try { await AiProviderClient.ListModelsAsync(AiSettings.Default.For(AiProvider.OpenAI).WithKey("sk-wrong") with { Endpoint = fake.Url + "/v1" }); } catch (AiProviderException ex) { rejected = ex.Message; }
            try { await AiProviderClient.ListModelsAsync(AiSettings.Default.For(AiProvider.Anthropic).WithKey("sk-ant-wrong") with { Endpoint = fake.Url }); } catch (AiProviderException ex) { anthropicRejected = ex.Message; }
            check(rejected is not null && rejected.Contains("rejected the API key", StringComparison.Ordinal) && !rejected.Contains("sk-wrong", StringComparison.Ordinal)
                && anthropicRejected is not null && anthropicRejected.Contains("rejected the API key", StringComparison.Ordinal) && !anthropicRejected.Contains("sk-ant-wrong", StringComparison.Ordinal), "Rejected keys give a short message that never echoes the key");

            File.Delete(file);
            var dialog = new AiSettingsDialog(file) { ShowInTaskbar = false, Opacity = 0 };
            T Find<T>(string name) where T : FrameworkElement => (T)dialog.FindName(name);
            try
            {
                dialog.Show();
                Find<ComboBox>("Provider").SelectedItem = AiProvider.Ollama;
                Find<TextBox>("Endpoint").Text = fake.Url + "/ollama";
                check(!Find<PasswordBox>("Key").IsEnabled && Find<TextBlock>("KeyStatus").Text.Contains("without an API key", StringComparison.Ordinal), "Ollama needs no key in the dialog");
                await dialog.TestAsync();
                check(Find<ComboBox>("Model").Items.Count == 2 && Find<TextBlock>("Feedback").Text.Contains("2 model", StringComparison.Ordinal), "Test Connection fills the model list");
                check(!dialog.SaveSettings() && Find<TextBlock>("Feedback").Text.Contains("model", StringComparison.OrdinalIgnoreCase), "Save requires a model");
                Find<ComboBox>("Model").Text = "sqlcoder:7b";

                Find<ComboBox>("Provider").SelectedItem = AiProvider.OpenAI;
                Find<PasswordBox>("Key").Password = OpenAiKey;
                Find<ComboBox>("Provider").SelectedItem = AiProvider.Ollama;
                check(Find<TextBox>("Endpoint").Text == fake.Url + "/ollama" && Find<ComboBox>("Model").Text == "sqlcoder:7b", "Switching providers keeps unsaved edits");
                check(dialog.SaveSettings() && store.Load().ActiveProvider == AiProvider.Ollama && store.Load().For(AiProvider.Ollama).Model == "sqlcoder:7b", "Saving makes the shown provider active");

                Find<ComboBox>("Provider").SelectedItem = AiProvider.OpenAI;
                check(Find<PasswordBox>("Key").Password == OpenAiKey && Find<TextBlock>("KeyStatus").Text == "No key saved.", "A typed key survives switching providers until saved");
                Find<TextBox>("Endpoint").Text = fake.Url + "/v1"; Find<ComboBox>("Model").Text = "gpt-test-a";
                check(dialog.SaveSettings() && Find<PasswordBox>("Key").Password.Length == 0 && store.Load().For(AiProvider.OpenAI).RevealKey() == OpenAiKey && Find<TextBlock>("KeyStatus").Text.StartsWith("A key is saved", StringComparison.Ordinal), "Save stores the key and clears the field");
                check(!File.ReadAllText(file).Contains(OpenAiKey, StringComparison.Ordinal), "The dialog never writes a key in clear text");
                dialog.RemoveSavedKey();
                check(!store.Load().For(AiProvider.OpenAI).HasKey && !Find<Button>("RemoveKeyButton").IsEnabled, "Remove Key deletes only the saved key");
                Find<PasswordBox>("Key").Password = "typed-but-not-saved";
            }
            finally { dialog.Close(); }
            check(((PasswordBox)dialog.FindName("Key")).Password.Length == 0, "Closing the dialog clears a typed key");

            File.WriteAllText(file, "{ not json");
            var broken = new AiSettingsDialog(file) { ShowInTaskbar = false, Opacity = 0 };
            try
            {
                broken.Show();
                check(!((Button)broken.FindName("SaveButton")).IsEnabled && ((TextBlock)broken.FindName("Feedback")).Text.Contains("could not be read", StringComparison.Ordinal) && File.ReadAllText(file) == "{ not json", "An unreadable settings file is reported and preserved");
            }
            finally { broken.Close(); }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    // Minimal stand-ins for the three list-models endpoints on localhost.
    private sealed class FakeProviders : IDisposable
    {
        private readonly HttpListener listener = new();
        public string Url { get; }
        public List<string> Seen { get; } = [];
        public FakeProviders()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = $"http://localhost:{port}";
            listener.Prefixes.Add(Url + "/"); listener.Start();
            _ = Task.Run(LoopAsync);
        }
        private async Task LoopAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch (Exception) { return; }
                // The Anthropic SDK sends the key as a bearer token (older clients used x-api-key); anthropic-version marks its requests.
                string path = context.Request.Url!.AbsolutePath, apiKey = context.Request.Headers["x-api-key"] ?? "", bearer = context.Request.Headers["Authorization"] ?? "";
                bool anthropic = context.Request.Headers["anthropic-version"] is not null;
                string anthropicKey = apiKey.Length > 0 ? apiKey : bearer.StartsWith("Bearer ", StringComparison.Ordinal) ? bearer[7..] : "";
                lock (Seen) Seen.Add((anthropic ? "anthropic:" : "openai:") + (anthropic ? anthropicKey : bearer));
                (int status, string body) = path switch
                {
                    "/v1/models" when anthropic && anthropicKey == AnthropicKey =>(200, """{"data":[{"id":"claude-sonnet-5-5","type":"model","display_name":"Sonnet","created_at":"2026-01-01T00:00:00Z"},{"id":"claude-opus-5-5","type":"model","display_name":"Opus","created_at":"2026-01-01T00:00:00Z"}],"has_more":false,"first_id":"claude-sonnet-5-5","last_id":"claude-opus-5-5"}"""),
                    "/v1/models" when !anthropic && bearer == "Bearer " + OpenAiKey => (200, """{"object":"list","data":[{"id":"gpt-test-b","object":"model"},{"id":"gpt-test-a","object":"model"}]}"""),
                    "/v1/models" when anthropic =>(401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""),
                    "/v1/models" => (401, """{"error":{"message":"Incorrect API key provided"}}"""),
                    "/ollama/api/tags" => (200, """{"models":[{"name":"sqlcoder:7b"},{"name":"llama-test:8b"}]}"""),
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

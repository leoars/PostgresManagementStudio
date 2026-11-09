using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Anthropic.Models.Messages;
using PGMS.PostgreSql;

namespace PGMS.Desktop;

public sealed record AiQueryResult(string Sql, string Provider, string Model);

/// <summary>Turns a request into PostgreSQL SQL. Sends only the request, the database structure and optional editor SQL; never table data.
/// The result is text for the editor; nothing here connects to the database or executes SQL.</summary>
public static partial class AiQueryGenerator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public const string Instructions = """
        You write PostgreSQL SQL for a user of a database client. The structure of the user's database is in <schema>.
        Use only schemas, tables, views, columns and types that exist in <schema>; when the request needs something that is not there, say so in a SQL comment instead of inventing names.
        Write SQL valid for the PostgreSQL version stated in <schema>. Quote identifiers only when required.
        Prefer read-only SELECT statements. Write statements that change data or structure only when the request explicitly asks for them, and then start the SQL with a comment line beginning with "-- WARNING:" that says what will change.
        When <current_sql> is present, the request is about that SQL (fix, change, extend or explain it).
        Reply with exactly one ```sql code block containing the complete SQL. Put any assumption or explanation inside it as short -- comments. Write nothing outside the block.
        """;

    // Server-side refusal fallback ("default" routing) for the models that accept it on the Claude API.
    internal static Func<Uri, bool> IsClaudeApi { get; set; } = uri => uri.Host == "api.anthropic.com"; // Tests point it at a local stand-in.
    private static readonly HashSet<string> FallbackModels = new(StringComparer.Ordinal) { "claude-opus-5-5", "claude-opus-5", "claude-fable-5-1", "claude-sonnet-5-5" };

    public static string Compose(string request, SchemaContext schema, string? currentSql)
    {
        var text = new StringBuilder();
        text.Append("<schema>\n").Append(schema.Text).Append("</schema>\n\n");
        if (!string.IsNullOrWhiteSpace(currentSql)) text.Append("<current_sql>\n").Append(currentSql.Trim()).Append("\n</current_sql>\n\n");
        text.Append("<request>\n").Append(request.Trim()).Append("\n</request>");
        return text.ToString();
    }

    public static async Task<AiQueryResult> GenerateAsync(AiProviderSettings settings, string prompt, CancellationToken token = default)
    {
        settings = settings.Validate();
        if (settings.Model.Length == 0) throw new AiProviderException("Choose a model in Tools ▸ AI Assistant Settings.");
        if (settings.NeedsKey && !settings.HasKey) throw new AiProviderException($"Save a {settings.Provider} API key in Tools ▸ AI Assistant Settings.");
        string reply = settings.Provider switch
        {
            AiProvider.Anthropic => await AnthropicAsync(settings, prompt, token).ConfigureAwait(false),
            AiProvider.OpenAI => await ChatAsync(settings, "/chat/completions", prompt, token).ConfigureAwait(false),
            _ => await ChatAsync(settings, "/api/chat", prompt, token).ConfigureAwait(false)
        };
        string sql = ExtractSql(reply);
        if (sql.Length == 0) throw new AiProviderException($"{settings.Provider} returned no SQL.");
        return new(sql, settings.Provider.ToString(), settings.Model);
    }

    /// <summary>The first ```sql (or bare ```) block; the whole reply when there is none.</summary>
    public static string ExtractSql(string reply)
    {
        var match = FencedBlock().Match(reply);
        return (match.Success ? match.Groups["sql"].Value : reply).Trim();
    }
    [GeneratedRegex(@"```(?:sql|postgresql|pgsql)?[ \t]*\r?\n(?<sql>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FencedBlock();

    private static async Task<string> AnthropicAsync(AiProviderSettings settings, string prompt, CancellationToken token)
    {
        // Only the key saved in PGMS, never another credential of this Windows user (see AiProviderClient).
        var client = new AnthropicClient { ApiKey = settings.RevealKey(), AuthToken = null, BaseUrl = settings.Endpoint };
        bool fallback = FallbackModels.Contains(settings.Model) && Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) && IsClaudeApi(uri);
        try
        {
            var parameters = new Anthropic.Models.Beta.Messages.MessageCreateParams
            {
                Model = settings.Model,
                MaxTokens = 16000,
                System = Instructions,
                Messages = [new BetaUserMessageParam { Content = prompt }],
            };
            if (fallback) parameters = parameters with { Betas = [AnthropicBeta.ServerSideFallback2026_07_01], Fallbacks = new Default() };
            var response = await client.Beta.Messages.Create(parameters, cancellationToken: token).ConfigureAwait(false);
            if (response.StopReason == "refusal")
                throw new AiProviderException("Anthropic declined this request" + (response.StopDetails?.Explanation is { Length: > 0 } why ? ": " + why : "."));
            if (response.StopReason == "max_tokens") throw new AiProviderException("The answer was cut off at the token limit. Ask for a smaller query.");
            return string.Concat(response.Content.Select(b => b.TryPickText(out var text) ? text.Text : ""));
        }
        catch (AnthropicUnauthorizedException ex) { throw new AiProviderException("Anthropic rejected the API key (HTTP 401).", ex); }
        catch (AnthropicForbiddenException ex) { throw new AiProviderException("The API key is not allowed to use this model (HTTP 403).", ex); }
        catch (AnthropicNotFoundException ex) { throw new AiProviderException($"Anthropic does not know the model \"{settings.Model}\" (HTTP 404).", ex); }
        catch (AnthropicRateLimitException ex) { throw new AiProviderException("Anthropic rate limit reached. Try again shortly.", ex); }
        catch (AnthropicBadRequestException ex) { throw new AiProviderException("Anthropic refused the request (HTTP 400). Check the model in AI Assistant Settings.", ex); }
        catch (Anthropic5xxException ex) { throw new AiProviderException("Anthropic is temporarily unavailable (HTTP 5xx). Try again later.", ex); }
        catch (AnthropicApiException ex) { throw new AiProviderException("Anthropic refused the request.", ex); }
        catch (AnthropicIOException ex) { throw new AiProviderException("Could not reach Anthropic at " + settings.Endpoint + ".", ex); }
    }

    private static async Task<string> ChatAsync(AiProviderSettings settings, string path, string prompt, CancellationToken token)
    {
        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = Instructions }, new JsonObject { ["role"] = "user", ["content"] = prompt })
        };
        if (settings.Provider == AiProvider.Ollama) body["stream"] = false;
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint + path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (settings.NeedsKey) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.RevealKey());
        try
        {
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new AiProviderException((int)response.StatusCode switch
                {
                    401 or 403 => $"{settings.Provider} rejected the API key (HTTP {(int)response.StatusCode}).",
                    404 => $"{settings.Provider} does not know the model \"{settings.Model}\" or the address is wrong (HTTP 404).",
                    429 => $"{settings.Provider} rate limit reached. Try again shortly.",
                    _ => $"{settings.Provider} answered HTTP {(int)response.StatusCode}."
                });
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            string? content = settings.Provider == AiProvider.Ollama
                ? root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var c) ? c.GetString() : null
                : root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("message", out var m) && m.TryGetProperty("content", out var oc) ? oc.GetString() : null;
            return content ?? throw new AiProviderException($"{settings.Provider} returned an unexpected answer.");
        }
        catch (HttpRequestException ex) { throw new AiProviderException($"Could not reach {settings.Provider} at {settings.Endpoint}: {ex.Message}", ex); }
        catch (JsonException ex) { throw new AiProviderException($"{settings.Provider} returned a response that is not JSON.", ex); }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested) { throw new AiProviderException($"{settings.Provider} did not answer within 3 minutes.", ex); }
    }
}

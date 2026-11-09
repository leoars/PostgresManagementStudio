using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;

namespace PGMS.Desktop;

/// <summary>Read-only provider calls. Listing models checks the key and address without generating text or spending tokens.</summary>
public static class AiProviderClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static async Task<IReadOnlyList<string>> ListModelsAsync(AiProviderSettings settings, CancellationToken token = default)
    {
        settings = settings.Validate();
        return settings.Provider switch
        {
            AiProvider.Anthropic => await ListAnthropicAsync(settings, token).ConfigureAwait(false),
            AiProvider.OpenAI => await ListAsync(settings, "/models", "data", "id", token).ConfigureAwait(false),
            _ => await ListAsync(settings, "/api/tags", "models", "name", token).ConfigureAwait(false)
        };
    }

    private static async Task<IReadOnlyList<string>> ListAnthropicAsync(AiProviderSettings settings, CancellationToken token)
    {
        // Only the key saved in PGMS: the default constructor also picks up environment variables and
        // `ant auth login` profiles, which would silently use another credential of this Windows user.
        var client = new AnthropicClient { ApiKey = settings.RevealKey(), AuthToken = null, BaseUrl = settings.Endpoint };
        try
        {
            var page = await client.Models.List(cancellationToken: token).ConfigureAwait(false);
            return page.Items.Select(m => m.ID).Order(StringComparer.Ordinal).ToArray();
        }
        // Most specific first; SDK messages can include request details, so only a short cause is shown.
        catch (AnthropicUnauthorizedException ex) { throw new AiProviderException("Anthropic rejected the API key (HTTP 401).", ex); }
        catch (AnthropicForbiddenException ex) { throw new AiProviderException("The API key is not allowed to list models (HTTP 403).", ex); }
        catch (AnthropicNotFoundException ex) { throw new AiProviderException("Anthropic address answered 404. Check the provider address.", ex); }
        catch (AnthropicRateLimitException ex) { throw new AiProviderException("Anthropic rate limit reached. Try again shortly.", ex); }
        catch (Anthropic5xxException ex) { throw new AiProviderException("Anthropic is temporarily unavailable (HTTP 5xx). Try again later.", ex); }
        catch (AnthropicApiException ex) { throw new AiProviderException("Anthropic refused the request.", ex); }
        catch (AnthropicIOException ex) { throw new AiProviderException("Could not reach Anthropic at " + settings.Endpoint + ".", ex); }
    }

    private static async Task<IReadOnlyList<string>> ListAsync(AiProviderSettings settings, string path, string array, string field, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.Endpoint + path);
        if (settings.NeedsKey) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.RevealKey());
        try
        {
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new AiProviderException((int)response.StatusCode switch
                {
                    401 or 403 => $"{settings.Provider} rejected the API key (HTTP {(int)response.StatusCode}).",
                    404 => $"{settings.Provider} address answered 404. Check the provider address.",
                    _ => $"{settings.Provider} answered HTTP {(int)response.StatusCode}."
                });
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty(array, out var items) || items.ValueKind != JsonValueKind.Array) throw new AiProviderException($"{settings.Provider} returned an unexpected model list.");
            return items.EnumerateArray().Select(i => i.TryGetProperty(field, out var v) ? v.GetString() : null).OfType<string>().Order(StringComparer.Ordinal).ToArray();
        }
        catch (HttpRequestException ex) { throw new AiProviderException($"Could not reach {settings.Provider} at {settings.Endpoint}: {ex.Message}", ex); }
        catch (JsonException ex) { throw new AiProviderException($"{settings.Provider} returned a response that is not JSON.", ex); }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested) { throw new AiProviderException($"{settings.Provider} did not answer within 20 seconds.", ex); }
    }
}

public sealed class AiProviderException(string message, Exception? inner = null) : Exception(message, inner);

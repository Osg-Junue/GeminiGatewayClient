using System.Text.Json;

namespace GeminiGatewayClient.ExploratoryTests.Infrastructure;

/// <summary>
/// Resolves exploratory test credentials and model settings strictly from environment variables.
/// Never stores or outputs raw secret values in logs or exception messages.
/// </summary>
public static class ExploratoryTestSettings
{
    public const string PrimaryApiKeyEnvVar = "GeminiAPIKey";
    public const string FallbackApiKeyEnvVar = "GEMINI_API_KEY";
    public const string ModelOverrideEnvVar = "GeminiTestModel";
    public const string FallbackModelOverrideEnvVar = "GEMINI_TEST_MODEL";
    public const string DefaultModel = "gemini-3-flash-preview";

    /// <summary>
    /// Gets a value indicating whether a Gemini API key is available in the current environment.
    /// </summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(TryGetApiKey(out _));

    /// <summary>
    /// Describes which environment variable source supplied the API key (without exposing the key value).
    /// </summary>
    public static string ApiKeySource
    {
        get
        {
            _ = TryGetApiKey(out var source);
            return source;
        }
    }

    /// <summary>
    /// Retrieves the API key from process or user environment variables, throwing if not configured.
    /// </summary>
    public static string GetRequiredApiKey()
    {
        var key = TryGetApiKey(out _);
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"No Gemini API key was found. Set the '{PrimaryApiKeyEnvVar}' user or process environment variable.");
        }

        return key;
    }

    /// <summary>
    /// Returns the configured model (<c>gemini-3-flash-preview</c> by default, or <c>GeminiTestModel</c> if set).
    /// </summary>
    public static string GetModel() =>
        TryReadEnvVar(ModelOverrideEnvVar, out _)
        ?? TryReadEnvVar(FallbackModelOverrideEnvVar, out _)
        ?? DefaultModel;

    /// <summary>
    /// Queries <c>GET /v1beta/models</c> to discover models that support <c>generateContent</c>.
    /// </summary>
    public static async Task<IReadOnlyList<DiscoveredModelInfo>> DiscoverGenerateContentModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var apiKey = GetRequiredApiKey();
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(GeminiClientOptions.DefaultEndpoint.ToString().TrimEnd('/') + "/models?pageSize=100"));
        request.Headers.TryAddWithoutValidation(GeminiClientOptions.DefaultApiKeyHeader, apiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("models", out var modelsElement) ||
            modelsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var discovered = new List<DiscoveredModelInfo>();
        foreach (var item in modelsElement.EnumerateArray())
        {
            var rawName = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                continue;
            }

            var supportsGenerateContent = false;
            if (item.TryGetProperty("supportedGenerationMethods", out var methodsProp) &&
                methodsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var method in methodsProp.EnumerateArray())
                {
                    if (string.Equals(method.GetString(), "generateContent", StringComparison.OrdinalIgnoreCase))
                    {
                        supportsGenerateContent = true;
                        break;
                    }
                }
            }

            if (!supportsGenerateContent)
            {
                continue;
            }

            var shortId = rawName.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? rawName["models/".Length..]
                : rawName;
            var displayName = item.TryGetProperty("displayName", out var dispProp) ? dispProp.GetString() : null;
            var inputTokenLimit = item.TryGetProperty("inputTokenLimit", out var inProp) && inProp.TryGetInt32(out var inVal)
                ? inVal
                : (int?)null;
            var outputTokenLimit = item.TryGetProperty("outputTokenLimit", out var outProp) && outProp.TryGetInt32(out var outVal)
                ? outVal
                : (int?)null;

            discovered.Add(new DiscoveredModelInfo(shortId, displayName, inputTokenLimit, outputTokenLimit));
        }

        return discovered;
    }

    private static string? TryGetApiKey(out string source)
    {
        if (TryReadEnvVar(PrimaryApiKeyEnvVar, out source) is { } primary)
        {
            return primary;
        }

        if (TryReadEnvVar(FallbackApiKeyEnvVar, out source) is { } fallback)
        {
            return fallback;
        }

        source = "None";
        return null;
    }

    private static string? TryReadEnvVar(string name, out string source)
    {
        var processValue = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(processValue))
        {
            source = $"Process:{name}";
            return processValue.Trim();
        }

        if (OperatingSystem.IsWindows())
        {
            var userValue = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(userValue))
            {
                source = $"User:{name}";
                return userValue.Trim();
            }
        }

        source = "None";
        return null;
    }
}

/// <summary>
/// Metadata for a Gemini model discovered via <c>GET /v1beta/models</c>.
/// </summary>
public sealed record DiscoveredModelInfo(
    string Id,
    string? DisplayName,
    int? InputTokenLimit,
    int? OutputTokenLimit);

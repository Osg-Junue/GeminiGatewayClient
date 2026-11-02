[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GeminiGatewayClient.ExploratoryTests.Infrastructure;

/// <summary>
/// Marks an exploratory xUnit <see cref="FactAttribute"/> that requires a live Gemini API key.
/// Automatically skips execution when <c>GeminiAPIKey</c> is not set (for example on GitHub Actions CI).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ExploratoryFactAttribute : FactAttribute
{
    private const string SkipMessage =
        "Skipped because environment variable 'GeminiAPIKey' is not set. Set 'GeminiAPIKey' to run live exploratory tests.";

    public ExploratoryFactAttribute()
    {
        if (!ExploratoryTestSettings.IsConfigured)
        {
            Skip = SkipMessage;
        }
    }
}

/// <summary>
/// Marks an exploratory xUnit <see cref="TheoryAttribute"/> that requires a live Gemini API key.
/// Automatically skips execution when <c>GeminiAPIKey</c> is not set (for example on GitHub Actions CI).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ExploratoryTheoryAttribute : TheoryAttribute
{
    private const string SkipMessage =
        "Skipped because environment variable 'GeminiAPIKey' is not set. Set 'GeminiAPIKey' to run live exploratory tests.";

    public ExploratoryTheoryAttribute()
    {
        if (!ExploratoryTestSettings.IsConfigured)
        {
            Skip = SkipMessage;
        }
    }
}


namespace GeminiGatewayClient.ExploratoryTests.Infrastructure;

/// <summary>
/// Shared collection definition for live Gemini API exploratory tests.
/// Runs sequentially and shares model discovery metadata across test classes.
/// </summary>
[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class ExploratoryTestCollection : ICollectionFixture<ExploratoryTestFixture>
{
    public const string CollectionName = "GeminiExploratoryTests";
}

/// <summary>
/// Resolves the target model (<c>gemini-3-flash-preview</c>) and fetches the model catalog once per test run.
/// </summary>
public sealed class ExploratoryTestFixture : IAsyncLifetime
{
    public IReadOnlyList<DiscoveredModelInfo> AvailableModels { get; private set; } = [];

    public string SelectedModel { get; private set; } = ExploratoryTestSettings.GetModel();

    public string? DiscoveryNote { get; private set; }

    public async Task InitializeAsync()
    {
        SelectedModel = ExploratoryTestSettings.GetModel();

        if (!ExploratoryTestSettings.IsConfigured)
        {
            DiscoveryNote = "API key not configured; model discovery skipped.";
            return;
        }

        try
        {
            AvailableModels = await ExploratoryTestSettings.DiscoverGenerateContentModelsAsync().ConfigureAwait(false);
            DiscoveryNote = $"Discovered {AvailableModels.Count} generateContent-capable models; using fixed model '{SelectedModel}'.";
        }
        catch (Exception ex)
        {
            DiscoveryNote = $"Using fixed model '{SelectedModel}' (catalog lookup note: {ex.GetType().Name}: {ex.Message}).";
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

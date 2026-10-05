namespace PoRedoMedia.Api.Common;

/// <summary>
/// The one switch for mock AI. When on, every provider is replaced by a mock, so the whole app
/// runs with no keys and spends nothing. Honoured in Development and Test; ignored in Production,
/// where serving canned output as if it were real would be a defect.
/// </summary>
public static class MockAi
{
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsDevOrTest() && configuration.GetValue<bool>(ConfigKeys.MocksUseMockAi);
}

namespace PoRedoMedia.Api.Common;

/// <summary>
/// The one switch for mock AI. When on, every provider is replaced by a mock, so the whole app
/// runs with no keys and spends nothing. Honoured only in Test (integration and E2E runs);
/// Development uses the real keys from Key Vault, and Production must never serve canned output.
/// </summary>
public static class MockAi
{
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment) =>
        environment.IsEnvironment(PoEnvironments.Test) && configuration.GetValue<bool>(ConfigKeys.MocksUseMockAi);
}

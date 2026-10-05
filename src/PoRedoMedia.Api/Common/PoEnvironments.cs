namespace PoRedoMedia.Api.Common;

/// <summary>
/// Local runs are Development, Azure is Production, and the integration and E2E tiers are Test.
/// </summary>
public static class PoEnvironments
{
    public const string Test = "Test";

    /// <summary>True for the local dev loop and for test runs, both of which use plain HTTP.</summary>
    public static bool IsDevOrTest(this IWebHostEnvironment env) =>
        env.IsDevelopment() || env.IsEnvironment(Test);
}

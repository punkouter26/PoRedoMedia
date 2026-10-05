using System.Diagnostics;
using Testcontainers.Azurite;

namespace PoRedoMedia.IntegrationTests;

/// <summary>One throwaway Azurite for the storage tests. Never the developer's compose instance.</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _container =
        new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithCommand("--skipApiVersionCheck")
            .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => DockerFactAttribute.DockerAvailable ? _container.StartAsync() : Task.CompletedTask;

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Azurite";
}

/// <summary>A fact that needs Docker. Without it the test is reported as skipped, never as passed.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public static bool DockerAvailable { get; } = Probe();

    public DockerFactAttribute()
    {
        if (!DockerAvailable)
            Skip = "Docker is not running.";
    }

    private static bool Probe()
    {
        try
        {
            using var docker = Process.Start(new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            return docker.WaitForExit(15_000) && docker.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>A theory that needs Docker.</summary>
public sealed class DockerTheoryAttribute : TheoryAttribute
{
    public DockerTheoryAttribute()
    {
        if (!DockerFactAttribute.DockerAvailable)
            Skip = "Docker is not running.";
    }
}
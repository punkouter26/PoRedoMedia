using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;

namespace PoRedoMedia.IntegrationTests;

[Collection(AzuriteCollection.Name)]
public sealed class RunsEndpointsTests(AzuriteFixture azurite) : IDisposable
{
    private readonly List<AppFactory> _factories = [];

    public void Dispose() => _factories.ForEach(f => f.Dispose());

    private AppFactory Factory(IRunStep step, int? dailyLimit = null)
    {
        var factory = new AppFactory(
            azurite.ConnectionString,
            services =>
            {
                // Only the stand-in: these tests are about the run engine, not any real function.
                services.RemoveAll<IRunStep>();
                services.AddSingleton(step);
            },
            dailyLimit is null ? null : new() { ["RenderQuota:DailyLimit"] = dailyLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        _factories.Add(factory);
        return factory;
    }

    private static async Task<MediaItem> AddSourceAsync(AppFactory factory, string user, MediaKind kind = MediaKind.Image)
    {
        var item = new MediaItem
        {
            Owner = new UserId(user), Id = MediaId.New(), Kind = kind, Status = MediaStatus.Ready, Origin = "Upload",
            Title = "source", ContentType = "image/png", Extension = ".png", CreatedAt = DateTimeOffset.UtcNow,
        };
        await factory.Services.GetRequiredService<IMediaRepository>().SaveAsync(item);
        return item;
    }

    private static Task<HttpResponseMessage> StartAsync(HttpClient client, MediaItem source, params MediaFunction[] functions) =>
        client.PostAsJsonAsync("/api/runs", new RunRequest(source.Id.Value, functions), WireJson.Default.RunRequest);

    private static async Task<RunDto> WaitForAsync(HttpClient client, Guid runId, RunStatus status)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var run = (await client.GetFromJsonAsync($"/api/runs/{runId}", WireJson.Default.RunDto))!;
            if (run.Status == status)
                return run;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Run {runId} did not reach {status}.");
    }

    private static async Task<int> UsedAsync(HttpClient client) =>
        (await client.GetFromJsonAsync("/api/quota", WireJson.Default.QuotaStatusDto))!.Used;

    [DockerFact]
    public async Task A_valid_run_is_accepted_executed_and_costs_one_credit()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var factory = Factory(new CopyStep(MediaFunction.MemeCaption));
        var client = await factory.SignedInAsync(user);
        var source = await AddSourceAsync(factory, user);

        var response = await StartAsync(client, source, MediaFunction.MemeCaption);
        var started = (await response.Content.ReadFromJsonAsync(WireJson.Default.RunDto))!;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var finished = await WaitForAsync(client, started.Id, RunStatus.Complete);
        Assert.Single(finished.OutputIds);
        Assert.Equal(1, await UsedAsync(client));
    }

    [DockerFact]
    public async Task A_refused_request_spends_nothing()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var factory = Factory(new CopyStep(MediaFunction.MemeCaption));
        var client = await factory.SignedInAsync(user);
        var image = await AddSourceAsync(factory, user);

        var wrongKind = await StartAsync(client, image, MediaFunction.Memeify);
        var noStep = await StartAsync(client, image, MediaFunction.Restyle);
        var empty = await StartAsync(client, image);
        var unknownSource = await client.PostAsJsonAsync("/api/runs", new RunRequest(Guid.NewGuid(), [MediaFunction.MemeCaption]), WireJson.Default.RunRequest);

        Assert.Equal(HttpStatusCode.BadRequest, wrongKind.StatusCode);
        Assert.Contains("cannot be applied to image", await wrongKind.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, noStep.StatusCode);
        Assert.Contains("Restyle is not available", await noStep.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownSource.StatusCode);
        Assert.Equal(0, await UsedAsync(client));
        Assert.Empty((await client.GetFromJsonAsync("/api/runs", WireJson.Default.ListRunDto))!);
    }

    [DockerFact]
    public async Task A_source_with_a_run_in_progress_refuses_a_second_one_without_charging()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var gate = new TaskCompletionSource();
        var factory = Factory(new CopyStep(MediaFunction.MemeCaption, gate.Task));
        var client = await factory.SignedInAsync(user);
        var source = await AddSourceAsync(factory, user);

        var first = (await (await StartAsync(client, source, MediaFunction.MemeCaption)).Content.ReadFromJsonAsync(WireJson.Default.RunDto))!;
        var second = await StartAsync(client, source, MediaFunction.MemeCaption);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await UsedAsync(client));
        gate.SetResult();
        await WaitForAsync(client, first.Id, RunStatus.Complete);
        Assert.Equal(HttpStatusCode.Accepted, (await StartAsync(client, source, MediaFunction.MemeCaption)).StatusCode);
    }

    [DockerFact]
    public async Task A_user_out_of_credit_is_refused_with_the_reset_time_and_nothing_runs()
    {
        var user = $"dev|{Guid.NewGuid()}";
        var step = new CopyStep(MediaFunction.MemeCaption);
        var factory = Factory(step, dailyLimit: 1);
        var client = await factory.SignedInAsync(user);
        var first = (await (await StartAsync(client, await AddSourceAsync(factory, user), MediaFunction.MemeCaption)).Content.ReadFromJsonAsync(WireJson.Default.RunDto))!;
        await WaitForAsync(client, first.Id, RunStatus.Complete);

        var refused = await StartAsync(client, await AddSourceAsync(factory, user), MediaFunction.MemeCaption);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("all 1 runs for today", await refused.Content.ReadAsStringAsync());
        Assert.Equal(1, step.Executions);
    }

    [DockerFact]
    public async Task The_owner_follows_a_run_live_and_nobody_else_can_join_or_read_it()
    {
        var owner = $"dev|owner-{Guid.NewGuid()}";
        var stranger = $"dev|stranger-{Guid.NewGuid()}";
        var gate = new TaskCompletionSource();
        var factory = Factory(new CopyStep(MediaFunction.MemeCaption, gate.Task));
        var client = await factory.SignedInAsync(owner);
        var run = (await (await StartAsync(client, await AddSourceAsync(factory, owner), MediaFunction.MemeCaption)).Content.ReadFromJsonAsync(WireJson.Default.RunDto))!;

        await using var ownerHub = Hub(factory, owner);
        await using var strangerHub = Hub(factory, stranger);
        var completed = new TaskCompletionSource<RunProgressDto>();
        var sawOutput = false;
        ownerHub.On<RunProgressDto>("RunProgress", p =>
        {
            sawOutput |= p.Output is not null;
            if (p.Status == RunStatus.Complete)
                completed.TrySetResult(p);
        });
        await ownerHub.StartAsync();
        await strangerHub.StartAsync();

        await ownerHub.InvokeAsync("JoinRun", run.Id.ToString());
        await Assert.ThrowsAsync<HubException>(() => strangerHub.InvokeAsync("JoinRun", run.Id.ToString()));
        Assert.Equal(HttpStatusCode.NotFound, (await (await factory.SignedInAsync(stranger)).GetAsync($"/api/runs/{run.Id}")).StatusCode);

        gate.SetResult();
        var last = await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(100, last.Percent);
        Assert.True(sawOutput);
    }

    private static HubConnection Hub(AppFactory factory, string user) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "hubs/run"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["X-Fake-User"] = user;
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, WireJson.Default))
            .Build();

    /// <summary>A stand-in function: saves a copy of the current item as its output.</summary>
    private sealed class CopyStep(MediaFunction function, Task? waitFor = null) : IRunStep
    {
        public int Executions { get; private set; }
        public IReadOnlySet<MediaFunction> Handles { get; } = new HashSet<MediaFunction> { function };

        public async Task ExecuteAsync(RunContext context, CancellationToken ct)
        {
            Executions++;
            if (waitFor is not null)
                await waitFor.WaitAsync(ct);
            await context.AddOutputAsync(context.Current with { Id = MediaId.New(), Origin = function.ToString(), ParentId = context.Current.Id });
        }
    }
}

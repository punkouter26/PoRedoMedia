using Microsoft.Extensions.Logging.Abstractions;
using PoRedoMedia.Api.Common;
using PoRedoMedia.Api.Features.Runs;
using PoRedoMedia.Shared.Enums;
using PoRedoMedia.Shared.Models;
using static PoRedoMedia.Shared.Enums.MediaFunction;

namespace PoRedoMedia.UnitTests;

public sealed class RunExecutorTests
{
    private static readonly UserId Owner = new("dev|runner");
    private readonly FakeMedia _media = new();
    private readonly FakeRuns _runs = new();
    private readonly FakeNotifier _notifier = new();
    private readonly List<string> _calls = [];

    private MediaItem NewMedia(MediaKind kind = MediaKind.Image, string origin = "Upload") => _media.Add(new MediaItem
    {
        Owner = Owner, Id = MediaId.New(), Kind = kind, Status = MediaStatus.Ready, Origin = origin,
        Title = origin, ContentType = "x/y", Extension = ".x", CreatedAt = DateTimeOffset.UtcNow,
    });

    private Run NewRun(MediaItem source, params MediaFunction[] functions) =>
        new() { Owner = Owner, Id = RunId.New(), SourceId = source.Id, Functions = functions, CreatedAt = DateTimeOffset.UtcNow };

    private RunExecutor Executor(params IRunStep[] steps) =>
        new(steps, _runs, _media, _notifier, NullLogger<RunExecutor>.Instance);

    private FakeStep Step(MediaFunction function, Func<RunContext, Task>? work = null) =>
        new([function], async context =>
        {
            _calls.Add(function.ToString());
            if (work is not null)
                await work(context);
        });

    [Fact]
    public async Task Steps_run_in_the_fixed_order_and_each_works_on_the_previous_result()
    {
        var source = NewMedia();
        MediaItem? seenByCaption = null, seenByVideo = null;
        var restyled = NewMedia(origin: "Restyle");
        var captioned = NewMedia(origin: "MemeCaption");
        var executor = Executor(
            Step(PhotoToVideo, c => { seenByVideo = c.Current; return c.AddOutputAsync(NewMedia(MediaKind.Video, "PhotoToVideo")); }),
            Step(MemeCaption, c => { seenByCaption = c.Current; return c.AddOutputAsync(captioned, becomesCurrent: true); }),
            Step(Restyle, c => c.AddOutputAsync(restyled, becomesCurrent: true)));

        await executor.ExecuteAsync(NewRun(source, PhotoToVideo, MemeCaption, Restyle), default);

        Assert.Equal(["Restyle", "MemeCaption", "PhotoToVideo"], _calls);
        Assert.Equal(restyled.Id, seenByCaption!.Id);
        Assert.Equal(captioned.Id, seenByVideo!.Id);
        var run = Assert.Single(_runs.All);
        Assert.Equal((RunStatus.Complete, 3, null), (run.Status, run.OutputIds.Count, run.Error));
        Assert.Equal(RunStatus.Complete, _notifier.Events[^1].Status);
        Assert.Equal(3, _notifier.Events.Count(e => e.Output is not null));
    }

    [Fact]
    public async Task A_failing_step_stops_the_chain_keeps_earlier_results_and_says_why()
    {
        var source = NewMedia();
        var executor = Executor(
            Step(Restyle, c => { c.AddNote("Used a fallback description."); return c.AddOutputAsync(NewMedia(origin: "Restyle"), becomesCurrent: true); }),
            Step(MemeCaption, _ => throw new RunStepException("The image model declined this photo.")),
            Step(RapRoast));

        await executor.ExecuteAsync(NewRun(source, Restyle, MemeCaption, RapRoast), default);

        Assert.Equal(["Restyle", "MemeCaption"], _calls);
        var run = Assert.Single(_runs.All);
        Assert.Equal((RunStatus.Failed, MemeCaption, "The image model declined this photo."), (run.Status, run.CurrentStep, run.Error));
        Assert.Single(run.OutputIds);
        Assert.Equal(["Used a fallback description."], run.Notes);
    }

    [Fact]
    public async Task An_unexpected_error_is_reported_without_leaking_its_details()
    {
        var executor = Executor(Step(Restyle, _ => throw new InvalidOperationException("connection string=secret")));

        await executor.ExecuteAsync(NewRun(NewMedia(), Restyle), default);

        Assert.Equal("Restyle failed unexpectedly.", Assert.Single(_runs.All).Error);
    }

    [Fact]
    public async Task A_step_that_handles_several_functions_runs_once_for_all_of_them()
    {
        var video = new FakeStep([Memeify, VideoRoast, Captions], _ => { _calls.Add("video"); return Task.CompletedTask; });

        await Executor(video).ExecuteAsync(NewRun(NewMedia(MediaKind.Video), Memeify, VideoRoast, Captions), default);

        Assert.Equal(["video"], _calls);
        Assert.Equal(RunStatus.Complete, Assert.Single(_runs.All).Status);
    }

    [Fact]
    public async Task A_run_whose_source_was_deleted_fails_with_that_reason()
    {
        var source = NewMedia();
        await _media.DeleteAsync(Owner, source.Id);

        await Executor(Step(Restyle)).ExecuteAsync(NewRun(source, Restyle), default);

        Assert.Empty(_calls);
        Assert.Contains("deleted", Assert.Single(_runs.All).Error);
    }

    [Fact]
    public void A_source_can_hold_only_one_place_in_the_queue()
    {
        var dispatcher = new RunDispatcher(Executor(), NullLogger<RunDispatcher>.Instance);
        var source = MediaId.New();

        Assert.True(dispatcher.TryReserve(source));
        Assert.False(dispatcher.TryReserve(source));
        Assert.True(dispatcher.TryReserve(MediaId.New()));
        dispatcher.Release(source);
        Assert.True(dispatcher.TryReserve(source));
    }

    private sealed class FakeStep(MediaFunction[] handles, Func<RunContext, Task> work) : IRunStep
    {
        public IReadOnlySet<MediaFunction> Handles { get; } = handles.ToHashSet();
        public Task ExecuteAsync(RunContext context, CancellationToken ct) => work(context);
    }

    private sealed class FakeNotifier : IRunNotifier
    {
        public List<RunProgressDto> Events { get; } = [];
        public Task ProgressAsync(RunProgressDto progress, CancellationToken ct = default) { Events.Add(progress); return Task.CompletedTask; }
    }

    private sealed class FakeRuns : IRunRepository
    {
        private readonly Dictionary<RunId, Run> _rows = [];
        public IReadOnlyCollection<Run> All => _rows.Values;
        public Task SaveAsync(Run run, CancellationToken ct = default) { _rows[run.Id] = run; return Task.CompletedTask; }
        public Task<Run?> GetAsync(UserId owner, RunId id, CancellationToken ct = default) => Task.FromResult(_rows.GetValueOrDefault(id));
        public Task<IReadOnlyList<Run>> ListAsync(UserId owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Run>>([.. _rows.Values]);
    }

    private sealed class FakeMedia : IMediaRepository
    {
        private readonly Dictionary<MediaId, MediaItem> _rows = [];
        public MediaItem Add(MediaItem item) { _rows[item.Id] = item; return item; }
        public Task SaveAsync(MediaItem item, CancellationToken ct = default) { _rows[item.Id] = item; return Task.CompletedTask; }
        public Task<MediaItem?> GetAsync(UserId owner, MediaId id, CancellationToken ct = default) => Task.FromResult(_rows.GetValueOrDefault(id));
        public Task<IReadOnlyList<MediaItem>> ListAsync(UserId owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MediaItem>>([.. _rows.Values]);
        public Task DeleteAsync(UserId owner, MediaId id, CancellationToken ct = default) { _rows.Remove(id); return Task.CompletedTask; }
    }
}

# PoRedoMedia — Plan

## A1. What exploration changed

| Finding | Consequence |
|---|---|
| Meme-ify is not optional in PoMemeVideo; every run calls the director | `RunEngineCommand` is split so placement and director run only when Meme-ify is ticked. The renderer already handles zero cues, roast-only and captions-only |
| PoRedoImage sends every image inline as base64 and saves results from the client | New: media is uploaded once, stored, and referenced by id. The server saves results |
| PoRedoImage does not store Veo videos or roast audio at all | New: the gallery stores images, videos and audio |
| Bulk streams NDJSON; Veo is polled by the browser | Both become server-side run steps reporting over SignalR, one mechanism for everything |
| Most PoRedoImage AI endpoints are anonymous with no antiforgery | Every PoRedoMedia endpoint is authenticated; every write group has the antiforgery filter |
| PoRedoImage mock AI only works in the Test environment; PoMemeVideo's mock returns `[]` and disables roast | One gate, allowed in Development and Test, with real mock services for every provider so all journeys run without keys **(spec)** |
| The video renderer reads sticker PNGs from the Client project's `wwwroot` | Stickers move into the Api project as content files |
| The public share page links the retro stylesheets and shows remix | Share page is rewritten as a small self-contained page for image or video |
| Captions need a Whisper deployment and a font on the Linux host | Captions offered only when configured; font presence is checked at deploy |
| Radzen 12 marks dynamic LINQ `[RequiresUnreferencedCode]`; the build treats trim warnings as errors | Checked in spike T05 before any UI is built |
| PoRedoImage's CLAUDE.md describes a CI `full-test` job that does not exist in its workflow | Nothing is copied from that workflow; CI follows PoMemeVideo's |

Restyle is ported as is: describe the photo, then generate from the description (your choice).

## A2. Architecture decisions

**AD1 — Host.** `PoRedoMedia.Api` is a Blazor Web App host: `AddRazorComponents()
.AddInteractiveWebAssemblyComponents().AddAuthenticationStateSerialization()`, with a host-only
`Components/App.razor` rendering the Client's routes with no prerender. Ported from
`PoRedoImage/src/PoRedoImage.Web/Program.cs`, including the `/_framework` allow-anonymous
middleware.

**AD2 — One media store.** Table `Media` (PK user id, RK media id) and blob prefix
`media/{mediaId}/` holding `source`, `thumb.jpg` and analysis files. `MediaItem` has `Kind`
(Image, Video, Audio), `Origin` (Upload or the function that made it), `ParentId`, `Title`,
`Pinned`, `ShareToken`. Replaces `UserImage` and the output half of `VideoSession`.

**AD3 — One upload path.** Images and videos both upload direct to blob with a short-lived SAS
(`POST /api/media/sas`, browser `PUT`, `POST /api/media/{id}/confirm`). Confirm validates by
content: ImageSharp `Identify` for images, ffprobe for videos. Ported from
`PoMemeVideo/.../Ingestion` and `Client/Services/BlobUploadService.cs`.

**AD4 — Analysis belongs to the media, not the run.** Frame vision labels, the audio envelope and
the transcript are stored under `media/{mediaId}/` and reused by every later run on that video, so
a re-run does not pay for vision or Whisper again.

**AD5 — One run engine.** Table `Runs` (PK user id, RK run id): source media, function set,
options JSON, status, current step, error, output media ids. `RunDispatcher` is PoMemeVideo's
`EngineRunDispatcher` (channel, one consumer, per-source lane) generalised: it resolves an
`IRunStep` per ticked function. Image steps run in the fixed order; video functions are handled
by one `VideoRunStep` that ends in one render. Progress goes out on `RunHub` (`/hubs/run`).

**AD6 — Stack rules are shared code.** `FunctionStack.Validate(kind, functions)` lives in
`PoRedoMedia.Shared`, so the Create page and the server use the same rules.

**AD7 — AI clients.** Video keeps PoMemeVideo's `AiFoundryClient` unchanged: it carries learned
fallbacks for schema, temperature and token-cap rejections. Image-side chat moves to
`Microsoft.Extensions.AI` `IChatClient` (Azure OpenAI and Ollama), replacing PoRedoImage's
`IChatCompletionService` and its two implementations. Vision providers, the vision router, Gemini
image, Veo and Lyria are ported as they are. Lyria is one client used by both roasts.

**AD8 — Caching and resilience.** PoRedoImage's caching decorators are kept but backed by
`HybridCache` in place of the private `MemoryCache`. Google HTTP clients keep the standard
resilience handler (2 min attempt, 5 min total, no retry on timeout). Veo keeps its own 15 min
timeout and no retries.

**AD9 — Mock AI.** `Mocks:UseMockAi` is honoured in Development and Test and refused in
Production. It swaps in mock services for vision, chat, image generation, music, video
generation, frame vision, director, transcription and roast voice. PoMemeVideo's two test mocks
move into `src`. A banner shows when mocks are active. If no keys resolve and the flag is off,
the affected functions are shown as unavailable with the reason.

**AD10 — Security.** Fallback policy requires an authenticated user. Anonymous endpoints are an
explicit list pinned by a test: health, auth, antiforgery token, the share page and its media,
static assets. Every `MapGroup` with a write calls `.RequireAntiforgeryValidation()`. `RunHub`
admits only the run's owner.

**AD11 — Quota.** One credit per run, default 10 per user per UTC day **(spec Q2)**. The lane is
reserved before the credit is spent; an invalid stack spends nothing.

**AD12 — Tests.** Four projects. bUnit component tests live in `UnitTests`. `IntegrationTests`
uses one Testcontainers Azurite for storage round trips and `WebApplicationFactory` with mock AI
for endpoints. `E2EAPI` runs the full HTTP stack with mocks. `E2EUI` is Playwright against a
running instance and includes one Axe scan. Budgets are counted in test cases by
`scripts/check-test-budgets.ps1` (ported). Tests that need Docker or a live server report as
skipped, never as passed, when it is missing.

**AD13 — Mobile.** The MAUI head is ported after the web app is complete. It uploads by SAS,
creates a run and polls `GET /api/runs/{id}`; it does not use SignalR.

**AD14 — Not carried over from the old clients.** PoRedoImage's flap-board look and sound effects
(`fx.js`, `audio.js`, `FlapText`, `BoardStatus`) and all PoMemeVideo retro components. The Phase 3
design decides the new look.

## A3. Dependency graph

```
T01-T05 foundation ──> T06-T09 auth/shell ──> T10-T15 media ──> T16-T19 runs
                                                                    │
              ┌─────────────────────────────────────────────────────┤
              v                                                     v
   T20-T32 image functions                              T33-T44 video functions
              └───────────────────────┬─────────────────────────────┘
                                      v
                 T45-T47 sharing, sounds page, housekeeping
                                      v
                 T48-T53 model picker, local AI, webcam, PWA, a11y
                                      v
                 T54-T56 mobile ──> T57-T59 CI and deploy ──> T60 verify
```

Image and video milestones depend only on runs, not on each other.

## A4. Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | `Microsoft.Extensions.AI.OpenAI` 10.10.1 needs a newer OpenAI SDK than `Azure.AI.OpenAI` 2.1.0 allows | Medium | High | T05 result: resolves to OpenAI 2.14.0; a chat request round-trips through a stub transport. Still open for `AiFoundryClient`'s direct SDK calls, which get the same stub test in T35 |
| R2 | Radzen 12 trim warnings break the build | Medium | Medium | T05 result: a filtering `RadzenDataGrid` builds clean under the analyzer. The analyzer is off for the Api project, which is never trimmed |
| R3 | A model id copied from old settings has been retired | Medium | Medium | Each provider task starts by checking the id against the provider's docs; changes are reported |
| R4 | Image generation, Veo waits and FFmpeg share the F1 plan's 60 CPU-min/day | High | High | Quota default 10; one run at a time per process; documented. A paid plan is your call |
| R5 | F1 recycles after ~20 idle minutes and loses an in-flight run | Medium | Medium | Housekeeping marks it interrupted; retry is free |
| R6 | Splitting `RunEngineCommand` (632 lines) breaks placement or timing | Medium | High | Its 22 existing unit tests are ported first and must stay green through the split |
| R7 | No Whisper deployment exists, so captions cannot be proven against a real provider | High | Low | Built and tested against the mock; success criterion 5 notes it as unproven until configured |
| R8 | Caption font missing on the Linux host | Medium | Medium | Deploy workflow asserts the font file exists, or bundles one |
| R9 | The MAUI head cannot be built or run on this machine (Android SDK, emulator) | Medium | Medium | T54 begins by checking the SDK; if absent I ask before installing |
| R10 | About 60 tasks is several long sessions | High | Medium | Checkpoints every 2–3 tasks; each milestone is usable on its own |
| R11 | bUnit and Radzen 12 components need JS interop stubs | Medium | Low | bUnit loose JS mode; fall back to Playwright for the affected component |

## A5. Checkpoints

A checkpoint is: full suite green, build clean, app starts and `/health` answers, then a one-line
status to you. I continue without waiting unless something in "ask first" applies.

| After | Demonstrates |
|---|---|
| T05 | Solution builds; spikes for R1 and R2 resolved |
| T09 | Sign in, empty shell, deny-by-default proven |
| T15 | Upload an image and a video; both appear in the gallery |
| T19 | A run can be created, validated, quota-checked and queued |
| T23 | **J1 (meme caption) end to end with mocks** |
| T27, T32 | All five image functions; stacked image run |
| T38, T41 | Video analysis and director; then **J2 end to end with mocks** |
| T44 | All three video functions from the UI |
| T47 | Share links, sounds page, retention |
| T53 | Model picker, local AI, webcam, PWA, accessibility |
| T56 | Android app completes J1 |
| T59 | Deployed and healthy |

## A6. Ten implementation examples for the chosen libraries

These show intent. Each is compiled for real in the task named beside it.

**1. Radzen 12 — the function stack (T23).** The Create page binds a multi-select list to the
shared rules, so invalid combinations are disabled as the user ticks.

```razor
<RadzenSteps @bind-SelectedIndex="_step">
  <Steps>
    <RadzenStepsItem Text="Media"><MediaPicker @bind-Value="_source" /></RadzenStepsItem>
    <RadzenStepsItem Text="Functions" Disabled="@(_source is null)">
      <RadzenCheckBoxList @bind-Value="_picked" TValue="MediaFunction" Orientation="Orientation.Vertical">
        <Items>
          @foreach (var f in FunctionStack.For(_source!.Kind))
          {
            <RadzenCheckBoxListItem Value="f" Text="@f.Label()"
                                    Disabled="@(!FunctionStack.CanAdd(_picked, f))" />
          }
        </Items>
      </RadzenCheckBoxList>
    </RadzenStepsItem>
    <RadzenStepsItem Text="Run" Disabled="@(!_picked.Any())" />
  </Steps>
</RadzenSteps>
```

**2. Radzen 12 — gallery with dialog and notification (T15).**

```razor
<RadzenDataList Data="_items" TItem="MediaDto" WrapItems="true" AllowPaging="true" PageSize="24">
  <Template Context="m">
    <RadzenCard @onclick="@(() => Dialogs.OpenAsync<MediaDetail>(m.Title,
        new() { ["Media"] = m }, new DialogOptions { Width = "min(960px, 96vw)" }))">
      <RadzenImage Path="@m.ThumbUrl" AlternateText="@m.Title" />
      <RadzenBadge Text="@m.Kind.ToString()" />
    </RadzenCard>
  </Template>
</RadzenDataList>
```

**3. SignalR — run progress into Radzen (T19, T23).**

```csharp
_hub = new HubConnectionBuilder().WithUrl(Nav.ToAbsoluteUri("/hubs/run")).WithAutomaticReconnect().Build();
_hub.On<RunProgressDto>("RunProgress", p => { _progress = p; InvokeAsync(StateHasChanged); });
await _hub.StartAsync();
await _hub.InvokeAsync("JoinRun", runId);
```
```razor
<RadzenProgressBar Value="@_progress.Percent" /> <RadzenText>@_progress.StepLabel</RadzenText>
```

**4. Microsoft.Extensions.AI — one chat client, structured output (T24).**

```csharp
services.AddSingleton<IChatClient>(sp => ollamaModel is { Length: > 0 }
    ? new OllamaApiClient(ollamaEndpoint, ollamaModel)                       // dev only
    : new AzureOpenAIClient(endpoint, new AzureKeyCredential(key))
          .GetChatClient(deployment).AsIChatClient());

var caption = await chat.GetResponseAsync<MemeCaption>(
    [new(ChatRole.System, CaptionPrompt.System), new(ChatRole.User, description)], cancellationToken: ct);
```

**5. HybridCache — never pay twice for the same image and provider (T25).**

```csharp
public ValueTask<VisionResult> AnalyzeAsync(byte[] image, CancellationToken ct) =>
    cache.GetOrCreateAsync($"vision:{scope}:{Convert.ToHexString(SHA256.HashData(image))}",
        async c => await inner.AnalyzeAsync(image, c),
        new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(6) }, cancellationToken: ct);
```

**6. Http.Resilience — Google clients (T27).**

```csharp
services.AddHttpClient("GeminiApi", c => c.BaseAddress = new("https://generativelanguage.googleapis.com/"))
    .AddStandardResilienceHandler(o =>
    {
        o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(2);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
        o.Retry.MaxRetryAttempts = 2;
    });
```

**7. ImageSharp — validate an upload by content and make its thumbnail (T13, T14).**

```csharp
var info = await Image.IdentifyAsync(stream, ct) ?? throw new InvalidMediaException("Not an image.");
stream.Position = 0;
using var img = await Image.LoadAsync(stream, ct);
img.Mutate(x => x.Resize(new ResizeOptions { Size = new(480, 480), Mode = ResizeMode.Max }));
await img.SaveAsJpegAsync(thumb, ct);
```

**8. FFmpeg — one render for the whole stack (T40, T41).** `FFmpegArgs.BuildFFmpegArgs` already
takes cues, roast voices and subtitles in one call; the step calls it once.

```csharp
var job = new RenderJob(source, cues: memeify ? script.Entries : [], voices: roast?.Lines ?? [],
                        subtitles: captions ? transcript : null, aspect, aggressive);
var seconds = await render.RenderAsync(job, ct);          // exactly one ffmpeg process
```

**9. Testcontainers — real storage in integration tests (T10).**

```csharp
public sealed class AzuriteFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _c =
        new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest").Build();
    public string ConnectionString => _c.GetConnectionString();
    public Task InitializeAsync() => _c.StartAsync();
    public Task DisposeAsync() => _c.DisposeAsync().AsTask();
}
```

**10. bUnit and Axe — the UI without and with a browser (T23, T53).**

```csharp
[Fact] public void Bulk_disables_every_other_function()
{
    using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    var cut = ctx.Render<FunctionPicker>(p => p.Add(x => x.Kind, MediaKind.Image));
    cut.Find("[data-fn=BulkStyles] input").Change(true);
    Assert.All(cut.FindAll("[data-fn]:not([data-fn=BulkStyles]) input"), i => Assert.True(i.HasAttribute("disabled")));
}

var result = await page.RunAxe(new AxeRunOptions { RunOnly = new() { Type = "tag", Values = ["wcag2aa", "wcag22aa"] } });
Assert.Empty(result.Violations);
```

Coverage is measured with `dotnet test --collect:"XPlat Code Coverage"` (coverlet). MinVer reads
the version from git tags; the first tag `v0.1.0` is created when you approve the first deploy.

## A8. Chosen design and component hierarchy

Create follows concept 1 (guided steps), chosen 2026-10-05 in place of concept 3: a RadzenSteps
wizard of Media, then Functions (preview beside the function checkboxes and their options), then
Run (summary, progress, results). The Create subtree below is superseded by that; the component
names are unchanged. Gallery follows concept 4 (grid with side panel) and is built.
Canvas: https://claude.ai/artifact/PjhKguLtMJmSNZeTCNLkiE

```
MainLayout                      RadzenLayout, RadzenHeader, RadzenBody
├─ header                       brand, links (Create, Gallery, Sounds), QuotaBadge,
│                               SessionCostChip, RadzenProfileMenu (name, sign out)
├─ MockDataBanner               shown only when mock AI is active
└─ hosts                        RadzenDialog, RadzenNotification, RadzenTooltip

/login      Login
/           Create
            ├─ MediaPicker      drop zone (RadzenCard + InputFile), Choose file, Webcam,
            │                   Gallery (opens GalleryPickerDialog); paste and drop
            ├─ FunctionPicker   one toggle tile per function valid for the media kind;
            │                   tiles that cannot be added are disabled (FunctionStack)
            ├─ FunctionOptions  one RadzenPanel per ticked function:
            │                   RestyleOptions, MemeCaptionOptions, RapRoastOptions,
            │                   PhotoToVideoOptions, BulkPrompts, VideoOptions
            ├─ RunBar           selected count, run order, AI models (AiServicePicker in a
            │                   dialog), Make it
            ├─ RunProgress      RadzenProgressBar and current step, from RunHub
            └─ RunResult        MediaDetail, or BulkBoard, or RoastPlayer
/gallery    Gallery
            ├─ filter           RadzenSelectBar (All, Images, Videos, Audio), search box
            ├─ RadzenDataList   of MediaCard (thumbnail, kind badge, title)
            └─ MediaPanel       side panel for the selected item: preview, FunctionPicker
                                and Run (same components as Create), Share (ShareDialog),
                                Download, Pin, Delete
/sounds     Sounds              RadzenDataGrid, SoundPlayButton, upload dialog
/v/{token}  share page          server-rendered, not part of the WASM app
```

`FunctionPicker`, `FunctionOptions` and `RunProgress` are written once and used by both Create
and the Gallery side panel.

## A7. Package versions added by this plan

| Package | Version | Verified |
|---|---|---|
| Radzen.Blazor | 12.0.5 | nuget.org, 2026-10-05 |
| Microsoft.Extensions.AI / .OpenAI | 10.10.0 / 10.10.1 | nuget.org, 2026-10-05 |
| bunit | 2.11.3 | nuget.org, 2026-10-05 |
| Testcontainers.Azurite | 4.14.0 | nuget.org, 2026-10-05 |
| OllamaSharp, coverlet.collector, Deque.AxeCore.Playwright, MinVer, Http.Resilience, Caching.Hybrid | latest stable at T01/T05 | checked then; recorded in `SPEC.md` §3 |

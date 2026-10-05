# PoRedoMedia — Specification

Status: approved 2026-10-05, updated with Phase 2 decisions · Companions:
[CAPABILITY-MAP.md](CAPABILITY-MAP.md), [tasks/plan.md](tasks/plan.md), [tasks/todo.md](tasks/todo.md)

## 1. Objective

PoRedoMedia is a private media studio. A signed-in user picks one image or one video, ticks the
functions to apply to it, and runs them as a single job. The result lands in the user's gallery
and can be downloaded, shared by link, or used as the source of the next run.

It replaces two apps, PoRedoImage and PoMemeVideo, by combining their existing functions behind
one workflow. No new AI function is added in this round.

## 2. User journeys

**J1 — Meme an image.** Sign in → Create → drop a photo → the page shows the image functions →
tick *Restyle* and *Meme caption* → Run → progress shows each step → the captioned image appears
and is saved to the gallery.

**J2 — Meme a video.** Create → drop a video (≤ 10 min) → tick *Meme-ify*, *Insult roast* (pick a
voice) and *Auto-captions* → Run → one render → the finished MP4 plays and is saved. SRT is
downloadable when captions were on.

**J3 — Chain.** Gallery → open an image → *Use as source* → tick *Photo → video* → Run → the
8-second clip is saved → *Use as source* again → tick *Meme-ify* → Run.

**J4 — Bulk.** Create → drop a photo → tick *Bulk styles* (other functions become unavailable) →
Run → up to 10 variations stream into a compare board → save the ones to keep.

**J5 — Share.** Gallery → open an item → Share → copy the `/v/{token}` link. An anonymous visitor
opens it and sees the image or video. The owner can revoke the link.

**J6 — Sounds.** Sounds page → search, audition, star a sound, upload a new one. Starred and
uploaded sounds are available to Meme-ify.

**J7 — Choose models.** On Create, open the model picker → choose a provider per capability,
including a browser-local model → the session cost chip updates after each run.

### 2.1 Function stack rules

A run takes exactly one source and one or more functions valid for its type.

| Source | Function | Combines with | Output |
|---|---|---|---|
| Image | Restyle | Meme caption, Rap roast, Photo → video | Image |
| Image | Meme caption | Restyle, Rap roast, Photo → video | Image |
| Image | Rap roast | Restyle, Meme caption, Photo → video | Audio with the image |
| Image | Photo → video | Restyle, Meme caption, Rap roast | Video |
| Image | Bulk styles ×10 | Nothing (exclusive) | Up to 10 images |
| Video | Meme-ify | Insult roast, Auto-captions | Video |
| Video | Insult roast | Meme-ify, Auto-captions | Video |
| Video | Auto-captions | Meme-ify, Insult roast | Video and SRT |

- Image steps run in a fixed order: Restyle → Meme caption. Rap roast and Photo → video then each
  use that final image and each produce their own gallery item.
- Video functions are rendered together in one FFmpeg pass.
- A run never crosses from image functions to video functions. Chaining is done with *Use as
  source* (J3).
- The server validates the stack. An invalid stack is rejected with 400 and no quota is spent.
- Restyle behaves as in PoRedoImage: the photo is described, then a new image is generated from
  the description with the photo's aspect ratio. The photo itself is not sent to the image model.
- Media is uploaded once, direct to storage, and referenced by id. The server saves every result
  to the gallery: images, videos and roast audio.
- Analysis of a video (frame labels, audio envelope, transcript) is stored with the video and
  reused by later runs on it.
- Each run costs one quota credit. The default limit is 10 runs per user per UTC day.

## 3. Tech stack (pinned)

| Area | Choice | Version |
|---|---|---|
| SDK | .NET, `global.json` `rollForward: latestMinor` | 10.0.100 (10.0.400 installed) |
| Language | C#, `Nullable`, `TreatWarningsAsErrors`, NuGet audit `low`; `EnableTrimAnalyzer` on Client and Shared only (the server is never trimmed) | `LangVersion` latest |
| Server | ASP.NET Core minimal APIs, SignalR | 10.0.x |
| Client | Blazor WebAssembly, no prerender, no AOT | 10.0.10 |
| UI | Radzen.Blazor | 12.0.5 |
| Chat abstraction | Microsoft.Extensions.AI / .OpenAI (image-side chat only) | 10.10.0 / 10.10.1 |
| Caching, resilience | Microsoft.Extensions.Caching.Hybrid, Microsoft.Extensions.Http.Resilience | 10.8.0 / latest 10.x |
| Versioning | MinVer (git tags) | 5.0.0 |
| Images | SixLabors.ImageSharp / ImageSharp.Drawing | 3.1.12 / 2.1.7 |
| Video | FFmpeg, external process (PATH locally, bundled in deploy) | 9.0.1 locally; BtbN `n8.1-latest` on F1 |
| Storage | Azure.Data.Tables / Azure.Storage.Blobs; Azurite locally | 12.11.0 / 12.25.0 |
| Azure AI | Azure.AI.OpenAI / Azure.AI.Vision.ImageAnalysis | 2.1.0 / 1.0.0 |
| Auth | Microsoft.Identity.Web (Entra OIDC) + cookie | 4.9.0 |
| Secrets | Azure.Extensions.AspNetCore.Configuration.Secrets, Azure.Identity | 1.5.1 / 1.21.0 |
| Logging | Serilog.AspNetCore, OpenTelemetry | 10.0.0 / 1.15.x |
| API docs | Scalar.AspNetCore | 2.14.11 |
| Tests | xunit, NSubstitute, Microsoft.AspNetCore.Mvc.Testing, Microsoft.Playwright | 2.9.3 / 5.3.0 / 10.0.10 / 1.59.0 |
| Test tools | bunit, Testcontainers.Azurite, Deque.AxeCore.Playwright, coverlet.collector | 2.11.3 / 4.14.0 / 4.12.0 / 6.0.2 |
| Mobile | Microsoft.Maui.Controls, CommunityToolkit.Mvvm, Microsoft.ML.OnnxRuntimeGenAI | 10.0.10 / 8.4.0 / 0.15.2 |

Versions are the newer of the two source repos. All versions live in `Directory.Packages.props`
(central package management with transitive pinning).

**AI models** (configuration, not code):

| Use | Provider | Model or deployment |
|---|---|---|
| Video director, tagging | AI Foundry | `gpt-5.4-nano` |
| Video frame vision | AI Foundry | `gpt-5.4-mini` |
| Transcription | AI Foundry | a Whisper deployment (must return `verbose_json`) |
| Roast Comic voice | AI Foundry | `gpt-4o-mini-tts` |
| Image text and vision | Azure OpenAI | `gpt-5.4-nano` |
| Image generation | Google | `gemini-2.5-flash-image` |
| Photo → video | Google | `veo-3.1-lite-generate-preview` |
| Music | Google | `lyria-3-clip-preview` |
| Local vision / chat (dev) | Ollama | `gemma4` |

These are copied from the source repos' settings. Each is re-verified against the provider before
the slice that uses it is built (§12, Q3).

## 4. Commands

```powershell
dotnet build PoRedoMedia.slnx                      # the build is the lint: 0 warnings, 0 errors
dotnet run --project src/PoRedoMedia.Api           # http://localhost:4100 | https://localhost:4101
docker compose up -d                               # Azurite (blob 10000, table 10002)
dotnet run --project src/PoRedoMedia.Api -- seed-sounds

dotnet test tests/PoRedoMedia.UnitTests
dotnet test tests/PoRedoMedia.IntegrationTests
dotnet test tests/PoRedoMedia.E2EAPI
pwsh scripts/run-e2e.ps1                           # starts a mock-mode instance, runs the browser tests, stops it
dotnet test --filter "FullyQualifiedName~SomeClass.SomeMethod"

# No Key Vault access: mock AI, no real provider calls
$env:Mocks__UseMockAi='true'; dotnet run --project src/PoRedoMedia.Api
```

`Mocks:UseMockAi` is honoured in Development and Test and refused in Production. It replaces
every provider (vision, chat, image, music, video generation, frame vision, director,
transcription, roast voice), so every journey runs without keys.

Ports 4100/4101 are fixed in `launchSettings.json`. They avoid 4000 (PoRedoImage) and 7000
(PoMemeVideo), so all three apps can run side by side.

## 5. Project structure

```
PoRedoMedia.slnx            Directory.Build.props   Directory.Packages.props   global.json
PoRedoMedia.Mobile.slnx     docker-compose.yml      SPEC.md   CAPABILITY-MAP.md   tasks/
src/
  PoRedoMedia.Api/
    Features/<Slice>/       endpoint + handler + entity + repository + services, co-located
    Common/                 entities, typed ids, cross-slice contracts, storage, AI clients, health
    Configuration/          composition root (the only place that sees every slice)
    Components/App.razor    host document only; renders the Client's routes as WASM
  PoRedoMedia.Client/       Pages/ Layout/ Shared/ LocalAi/ Services/ wwwroot/
  PoRedoMedia.Shared/       Models/ Enums/ Json/
  PoRedoMedia.Mobile/
tests/
  PoRedoMedia.UnitTests/  PoRedoMedia.IntegrationTests/  PoRedoMedia.E2EAPI/  PoRedoMedia.E2EUI/
scripts/                    setup.ps1, meme-sounds/sounds-metadata.json
PoRedoImage/  PoMemeVideo/  read-only reference, gitignored
```

Slices: `Auth`, `Media`, `Runs`, `Restyle`, `MemeCaption`, `BulkStyles`, `RapRoast`,
`PhotoToVideo`, `Memeify`, `VideoRoast`, `Captions`, `Render`, `Sounds`, `Sharing`, `Quota`,
`Housekeeping`, `AiCatalog`.

## 6. Conventions

- **Vertical slices.** A slice never references another slice. It depends on contracts in
  `Api/Common` and gets implementations from DI. `GlobalUsings.cs` imports only `Api.Common` and
  `Shared`.
- **Minimal APIs only**, grouped with `MapGroup` and static handlers.
- **Deny by default.** The fallback policy requires an authenticated user; a public endpoint says
  `.AllowAnonymous()` explicitly. Every state-changing group calls `.RequireAntiforgeryValidation()`.
- **Typed ids** inside (`MediaId`, `RunId`, `UserId`, `SoundId`); raw `Guid` in wire DTOs.
- **Storage names** come from `StorageNames`; config keys from `ConfigKeys`. No string literals.
- **Radzen first.** Use a Radzen component wherever one exists; custom markup only where none does.
- **All UI lives in `.Client`.** No inline styles; scoped `.razor.css`.
- **A fallback must tell the user.** Any path that substitutes canned or degraded AI output sets a
  reason the UI shows.
- **Async all the way**; no `.Result` or `.Wait()`. Delete dead code in the same change.

```csharp
// Features/Quota/QuotaEndpoints.cs
public static class QuotaEndpoints
{
    public static IEndpointRouteBuilder MapQuota(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/quota").MapGet("/", GetAsync);
        return app;
    }

    private static async Task<Ok<QuotaStatusDto>> GetAsync(
        ClaimsPrincipal user, IRenderQuota quota, CancellationToken ct) =>
        TypedResults.Ok(await quota.GetStatusAsync(user.RequireUserId(), ct));
}
```

**Git.** `master` only. One commit per task. Never push unless asked.

## 7. Testing strategy

| Tier | Stack | Scope | Budget (cases) |
|---|---|---|---|
| Unit | xUnit, no I/O | Stack validation, planners, schedulers, FFmpeg argument building, meme text layout, catalog and quota logic | ≤ 100 |
| Integration | xUnit + `WebApplicationFactory`, repositories substituted, mock AI | Each slice's endpoints: auth, antiforgery, validation, happy path | ≤ 50 |
| E2E API | xUnit + `WebApplicationFactory`, full HTTP stack | Routing contract, anonymous allow-list, one run per media type against mocks | ≤ 25 |
| E2E UI | xUnit + Playwright (C#), against a running instance; skipped unless `E2E_BASE_URL` is set | J1, J2, J5 and login | ≤ 25 |

- Build with TDD: failing test first, then the code, then the full suite, then build, then commit.
- Tests never spend AI tokens: the Test environment forces mock AI and a test asserts it.
- Coverage target: ≥ 80 % line coverage on pure logic in `Features/*` and `Common`. Endpoint
  wiring and provider HTTP clients are covered by Integration tests and carry no percentage gate.
- A failing test is reported as failing. Tests are not weakened, skipped or deleted to pass.

## 8. Boundaries

**Always**
- Run with mock fallbacks when provider keys are absent, with the mock banner visible.
- Validate uploads on the server: type by content, size, video duration.
- Keep every per-item blob under one prefix so one delete removes everything.
- Keep `SPEC.md` and `tasks/todo.md` in step when a decision changes.

**Ask first**
- Creating or changing any Azure resource, Key Vault secret or app setting.
- The first deploy, and any change to the deploy workflow.
- Adding a NuGet or JavaScript dependency that is not in §3.
- Deleting stored data, or changing a table or blob layout once data exists.
- Changing auth, quota limits or the anonymous endpoint list.
- Pushing to a remote.

**Never**
- Commit a secret. No `dotnet user-secrets`, no `UserSecretsId`.
- Put a token in the browser, or call `AddOidcAuthentication()`.
- Modify `PoRedoImage/` or `PoMemeVideo/`, or touch their Azure resources.
- Fall back silently from a free local model to a metered provider.
- Enable guest or fake auth in Production.
- Fabricate a provider's API details; verify or ask.

## 9. Out of scope

Public feed, trending and remix · Cue Studio and re-render · GIF export · migrating data from the
old apps · retiring the old apps · iOS · new AI functions · real-time collaboration · Bicep or
container hosting · a paid App Service plan.

## 10. Edge cases and error states

| Case | Behaviour |
|---|---|
| Unsupported file type, image too large, video > 10 min | Rejected before upload completes, with the limit stated |
| Invalid function stack | 400, nothing runs, no quota spent |
| Daily quota spent | 429 with a message and the reset time; Run is disabled |
| Provider key missing | That function is shown as unavailable with the reason; others still work |
| One image step fails mid-chain | Run ends as failed at that step; outputs already produced are kept and labelled partial |
| Video roast fails | The video still renders without it, and the result says so |
| Whisper not configured | Auto-captions is not offered |
| Bulk: some of the 10 fail | The successful ones are shown; failed slots can be retried individually |
| Veo job times out | Run fails with a retry action; the first two retries are free |
| Server restarts mid-run | Housekeeping marks the run interrupted; retry is free |
| Two runs on the same source at once | The second gets 409 |
| Local model fails or the device is unsupported | The error is shown as is; no cloud fallback |
| Storage not configured | Health reports unhealthy and writes fail loudly, never a silent 200 |
| Share link revoked or item deleted | `/v/{token}` returns 404 |
| F1 CPU quota exhausted (platform 403) | Out of the app's control; documented in the runbook |

## 11. Success criteria

1. `dotnet build PoRedoMedia.slnx` reports 0 warnings and 0 errors.
2. All four test tiers pass and each is within its budget.
3. With `Mocks__UseMockAi=true` and no provider keys, J1, J2, J4 and J5 complete end to end on
   localhost and the mock banner is shown.
4. Every stack in §2.1 marked as combinable runs; every other stack is rejected with 400.
   Proven by a unit theory over the full matrix.
5. All 5 image functions and all 3 video functions each produce a gallery item against real
   providers on localhost. Evidence: one screenshot or output file per function.
6. A stacked video run (Meme-ify + Insult roast + Auto-captions) invokes FFmpeg once for the
   final render. Proven by a test on the render command.
7. An anonymous request to any endpoint outside the allow-list is refused; a write without an
   antiforgery token gets 400. Proven by E2E API tests.
8. `/v/{token}` serves a shared image and a shared video to an anonymous browser, and 404s after
   revoke.
9. A user at the daily limit gets 429 and no provider call is made.
10. The model picker changes which provider serves image analysis, and a browser-local model
    completes an analysis with no server AI call. Proven by a Playwright test and a server log.
11. The app installs as a PWA and captures a source image from the webcam.
12. The Android app builds from `PoRedoMedia.Mobile.slnx` and completes J1 against the local Api.
13. CI builds and runs Unit tests on push; the deployed app answers `/health` as healthy on the F1
    plan.
14. No secret is present in the repository (checked by `/security-review`).

## 12. Open questions

| # | Question | Default if unanswered |
|---|---|---|
| Q1 | Resolved: Radzen.Blazor 12.0.5 | — |
| Q2 | Resolved by default: 10 runs per day; every run costs 1 | Change on request |
| Q3 | Are the model ids in §3 still live? `gemini-2.5-flash` was retired under PoRedoImage once | Verify each before its slice is built; report any that changed |
| Q4 | Is there a Whisper deployment for captions? PoMemeVideo ships with it empty | Captions slice is built and tested against mocks; offered only when configured |
| Q5 | Source limits: max image size and max video size | 10 MB image; 10 min and 200 MB video |
| Q6 | Rap roast on an image yields audio beside the image. Should it be muxed into a video instead? | Keep as audio with the image, as PoRedoImage does |
| Q7 | Retention: 30 days for unpinned items, as PoMemeVideo does? | Yes |
| Q8 | App name and resource group in Azure, and the Entra app registration | `app-poredomedia` in RG `PoRedoMedia`; a new registration, created when deploy is approved |

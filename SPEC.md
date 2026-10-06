# PoRedoMedia — Specification

Status: approved 2026-10-05, updated with Phase 2 decisions · Companions:
[CAPABILITY-MAP.md](CAPABILITY-MAP.md), [tasks/plan-archive.md](tasks/plan-archive.md), [tasks/todo.md](tasks/todo.md)

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

**J2 — Meme a video.** Create → drop a video (≤ 1 min) → tick *Meme-ify*, *Insult roast* (pick a
voice) and *Auto-captions* → Run → one render → the finished MP4 plays and is saved. SRT is
downloadable when captions were on.

**J3 — Chain.** Gallery → open an image → *Use as source* → tick *Photo → video* → Run → the
8-second clip is saved → *Use as source* again → tick *Meme-ify* → Run.

**J4 — Bulk.** Create → drop a photo → tick *Bulk styles* (other functions become unavailable) →
Run → up to 10 variations appear as they are made and are all saved to the gallery; the unwanted
ones are deleted there. (The compare board and "save the ones to keep" were never built.)

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
| Azure AI | OpenAI SDK against Azure's v1 endpoint / Azure.AI.Vision.ImageAnalysis. Azure.AI.OpenAI is not used: its only stable release cannot load beside the OpenAI SDK that Microsoft.Extensions.AI needs | 2.14.0 / 1.0.0 |
| Auth | Microsoft.Identity.Web (Entra OIDC) + cookie | 4.9.0 |
| Secrets | Azure.Extensions.AspNetCore.Configuration.Secrets, Azure.Identity | 1.5.1 / 1.21.0 |
| Logging | Microsoft.Extensions.Logging (built in). Serilog, OpenTelemetry and Scalar were planned and never added | — |
| Tests | xunit, NSubstitute, Microsoft.AspNetCore.Mvc.Testing, Microsoft.Playwright | 2.9.3 / 5.3.0 / 10.0.10 / 1.59.0 |
| Test tools | bunit, Testcontainers.Azurite, Deque.AxeCore.Playwright, coverlet.collector | 2.11.3 / 4.14.0 / 4.13.0 / 6.0.2 |
| Phone | The installable web app, with a share target. The MAUI Android app was removed on 2026-10-05 | — |

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
docker-compose.yml          SPEC.md   CAPABILITY-MAP.md   AGENTS.md   DOCS/   tasks/
src/
  PoRedoMedia.Api/
    Features/<Slice>/       endpoint + handler + entity + repository + services, co-located
    Common/                 cross-slice contracts; Domain/ entities and typed ids; Storage/; Ai/ clients
    Configuration/          composition root (the only place that sees every slice)
    Components/App.razor    host document only; renders the Client's routes as WASM
  PoRedoMedia.Client/       Pages/ Layout/ Shared/ LocalAi/ Services/ wwwroot/
  PoRedoMedia.Shared/       Models/ Enums/ Json/
tests/
  PoRedoMedia.UnitTests/  PoRedoMedia.IntegrationTests/  PoRedoMedia.E2EAPI/  PoRedoMedia.E2EUI/
scripts/                    run-e2e.ps1, check-test-budgets.ps1, live-check.ps1, package.py, meme-sounds/
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
- **All UI lives in `.Client`.** No inline styles: tokens and shared rules in `wwwroot/css/app.css`, component layout in scoped `.razor.css`. Colours come from theme variables so light and dark both work.
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

**Git.** `master` only. Commit and push when asked for a git sync (see AGENTS.md).

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

Cue Studio and re-render · migrating data from the
old apps · retiring the old apps · iOS · new AI functions · real-time collaboration · Bicep or
container hosting · a paid App Service plan.

## 10. Edge cases and error states

| Case | Behaviour |
|---|---|
| Unsupported file type, image too large, video > 1 min | Rejected before upload completes, with the limit stated |
| Invalid function stack | 400, nothing runs, no quota spent |
| Daily quota spent | 429 with a message and the reset time; Run is disabled |
| Provider key missing | That function is shown as unavailable with the reason; others still work |
| One image step fails mid-chain | Run ends as failed at that step; outputs already produced are kept and labelled partial |
| Video roast fails | The video still renders without it, and the result says so |
| Whisper not configured | Auto-captions is not offered |
| Bulk: some of the 10 fail | The successful ones are shown and saved. There is no per-slot retry: run it again with only the missing styles ticked |
| A run fails or is cancelled | Results already made are kept. A run that made nothing gives its credit back and says so. This covers a Veo timeout, so its retry is free |
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
12. Withdrawn 2026-10-05: the Android app was removed. A phone uses the installed web app, which
    takes a photo or video shared to it from another app.
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
| Q5 | Source limits: max image size and max video size | 10 MB image; 1 min and 200 MB video (lowered from 10 min on 2026-10-05) |
| Q6 | Rap roast on an image yields audio beside the image. Should it be muxed into a video instead? | Keep as audio with the image, as PoRedoImage does |
| Q7 | Retention: 30 days for unpinned items, as PoMemeVideo does? | Yes |
| Q8 | App name and resource group in Azure, and the Entra app registration | `app-poredomedia` in RG `PoRedoMedia`; a new registration, created when deploy is approved |

## Review outcomes (2026-10-05)

Rules added by the Phase 5 review:
- An upload link writes to a staging blob. Confirm checks it, copies it inside storage with the
  app's content type, and deletes it. Images over 40 megapixels are refused.
- An uploaded sound is visible to its uploader only; the seeded library is shared. 25 uploads per user.
- Upload links, video frames and sound uploads share a limit of 60 requests per user per hour.
- A run request whose options a step would refuse is answered 400 before a credit is spent.
- An item with a run in progress cannot be deleted.
- Fake auth and mock AI need Development or Test (not merely "not Production").
- Public share pages hand out 15-minute read links.

Found and not fixed (each needs a decision or is low value):
- Fixed 2026-10-05: the session cookie is `SameSite=Lax`. It was `Strict`, which browsers withhold from the
  redirect that follows the return from Microsoft, so every Microsoft sign-in landed back on the login page.
- Fixed 2026-10-05: sign-out is a POST with the antiforgery token. It ends this app's session only; the Microsoft session is left alone.
- `AzureAd:TenantId` is `common`, so any Microsoft account can sign in until `Auth:AllowedEmails` is set (added 2026-10-05; empty by default).
- Confirming a video upload still transcribes it without spending a credit (bounded by the upload rate limit).
- Output blobs can be orphaned if the row write fails after the blob write; bulk draws continue briefly after a failed save.
- Fixed 2026-10-05: storage clients are created once per table and container.
- Blob CORS rules are replaced at each start: the storage account must not be shared with another app.
- More than one app instance would make housekeeping fail runs another instance is executing.
- Removed 2026-10-05: the unused video trim parameters. Brought back the same day as a working
  trim: see "Features brought back from the old apps".

## Azure resources (2026-10-05)

- Resource group `PoRedoMedia` (westus3): plan `asp-PoRedoMedia-f1` (Linux F1), web app
  `app-poredomedia` (system managed identity), storage account `stporedomedia`.
- Entra app registration `PoRedoMedia` (any Microsoft account); redirect URLs for localhost:4100
  and app-poredomedia.azurewebsites.net.
- Whisper deployment `whisper` on `po-aiservices-shared`; `AiFoundry:TranscriptionDeployment` is
  set. Auto-captions is proven against it. Whisper is called on the deployment path with an
  api-version: the `/openai/v1` path the rest of the app uses answered DeploymentNotFound for it.
- Vault: `PoRedoMedia--AzureAd--ClientId`, `--AzureAd--ClientSecret`, `--Storage--ConnectionString`.
  The app loads only `PoRedoMedia--*`; a Development run ignores the storage secret and uses Azurite.
- The web app's identity has get/list on `kv-poshared` secrets.
- First deploy 2026-10-05 (tag `v0.1.0`), done from a developer machine: publish, bundle static
  Linux ffmpeg, `scripts/package.py`, `az webapp deploy`. https://app-poredomedia.azurewebsites.net
  answers `/health/live` 200, offers all eight functions, refuses unsigned API calls, has no
  dev-login, and its sound library is seeded (30 sounds).
- Open: no signed-in run has been made on the deployed app (Microsoft sign-in is interactive), so
  the bundled ffmpeg and the host's fonts are unproven there. No GitHub repo or deploy workflow yet.
- F1 limits met on the first deploy: a crash loop exhausts the worker restart quota (15 per window)
  and disables the site until the window resets.

## Changes of 2026-10-05 (audit round)

Behaviour added or changed, beyond the fixes noted above:
- **Create is one pane**: preview, functions, options and a sticky run bar. A run's id is in the
  address, so a refresh, the back button and the header's run tray all return to it.
- **Runs survive navigation.** The header tray lists recent runs, follows unfinished ones, and can
  start a finished run again with the same options. A failed run offers Retry.
- **Gallery**: search covers titles, origin and text; sort; multi-select with bulk pin, download
  and delete; the chain an item was made from; keyboard shortcuts (`/`, arrows, `Del`, `P`,
  `Enter`, `Esc`). Filter, sort and the open item are in the address. "Add media" opens the same
  picker Create uses. On a narrow screen the details open as a bottom sheet.
- **Sounds**: one shared player; the list endpoint returns the whole visible library (its paging
  and filter parameters were unused and are gone). An uploaded sound is checked by its first bytes.
- **Video frames** are sampled on the server when the browser sent none, so clips this app made
  and gallery uploads get real frame analysis. The browser skips frames that look like the last.
- **Auto-captions is always offered.** Without a server speech model, a Whisper model in the
  browser transcribes clips up to 5 minutes and 80 MB at upload.
- **The director** tries the larger deployment once when the usual one returns no usable script.
- **Installed app**: a service worker keeps the shell and seen thumbnails for offline opening and
  receives files shared from other apps.
- **Look and sound**: light and dark themes, a WebGL backdrop, synthesised interface sounds with a
  mute and volume setting, a karaoke stage for roasts with a PNG card export. All motion respects
  the reduced-motion setting.
- **Hardening**: `Auth:AllowedEmails`, POST sign-out, `App:PublicBaseUrl` for share links,
  forwarded headers and HSTS outside Development and Test, `nosniff` / frame / referrer headers.
- **Build**: `AnalysisLevel` latest-recommended with code style enforced; five naming and logging
  rules are switched off in `.editorconfig` with the reason beside them.

Considered and not done:
- Replacing `AiFoundryClient` with `IChatClient`: it carries per-deployment behaviour learned
  against the live service, and there was no way to re-prove it without spending on providers.
- Embedding-based sound matching: needs a new embedding deployment (ask first), and the existing
  matcher is an idf-weighted cosine over tags and names, not the plain tag overlap first assumed.
- Pinning `AllowedHosts`: the platform's own warm-up requests could be refused. Share links use
  `App:PublicBaseUrl` instead.
- A content security policy for the app pages: the on-device models load from two CDNs and need
  testing against one before it can be enforced.
- Moving `ConfigEndpoint` into a slice: it reads every slice, which only the composition root may.

## Features brought back from the old apps (2026-10-05)

Eight things the old apps had were added back on request. The feed, remix and GIF export had been
out of scope (§9) and no longer are; Cue Studio still is.

- **Trim.** Two sliders on Create set `Video.trimStart` and `Video.trimEnd`. The render seeks the
  source, and labels, speech and loudness are shifted to count from the trimmed start. A value
  that cannot be read or is out of range trims less; it never fails the run.
- **Run log.** Every progress line is kept on the run (`Log`, at most 12,000 characters) and shown
  under the steps: speech heard, moments found, each cue (`HIT #n`) and each roast joke.
- **Roast stage.** A lyric line plays the track from there; a slider shifts the highlighting by up
  to 3 seconds; "What the AI saw" shows the scene the roast was written from (`MediaItem.Detail`,
  a new optional column); the roast can be recorded in the browser as a WebM video, meme cut or
  classic.
- **Notifications.** Starting a run asks for permission once. A run that ends while the tab is
  hidden raises a system notification and alternates the tab title.
- **Feed and remix.** Sharing has a switch that also posts the item to `/feed`, which every
  signed-in user can open (Trending or New, 60 items). The share page counts views. A Meme-ify
  result keeps its cues (`script.json`), and "Remix" re-times them onto another user's video
  (`Memeify.remix` = the share token) without calling the director. No anonymous route was added.
- **Exports.** `GET /api/media/{id}/gif` makes a looping GIF of the first 8 seconds; the first
  request costs one run credit and the file is kept. `GET /api/media/{id}/roast-audio` returns a
  video's roast as a sound file.
- **Gallery.** A right-click menu on each card, copy a picture to the clipboard, and one ZIP
  (`GET /api/media/zip?ids=`, up to 50 items, stored uncompressed) for a multi-item download.

- **Camera video.** The camera panel on Create records a video as well as taking a photo: up to
  1 minute, with the microphone when it is allowed. A WebM that arrives with no length in its
  header, as a browser recording does, is repacked at confirm (streams copied, not re-encoded) so
  it can be measured and scrubbed.

Not done: clickable tag chips. Gallery items in this app carry no tags, so there is nothing to
filter by; the search already covers titles, origin and text.

Storage: `Media.Detail`, `Runs.Log` and five properties on a share link (`OnFeed`, `Author`,
`SharedAt`, `Views`, `Remixes`) are new and optional, so existing rows read as before.

## Edge-case round (2026-10-06)

Ten issues from a review of rare workflows, fixed with these decisions:

- **Refused uploads keep their reason.** The client resends a write only when the 400 is the
  antiforgery refusal. A video longer than a minute is refused in the browser before it is sent.
- **Runs do not stick on "running".** When the live connection closes for good the page asks for
  the run every 3 seconds until it ends.
- **Phone photos are turned upright** from their rotation tag whenever an image is processed or
  given a thumbnail. The stored original is not rewritten.
- **A failed run that made nothing is not charged**, and neither is a cancelled one.
- **A run can be cancelled** (`DELETE /api/runs/{id}`, a button on Create). A queued run ends when
  its turn comes, not at once. There is still one queue for the whole app and no queue position.
- **The GIF is made by a POST** (`POST /api/media/{id}/gif`), one at a time per video; the GET
  only serves it. Downloads are marked as downloads, so a refusal no longer replaces the page.
- **Multi-item download** is refused in the page above 50 items or 1 GB.
- **Expiry is shown, not changed.** A shared item still expires at 30 days unless pinned; the
  details panel and the share dialog now say when. `/api/config` carries `retentionDays`.
- **Gallery shortcuts** act only when focus is on the page or a card. Search and Escape work anywhere.
- **A remix is carried by the run's options alone**; the banner follows them and its cancel removes it.

Also: a file dropped during an upload is ignored; the gallery shows results of a run that ends
while it is open; an uploaded sound can be deleted by its uploader (`DELETE /api/sounds/{id}`).

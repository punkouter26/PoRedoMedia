# PoRedoMedia — Tasks

> The file lists below record what each task planned to create. Several were later merged or
> renamed (for example `BulkBoard.razor`, `ShareDialog.razor`, `AiServicePicker.razor`,
> `intake.js`, `video-frames.js`); the code is the record of what exists. M8 (Mobile) was
> removed on 2026-10-05: see SPEC "Changes of 2026-10-05".

Rules for every task: write the failing test first; make it pass; run the full suite; build with
0 warnings; one commit. Manifests list source files; each task also touches at most one test file
unless stated. A task may not edit files outside its manifest except `Program.cs` /
`ServiceRegistration.cs` registration lines and `Directory.Packages.props`.

Paths are relative to `src/PoRedoMedia.` (so `Api/Common/X.cs` is
`src/PoRedoMedia.Api/Common/X.cs`). Sources: `Redo:` = `PoRedoImage/src/PoRedoImage.`,
`Meme:` = `PoMemeVideo/src/PoMemeVideo.`.

Standard verification, unless a task says otherwise:
`dotnet build PoRedoMedia.slnx` then `dotnet test PoRedoMedia.slnx`.

## M0 — Foundation

- [x] **T01 Repository scaffold.** Files: `.gitignore`, `global.json`, `Directory.Build.props`,
  `Directory.Packages.props`, `PoRedoMedia.slnx`. `git init` on `master`; `PoRedoImage/` and
  `PoMemeVideo/` ignored. Accept: `dotnet restore` succeeds. Deps: none.
- [x] **T02 Projects.** Files: `Api/PoRedoMedia.Api.csproj`, `Client/PoRedoMedia.Client.csproj`,
  `Shared/PoRedoMedia.Shared.csproj`, `docker-compose.yml`, `.editorconfig`. Accept: build is
  clean. Deps: T01.
- [x] **T03 Test projects and budgets.** Files: four test `.csproj`, `scripts/check-test-budgets.ps1`
  (port `Meme` script). Accept: script exits 0. Deps: T02.
- [x] **T04 Host and health.** Files: `Api/Program.cs`, `Api/Common/HealthEndpoint.cs`,
  `Api/Components/App.razor`, `Client/Program.cs`, `Client/Routes.razor`. Test:
  `E2EAPI/HealthApiTests.cs` (`/health/live` 200). Accept: app starts on 4100. Deps: T03.
- [x] **T05 Spikes (R1, R2).** Files: `Directory.Packages.props`, `Api/Common/Ai/ChatClients.cs`,
  `Client/Pages/Home.razor`. Test: `UnitTests/ChatClientsTests.cs` builds an `IChatClient` from an
  `AzureOpenAIClient`; the page renders a `RadzenDataGrid` with filtering. Accept: clean build with
  trim analyzer on. **Stop and ask if R1 conflicts.** Deps: T04.
  **Checkpoint.**

## M1 — Auth and shell

- [x] **T06 Auth modes.** Files: `Api/Features/Auth/AuthServiceExtensions.cs`, `AuthEndpoints.cs`,
  `FakeAuthHandler.cs`, `Api/Common/PoEnvironments.cs`. Port `Redo:Web/Features/Auth` (drop
  `SignInTelemetry`). Test: fake auth throws in Production; dev-login sets a cookie. Deps: T04.
- [x] **T07 Antiforgery and deny by default.** Files: `Api/Common/Antiforgery.cs` (token endpoint,
  filter, extension), `Client/Http/AntiforgeryTokenHandler.cs`, `Client/Http/CorrelationHeaderHandler.cs`.
  Test: `E2EAPI/RoutingContractApiTests.cs` — anonymous surface equals the allow-list; write
  without token is 400. Deps: T06.
- [x] **T08 Configuration, Key Vault, mock gate.** Files: `Api/Configuration/KeyVaultExtensions.cs`
  (`PoRedoMedia--` prefix; port `Meme:Api/Configuration/PrefixKeyVaultSecretManager.cs`),
  `Api/Common/MockAi.cs`, `Shared/ConfigKeys.cs`, `Api/appsettings.json`,
  `Api/appsettings.Development.json`. Test: gate is false in Production whatever the flag. Deps: T04.
- [x] **T09 Client shell.** Files: `Client/Layout/MainLayout.razor` (+css), `Client/_Imports.razor`,
  `Client/Pages/Login.razor`, `Client/Shared/RedirectToLogin.razor`, `Client/Shared/MockDataBanner.razor`.
  Built to the Phase 3 design. Test: `E2EUI/LoginUiTests.cs`. Deps: T07, design chosen.
  **Checkpoint.**

## M2 — Media

- [x] **T10 Storage kernel.** Files: `Api/Common/StorageNames.cs`, `StorageClients.cs` (table and
  blob factories), `BlobStorageService.cs`, `BlobDelivery.cs`, `StronglyTypedIds.cs`. Port from
  `Meme:Api/Common`. Test: `IntegrationTests/AzuriteFixture.cs` + blob round trip; ids serialise as
  bare GUIDs. Deps: T08.
- [x] **T11 Media entity and repository.** Files: `Api/Common/MediaItem.cs`,
  `Api/Common/RepositoryContracts.cs`, `Api/Features/Media/MediaTableRepository.cs`,
  `Shared/Models/MediaDtos.cs`, `Shared/Enums/MediaKind.cs`. Test: table round trip; a user cannot
  read another user's item. Deps: T10.
- [x] **T12 FFmpeg process.** Files: `Api/Common/FFmpegProcess.cs` (port `Meme:Api/Features/Output/FFmpegProcess.cs`).
  Test: probe duration of a generated 1 s clip. Deps: T04.
- [x] **T13 Upload.** Files: `Api/Features/Media/MediaEndpoints.cs` (`/sas`, `/{id}/confirm`),
  `UploadValidation.cs`. Port from `Meme:Api/Features/Ingestion`. Test: wrong type, oversize and
  > 10 min are rejected; confirm ignores a client-supplied path. Deps: T11, T12.
- [x] **T14 Gallery API.** Files: `Api/Features/Media/MediaEndpoints.cs` (list, get as 302 to SAS,
  thumb, pin, title, delete), `Thumbnails.cs`. Test: delete removes the whole prefix. Deps: T13.
- [x] **T15 Gallery UI.** Files: `Client/Services/MediaApi.cs`, `Client/Services/BlobUploadService.cs`
  (port), `Client/Pages/Gallery.razor`, `Client/Shared/MediaDetail.razor`,
  `Client/wwwroot/js/browser-actions.js` (download, copy, share only). Test: bUnit gallery renders
  items by kind. Deps: T14, T09.
  **Checkpoint.**

## M3 — Runs

- [x] **T16 Stack rules.** Files: `Shared/Enums/MediaFunction.cs`, `Shared/FunctionStack.cs`.
  Test: one theory over every combination in `SPEC.md` §2.1 (success criterion 4). Deps: T02.
- [x] **T17 Quota.** Files: `Api/Features/Quota/RenderQuotaService.cs`, `QuotaEndpoints.cs`,
  `Shared/Models/QuotaStatusDto.cs`. Port `Meme:Api/Features/Quota`. Test: 11th run of the day is
  refused. Deps: T10.
- [x] **T18 Run entity and dispatcher.** Files: `Api/Common/Run.cs`, `Api/Common/ServiceContracts.cs`
  (`IRunStep`, `IRunNotifier`), `Api/Features/Runs/RunTableRepository.cs`, `RunDispatcher.cs` (port
  `Meme:.../EngineRunDispatcher.cs` without `QueueRender`). Test: steps run in the fixed order; a
  failing step stops the chain and keeps earlier outputs; second run on a busy source is refused.
  Deps: T11, T16.
- [x] **T19 Runs API and hub.** Files: `Api/Features/Runs/RunsEndpoints.cs`, `Api/Hubs/RunHub.cs`,
  `Api/Hubs/RunHubNotifier.cs`, `Shared/Models/RunDtos.cs`. Test: invalid stack is 400 and spends
  no quota; quota spent is 429; busy is 409; hub join by a non-owner is refused. Deps: T17, T18.
  **Checkpoint.**

## M4 — Image functions

- [x] **T20 Image AI contracts and mocks.** Files: `Api/Common/Ai/ImageAiContracts.cs`
  (from `Redo:Domain/Interfaces`), `Api/Common/Ai/MockImageAi.cs` (from
  `Redo:Infrastructure/Services/Mocks/MockAiServices.cs`), `Api/Common/Ai/ImageBytes.cs`.
  Test: mocks registered only when the gate is on. Deps: T08.
- [x] **T21 Meme rendering.** Files: `Api/Features/MemeCaption/ImageSharpMemeGenerator.cs`,
  `MemeTextRenderer.cs`, `MemeTemplateService.cs`, `MemeTemplate.cs`. Port from
  `Redo:Infrastructure/Services`. Tests: port `MemeGeneratorServiceTests`, `MemeTemplateServiceTests`.
  Deps: T20.
- [x] **T22 Meme caption step.** Files: `Api/Features/MemeCaption/MemeCaptionStep.cs`,
  `CaptionWriter.cs`. Vision → caption → render → save as a new `MediaItem`. Test: step output is a
  PNG media item with the source as parent. Deps: T19, T21.
- [x] **T23 Create page.** Files: `Client/Pages/Create.razor`, `Client/Shared/MediaPicker.razor`,
  `Client/Shared/FunctionPicker.razor`, `Client/Shared/RunProgress.razor`, `Client/Services/RunApi.cs`.
  Tests: bUnit picker (example 10); `E2EUI/CreateImageUiTests.cs` for J1 with mocks. Deps: T15, T22.
  **Checkpoint — J1 works.**
- [x] **T24 Chat clients.** Files: `Api/Common/Ai/ChatClients.cs`, `Api/Common/Ai/CachingChatClient.cs`.
  Replaces `Redo` `AzureOpenAiChatCompletionService`, `OllamaChatCompletionService`,
  `CachingChatCompletionService`, `AzureOpenAiService` caption path. Tests: port the caption cases of
  `OpenAIServiceTests`; cache hit skips the inner client. Deps: T05.
- [x] **T25 Vision: Azure CV, router, cache.** Files: `Api/Common/Ai/AzureVisionService.cs`,
  `AzureSceneDetailService.cs`, `VisionServiceRouter.cs`, `CachingVisionService.cs`. Tests: port
  `VisionServiceRouterTests`, `VisionFallbackTests`, `ComputerVisionServiceTests`. Deps: T20.
- [x] **T26 Vision: Gemini, Azure OpenAI, Ollama.** Files: `Api/Common/Ai/GeminiVisionService.cs`,
  `OpenAiVisionService.cs`, `OllamaVisionService.cs`. Test: router picks each by id namespace.
  Deps: T24, T25.
- [x] **T27 Restyle.** Files: `Api/Features/Restyle/RestyleStep.cs`, `ReproductionPromptWriter.cs`,
  `Api/Common/Ai/GeminiImageService.cs`, `Shared/StyleRecipes.cs`. Port as is. Tests: port
  `Imagen3ServiceTests`; a Gemini refusal fails the step with a user-facing reason. Deps: T19, T26.
  **Checkpoint.**
- [x] **T28 Bulk styles, server.** Files: `Api/Features/BulkStyles/BulkStylesStep.cs`,
  `BulkGenerationService.cs`, `BulkPromptRepository.cs`, `BulkPromptEndpoints.cs`. Each finished
  variation is pushed over the hub. Tests: port the bulk case of `AiPipelineEfficiencyTests` and
  `BulkPromptTableEntityRoundTripTests`; a failed slot does not fail the run. Deps: T27.
- [x] **T29 Bulk styles, UI.** Files: `Client/Shared/BulkBoard.razor`, `Client/Shared/BulkPrompts.razor`.
  Test: bUnit — failed slot shows retry; saving keeps only the selected. Deps: T23, T28.
- [x] **T30 Rap roast, server.** Files: `Api/Features/RapRoast/RapRoastStep.cs`, `RoastLyricsWriter.cs`,
  `SceneDescriber.cs`, `SceneSnapshot.cs`, `Api/Common/Ai/LyriaClient.cs`. Output is an Audio media
  item whose parent is the image, with lyrics. Tests: port `RapRoastTests`,
  `SceneSnapshotParsingTests`; music refusal is reported, not thrown. Deps: T24, T25.
- [x] **T31 Rap roast, UI.** *(Finished 2026-10-05 as `RoastStage.razor`: line highlighting timed from line lengths, a loudness pulse, and a PNG card. No WebM export.)* Earlier note: lyrics are stored with the track and shown beside the audio
  player in results and in the gallery. Not ported yet: the karaoke stage (line highlighting), and the
  PNG card and WebM export from `Redo` `roastStage.js` and `RoastScript.cs`. Deps: T23, T30.
- [x] **T32 Photo → video.** Files: `Api/Features/PhotoToVideo/PhotoToVideoStep.cs`,
  `VeoVideoService.cs`. The step starts the Veo job and polls server-side; the clip is saved as a
  Video media item. Tests: port `VeoAudioDirectionTests`; timeout fails the step with a retry.
  Then `E2EAPI/ImageRunApiTests.cs`: Restyle + Meme caption + Rap roast + Photo → video in one run
  yields three outputs. Deps: T27, T30.
  **Checkpoint — all image functions.**

## M5 — Video functions

- [x] **T33 Sound library core.** Files: `Api/Common/SoundAsset.cs`, `SoundVocabulary.cs`,
  `Api/Features/Sounds/SoundAssetTableRepository.cs`, `SemanticMatchingService.cs`, `AudioDuration.cs`.
  Tests: port `SemanticMatchingServiceTests`, `AudioDurationTests`. Deps: T10.
- [x] **T34 Sounds API and seeding.** Files: `Api/Features/Sounds/SoundsEndpoints.cs`,
  `SeedSoundsCommand.cs`, `SoundFavoritesTableRepository.cs`, `SoundTagger.cs`,
  `scripts/meme-sounds/sounds-metadata.json`. Test: favourites sort first; upload is tagged. Deps: T33.
- [x] **T35 Foundry client and frame vision.** Files: `Api/Common/Ai/AiFoundryClient.cs` (port
  unchanged), `Api/Features/Memeify/AiFoundryVisionService.cs`, `VisionStore.cs`,
  `Api/Common/Ai/MockVideoAi.cs`, `Api/Features/Media/MediaEndpoints.cs` (`POST /{id}/frames`).
  Test: labels are stored under the media prefix and a second post is not re-analysed. Deps: T14, T20.
- [x] **T36 Source audio and transcription.** Files: `Api/Features/Captions/SourceAudioAnalysis.cs`,
  `AiFoundryTranscriptionService.cs`, `Transcript.cs`, `Api/Common/FFmpegArgs.Audio.cs`
  (`BuildSourceAudioArgs`). Runs once at confirm for videos. Test: envelope and transcript are
  written once per media. Deps: T12, T35.
- [x] **T37 Director.** Files: `Api/Common/DirectorScript.cs` (with `ScriptEntry`),
  `Api/Features/Memeify/DirectorPrompt.cs`, `AiFoundryDirectorService.cs`,
  `Shared/Models/ScriptEntryDto.cs`, `Shared/Enums/VisualEffectType.cs`. Revision schema removed.
  Tests: port the director cases of `AiPipelineTests`. Deps: T35.
- [x] **T38 Placement.** Files: `Api/Features/Memeify/PlacementPlanner.cs`, `CueSnapping.cs`,
  `TokenBucketTimingService.cs`. Tests: port the placement cases of `AiPipelineTests`,
  `TokenBucketTimingServiceTests`. Deps: T33, T37.
  **Checkpoint.**
- [x] **T39 Render arguments.** Files: `Api/Features/Render/FFmpegArgs.cs` (no GIF), `RenderJob.cs`,
  `SubtitleChunker.cs`, `SrtWriter.cs`, `Api/Assets/overlays/*` (sticker PNGs moved from
  `Meme:Client/wwwroot/overlays`). Tests: port `FFmpegFilterChainTests`, `SubtitleAndSrtTests`.
  Deps: T12.
- [x] **T40 Render service.** Files: `Api/Features/Render/FFmpegRenderService.cs`,
  `RenderVideoCommand.cs`. Output saved as a Video media item with thumbnail. Test: zero cues,
  roast only and captions only each produce one ffmpeg invocation. Deps: T39, T14.
- [x] **T41 Video run step.** Files: `Api/Features/Memeify/VideoRunStep.cs` (from
  `Meme:.../RunEngineCommand.cs`: remix and re-render removed; placement and director gated on
  Meme-ify), `Api/Features/Memeify/MemeifyOptions.cs`. Tests: port the helper cases of
  `AiPipelineTests`; `E2EAPI/VideoRunApiTests.cs` — Meme-ify + roast + captions renders once
  (success criterion 6); captions only does not call the director. Deps: T38, T40.
  **Checkpoint — J2 works through the API.**
- [x] **T42 Video roast.** Files: `Api/Features/VideoRoast/RoastService.cs`, `RoastVoices.cs`,
  `Api/Common/Ai/MockRoastVoice.cs`. Port `Meme:.../RoastService.cs`, `SessionRoast.cs`; Rap voice
  uses `LyriaClient`. Tests: port `RoastServiceTests`; a failed roast leaves the render intact and
  sets a reason. Deps: T41, T30.
- [x] **T43 Captions output.** Files: `Api/Features/Captions/CaptionsEndpoints.cs`
  (`GET /api/media/{id}/captions.srt`, transcript). Test: SRT matches the transcript; not offered
  when Whisper is unconfigured and mocks are off. Deps: T41.
- [x] **T44 Video UI.** Files: `Client/wwwroot/js/video-frames.js` (port), `Client/Shared/VideoOptions.razor`
  (persona, aspect, voice, captions), `Client/Shared/MediaPicker.razor` (frame capture after a video
  upload), `Client/Services/ConfigApi.cs`, `Api/Features/Runs/ConfigEndpoints.cs` (which functions
  and voices are available). Test: `E2EUI/CreateVideoUiTests.cs` for J2 with mocks. Deps: T23, T42, T43.
  **Checkpoint — all video functions.**

## M6 — Sharing, sounds page, retention

- [x] **T45 Share links.** Files: `Api/Features/Sharing/ShareLinkStore.cs`, `SharingEndpoints.cs`,
  `SharePageEndpoints.cs`, `Shared/Models/SharingDtos.cs`, `Client/wwwroot/css/share-page.css`.
  Feed, remix and `IsPublic` removed; page serves image, video or audio. Test: anonymous 200, then
  404 after revoke and after delete (success criterion 8). Deps: T14.
- [x] **T46 Share and sounds UI.** Files: `Client/Shared/ShareDialog.razor`, `Client/Pages/Sounds.razor`,
  `Client/Services/SoundAudition.cs`, `Client/Services/SoundsApi.cs`. Test: bUnit — starring calls
  the API and reorders. Deps: T34, T45, T15.
- [x] **T47 Housekeeping.** Files: `Api/Features/Housekeeping/HousekeepingService.cs`,
  `Api/Common/RetentionPolicy.cs`. Sweeps runs and media. Tests: port `HousekeepingClassifyTests`;
  pinned items survive; an interrupted run becomes retryable. Deps: T19, T45.
  **Checkpoint.**

## M7 — Model picker, local AI, capture, PWA

- [x] **T48 Catalog and pricing.** Files: `Shared/AiProviderIds.cs`, `Shared/AiCatalog.cs`
  (capabilities, options), `Api/Features/AiCatalog/PricingEndpoints.cs`, `Shared/Models/AiPricingDto.cs`.
  Tests: port `AiServiceCatalogTests`. Deps: T26.
- [x] **T49 Picker and cost chip.** Files: `Client/Shared/AiServicePicker.razor`,
  `Client/Services/AiSelectionState.cs`, `Client/Services/SessionCostService.cs`,
  `Client/Shared/SessionCostChip.razor`, `Shared/Models/RunDtos.cs` (model ids on the request).
  Tests: port `SessionCostServiceTests`; a run with `remote:gemini-vision` resolves Gemini. Deps: T48, T23.
- [x] **T50 Local AI, core.** Files: `Client/LocalAi/LocalModelRegistry.cs`, `DtypeChain.cs`,
  `LocalInferenceSession.cs`, `LocalAiService.cs`, `LocalAiErrorClassifier.cs` (plus the small
  record files beside them). Tests: port `LocalAiChainTests`. Deps: T48.
- [x] **T51 Local AI, browser runtime.** Files: `Client/LocalAi/JsLocalInferenceRuntime.cs`,
  `Client/wwwroot/js/local-ai/local-ai-interop.js`, `transformers-worker.js`, `webllm-worker.js`,
  `Api/Features/Runs/RunsEndpoints.cs` (accept a precomputed description). Test: a run carrying a
  precomputed description makes no server vision call (success criterion 10); a local failure does
  not fall back. Deps: T49, T50.
- [x] **T52 Webcam, paste, drop, PWA.** Files: `Client/wwwroot/js/intake.js` (from `Redo` `ux.js`:
  intake and camera only), `Client/Shared/MediaPicker.razor`, `Client/wwwroot/manifest.webmanifest`,
  `Client/wwwroot/sw.js`, `Api/Components/App.razor`. Test: port `CameraCaptureUiTests`
  (success criterion 11). Deps: T23.
- [x] **T53 Accessibility.** Files: `E2EUI/AccessibilityUiTests.cs` only, plus fixes it forces.
  Scans Login, Create, Gallery, Sounds. Accept: no WCAG 2.2 AA violations. Deps: T46, T52.
  **Checkpoint.**

## M8 — Mobile

- [x] **T54 Mobile project.** Mechanical copy of `Redo:Mobile` into `Mobile/` with namespaces
  renamed, plus `PoRedoMedia.Mobile.slnx`. This one task exceeds five files by nature; it changes
  no behaviour. **Starts by checking the Android SDK; I ask before installing anything.** Accept:
  builds for `net10.0-android`. Deps: T53.
- [x] **T55 Mobile API client.** Files: `Mobile/Services/MobileApiClient.cs`, `IMobileApiClient.cs`.
  SAS upload, create run, poll `GET /api/runs/{id}`, gallery. Test: `IntegrationTests/MobileClientTests.cs` (the old app had no client tests to port).
  Deps: T54.
- [x] **T56 Mobile view models.** *(Withdrawn: the Android app was removed 2026-10-05.)* *(Code done and building for `net10.0-android`; the client is proven against the real server by `MobileClientTests`. Still open: J1 on a device. **Deferred by the user on 2026-10-05**: this PC is Windows on ARM, which has no Android emulator package, and no phone was attached.)* Files: `Mobile/ViewModels/MainViewModel.cs`, `GalleryViewModel.cs`,
  `BulkItemViewModel.cs`. Accept: J1 on the emulator against the local Api (success criterion 12).
  Deps: T55. **Checkpoint.**

## M9 — CI and deploy (each needs your go-ahead)

- [x] **T57 CI.** Files: `.github/workflows/ci.yml` (build, budgets, unit tests). `ci-full.yml`
  was removed on 2026-10-07: the integration and API E2E suites run locally. Deps: T53.
- [x] **T58 Deploy workflow.** *(Done 2026-10-07.)* `.github/workflows/deploy.yml` runs after a
  green CI on `master`: publish, bundle static ffmpeg and `DejaVuSans-Bold.ttf`, encode one
  captioned h264 frame with both, `scripts/package.py`, zip deploy, health gate. The app falls back
  to the bundled font (`FFmpegProcess.BundledFontPath`) when the host has none. Deps: T57.
- [x] **T59 Azure and first deploy.** *(Done 2026-10-07, except the signed-in run below.)*
  - Resource group `PoRedoMedia` (westus3): plan `asp-PoRedoMedia-f1` (Linux F1), web app
    `app-poredomedia` (system identity, get/list on `kv-poshared`), storage `stporedomedia`, Entra
    registration `PoRedoMedia`, vault secrets `PoRedoMedia--AzureAd--ClientId`,
    `--AzureAd--ClientSecret`, `--Storage--ConnectionString`.
  - Deploy identity: the PoShared deployment registration (`07bc75d9-…`, also used by PoMemeVideo)
    has federated credential `github-poredomedia-master` and Contributor on the `PoRedoMedia`
    resource group only. GitHub presents the subject with ids in it:
    `repo:punkouter26@121304072/PoRedoMedia@1409377614:ref:refs/heads/master`.
  - Repo variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. Tag `v0.1.0` is on
    the first manual deploy (`b16f615`).
  - Open: no signed-in run has been made on the deployed app (Microsoft sign-in is interactive), so
    a real render with the bundled ffmpeg and font is unproven there.

## M10 — Verify

- [ ] **T60 Phase 5.** Reviews (`/code-review`, `/security-review`, dead code) were done and fixed
  on 2026-10-05 and have not been re-run over the two commits since. Evidence, 2026-10-07:

  | # | Criterion | Evidence |
  |---|---|---|
  | 1 | 0 warnings, 0 errors | Release build clean locally and in CI run 0fa36f5 |
  | 2 | Four tiers pass within budget | Unit 100/100, Integration 49/50, E2E API 7/25, E2E UI 12/25 (11 pass, on-device model test skipped without `E2E_LOCAL_AI=1`) |
  | 3 | J1, J2, J4, J5 with mocks | `scripts/run-e2e.ps1` green |
  | 4 | Stack matrix | `FunctionStackTests` |
  | 5 | Each function against real providers | **Open**: no output files kept; `scripts/live-check.ps1` produces them and spends real credits |
  | 6 | One ffmpeg render per stacked run | Its test is in the suites that passed today (not re-traced to a named test) |
  | 7 | Deny by default, antiforgery | E2E API green; live site answers 401 to anonymous `/api/media` and `/api/runs` |
  | 8 | Share page, 404 after revoke | Its test is in the suites that passed today (not re-traced to a named test) |
  | 9 | 429 at the daily limit | Its test is in the suites that passed today (not re-traced to a named test) |
  | 10 | Model picker and on-device model | Picker covered; the on-device test is the skipped one, so **not re-proven today** |
  | 11 | PWA and webcam | E2E UI green; live `/manifest.webmanifest` 200 |
  | 12 | Withdrawn | — |
  | 13 | CI on push, deployed `/health` | CI and Deploy green on 0fa36f5; `/health/live` 200 after the workflow deploy |
  | 14 | No secret in the repo | Security review of 2026-10-05; the workflow uses OIDC and stores no secret |

  Deferred or not fixed (decisions still yours):
  - Signed-in run on the deployed app; criterion 5 output files; criterion 10 on-device run.
  - `AzureAd:TenantId` is `common`: any Microsoft account can sign in until `Auth:AllowedEmails` is set.
  - Confirming a video upload transcribes it without spending a credit (bounded by the upload rate limit).
  - Output blobs can be orphaned if the row write fails after the blob write.
  - Blob CORS rules are replaced at each start: the storage account must not be shared.
  - More than one app instance would make housekeeping fail runs another instance is executing.
  - The health gate proves the site answers, not that the new build is the one answering.
  - Workflow actions are pinned to Node 20 builds, which GitHub now warns about.

## Verification of the plan as a whole

- After T23 and T41: run `dotnet run --project src/PoRedoMedia.Api` with `Mocks__UseMockAi=true`
  and Azurite up, then drive J1 and J2 in a browser.
- After T44: repeat J1–J5 against real providers with `az login` and record one output per
  function (success criterion 5).
- Before T57: `scripts/check-test-budgets.ps1` and coverage report against the 80 % target.

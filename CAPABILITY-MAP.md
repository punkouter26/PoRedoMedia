# PoRedoMedia — Capability Map

Status: draft for approval · 2026-10-05

PoRedoMedia merges two existing apps into one. This map lists every capability, where its code
comes from, and what happens to it. `Redo` = `PoRedoImage/`, `Meme` = `PoMemeVideo/`. Both folders
are read-only reference and are not part of the new repository.

Actions: **Port** = move with light edits · **Rework** = same behaviour, new shape · **New** =
written for this app · **Prune** = not carried over.

## 1. Projects

| Project | Role | Origin |
|---|---|---|
| `src/PoRedoMedia.Api` | The one server. Vertical slices under `Features/`, kernel in `Common/`, host wiring in `Configuration/`. Hosts the WASM client same-origin. | Meme shape |
| `src/PoRedoMedia.Client` | Blazor WASM SPA, Radzen UI. The only `wwwroot`. | Redo UI stack, new pages |
| `src/PoRedoMedia.Shared` | Wire DTOs, enums, provider ids, JSON context. Ships to the browser. | Both |
| `src/PoRedoMedia.Mobile` | MAUI Android head, own `PoRedoMedia.Mobile.slnx`. Talks to the Api over HTTP only. | Redo, ported last |
| `tests/PoRedoMedia.UnitTests` · `IntegrationTests` · `E2EAPI` · `E2EUI` | Four tiers. | Meme layout |

Redo's `Domain`, `Application` and `Infrastructure` projects are not recreated; their types move
into the slice that uses them, or into `Api/Common` when two slices need them.

## 2. User-facing capabilities

### 2.1 Workflow shell

| Capability | Slice (Api) / page (Client) | Origin | Action |
|---|---|---|---|
| Pick media: drop, file picker, webcam, or an existing gallery item | `Media` / `Create` page | Redo `ImageUploadPanel`, webcam capture; Meme `BlobUploadService`, `video-frames.js` | Rework |
| Detect type and offer only the valid functions | `Runs` / `Create` page | New (replaces Redo `FeatureCatalog` and Meme create settings) | New |
| Stack several functions and run them as one job | `Runs` | Meme `EngineRunDispatcher`, `RunEngineCommand` | Rework |
| Live progress for a run | `Runs` hub / job tray | Meme `EngineHub`; Redo `JobTray` | Rework |
| Gallery of images and videos; pin, delete, download, use as source | `Media` / `Gallery` page | Redo `UserImages`, `Gallery`; Meme `Output`, `Results` | Rework |
| Share link with public page `/v/{token}` | `Sharing` | Meme `ShareLinkStore`, `SharePageEndpoints` | Port, extended to images |
| Sign in, sign out | `Auth` / `Login` page | Redo `Features/Auth` | Port |

### 2.2 Image functions

| Function | Slice | Origin | Action |
|---|---|---|---|
| AI restyle | `Restyle` | Redo `ImageAnalysisOrchestrator`, `ReproductionPromptWriter`, `GeminiImagen3Service`, `StyleRecipeCatalog` | Port |
| Meme caption | `MemeCaption` | Redo `ImageSharpMemeGeneratorService`, `MemeTextRenderer`, `MemeTemplateService` | Port |
| Bulk styles ×10 | `BulkStyles` | Redo `BulkGenerationService`, saved prompts, compare board | Port |
| Rap roast | `RapRoast` | Redo `RapRoastOrchestrator`, `RoastLyricsWriter`, `SceneDescriber`, `LyriaMusicService` | Port |
| Photo → 8 s video | `PhotoToVideo` | Redo `VeoVideoGenerationService`, `VideoGenerateEndpoints` | Port |

### 2.3 Video functions

| Function | Slice | Origin | Action |
|---|---|---|---|
| Meme-ify (vision → director → sounds, stickers, text) | `Memeify` | Meme `Processing` (vision, director, `PlacementPlanner`, `CueSnapping`, `SourceAudioAnalysis`) | Port |
| Insult roast voiceover (Comic / Neural / Rap) | `VideoRoast` | Meme `RoastService`, `SessionRoast` | Port |
| Auto-captions + SRT export | `Captions` | Meme `AiFoundryTranscriptionService`, `SubtitleChunker`, `SrtWriter` | Port |
| Render (one FFmpeg pass for the whole stack) | `Render` | Meme `FFmpegRenderService`, `FFmpegArgs`, `FFmpegProcess`, `RenderVideoCommand` | Port |

### 2.4 Supporting capabilities

| Capability | Slice | Origin | Action |
|---|---|---|---|
| Sound library: browse, audition, favourite, upload, seed | `Sounds` | Meme `MemeLibrary`, `SeedSoundsCommand`, `SoundTagger`, `SemanticMatchingService` | Port |
| Daily render quota | `Quota` | Meme `RenderQuotaService` | Port |
| Retention sweep and orphan recovery | `Housekeeping` | Meme `HousekeepingService` | Port |
| Per-capability model picker and session cost chip | `AiCatalog` / `AiServicePicker` | Redo `AiServiceCatalog`, `Pricing`, `SessionCostService` | Port |
| Browser-local AI (WebGPU, WASM fallback) | Client `LocalAi/` | Redo `LocalAi/`, `local-ai/*.js` | Port |
| PWA install shell | Client `wwwroot` | Redo `manifest.webmanifest`, `sw.js` | Port |
| Health, diagnostics, OpenAPI | `Common` | Both | Port |
| Mock AI with a "USING MOCK DATA" banner | `Common` + `MockDataBanner` | Redo `MockAiServices`, `IMockable`; Meme `AiInterceptionHandler` | Rework into one gate |
| Android app | `PoRedoMedia.Mobile` | Redo `PoRedoImage.Mobile` | Port, re-pointed |

## 3. AI providers

All existing providers are kept.

| Provider | Used for | Origin |
|---|---|---|
| Azure AI Foundry (`AiFoundryClient`) | Video: frame vision, director, sound tagging, Whisper, TTS | Meme |
| Azure Speech | Video roast, Neural voice | Meme |
| Azure OpenAI | Image: captions, prompt enhancement, roast lyrics, vision option | Redo |
| Azure Computer Vision | Image: tags, scene detail (OCR), vision option | Redo |
| Google Gemini | Image generation, vision option | Redo |
| Google Veo | Photo → video | Redo |
| Google Lyria | Rap roast beat (image), Rap voice (video) | Both, one client |
| Ollama | Dev-only vision and chat option | Redo |
| Browser-local (Florence-2, Qwen2.5) | Image analysis and prompt enhancement, on device | Redo |

## 4. Pruned

| Capability | Origin | Reason |
|---|---|---|
| Public feed, trending, remix | Meme `FeedRanking`, `Feed` page, `RemixScriptMapper` | App is a private studio with share links |
| Cue Studio: timeline, cue editing, "Ask director", re-render | Meme `Reveal`, `CueStage`, `CueList`, `cue-scrubber.js`, `DirectorAssistService` | Dropped in the feature audit; a result is changed by running again |
| GIF export | Meme `OutputEndpoints` | Dropped in the feature audit |
| Retro-terminal UI and its hand-rolled components | Meme `Components/`, `retro-terminal.*.css` | Replaced by Radzen |
| One page per feature | Redo `Pages/*`, `FeaturePageBase`, `FeatureShell`; Meme `Source`, `Engine` | Replaced by one pick → stack → run flow |
| Onion layer projects | Redo `Domain`, `Application`, `Infrastructure` | Collapsed into slices |
| Architecture test tier | Redo `Tests.Architecture` | Four tiers only |
| Bicep infra, Dockerfile | Redo `infra/`, `Dockerfile` | Deploys to the shared F1 plan by zip deploy |
| Data migration from the old storage accounts | — | Fresh start; sounds are re-seeded |

## 5. Slice dependencies

Slices do not reference each other. They share only the contracts in `Api/Common`.

```
Auth ─────────────────────────────┐
Media ──┐                         │  every slice
        ├─> Runs ──> image slices │  depends on
Quota ──┘        └─> video slices ──> Render
Sounds ─────────────> Memeify
Sharing ──> Media
Housekeeping ──> Media, Sharing
AiCatalog ──> image slices
```

Build order follows this graph: Auth and Media first, then Runs with Quota, then the function
slices, then Sharing, Sounds management, AiCatalog and local AI, Mobile, and deployment last.

# To do

## Done: clean-up C1–C10 (2026-10-21)

- [x] C1 Repo hygiene: `.aura/` and `.vscode/mcp.json` untracked and ignored; screenshot set renamed to `261020-…`; this file and `DOCS/README.md` back.
- [x] C2 Vision: one `IVisionService` (Gemini, else Azure OpenAI) replaces the router and model picker; `ICombinedVisionAnalyzer` folded into `ISceneDetailProvider`.
- [x] C3 Dropped Ollama (`OllamaSharp`), the tags-only Azure Computer Vision provider, `AiProviderIds` and the `eval-ai` command.
- [x] C4 On-device AI behind Settings → Advanced; its JS in `js/local-ai/device-tasks.js`.
- [x] C5 Effects and interface sounds default off; depth preview and gallery shatter/fling removed.
- [x] C6 `/settings` page replaces the account drawer.
- [x] C7 Tried hiding Rap roast, Bulk styles, Photo to video and Insult roast behind a switch; reverted on request: every function always shows.
- [x] C8 Feed and Sounds tabs behind their switches; share links stay.
- [x] C9 Device-result endpoints split from `MediaEndpoints`; `VideoRunStep` → `VideoStep`.
- [x] C10 `scripts/package.sh` shared by `deploy.yml` and hand packaging; tests updated, budgets below the caps.

## Done: clean-up round 2, C1–C10 (2026-10-21)

- [x] C1 Dropped the unused on-device text model (WebLLM/Qwen) and its worker; one local model, Florence-2.
- [x] C2 Dropped Azure Computer Vision (`ComputerVision:*`, `Azure.AI.Vision.ImageAnalysis`); the rap roast reads the picture with its own vision call.
- [x] C3 Meme-ify vision: Gemini watches the whole clip, Foundry frames are the fallback; `Vision:WholeClip`, `Vision:FrameProvider` and the Gemini frame labeller removed.
- [x] C4 `Features/Memeify`, `VideoRoast` and `Captions` merged into `Features/Video`; vision and speech clients moved to `Common/Ai`.
- [x] C5 Cost chip left the header (the spend shows on Create and in Runs); Library page and Gallery merged into one page.
- [x] C6 Community feed and remix removed; share links stay.
- [x] C7 Sounds page, sound upload, favourites and spectrograms removed; Meme-ify keeps the seeded library.
- [x] C8 `package.py` folded into `package.sh`; seed-sounds looks in two places; Azurite pinned to 3.37.0.
- [x] C9 Exports under one route: `/api/media/{id}/export/{gif|srt|roast}`.
- [x] C10 Settings page unit tests. Sharing one test host between E2EAPI and Integration was skipped: it saves ~10 lines and couples two projects.

## Done: fewer functions (2026-10-21)

- [x] Upscale and Cutout removed everywhere (server, on-device cutout, Hugging Face, prices, config keys).
- [x] Rap roast has no options: always stand-up, Nuclear, strong language allowed.
- [x] Bulk styles: Clear button; empty prompt boxes are skipped and not listed.

## Open

- [ ] Run `scripts/package.sh` once on Linux/WSL, or watch the next deploy, to confirm the moved packaging steps.
- [ ] Decide whether recipes and settings should follow the account (they are per browser).
- [ ] Old recipes in a browser that include Upscale or Cutout no longer load (the whole recipe list reads as empty there).
- [ ] Old `SoundFavorites` table and `media/{id}/script.json` blobs are no longer read; drop them from storage when convenient.

# PoRedoMedia in one page

A private media studio. A signed-in user picks one image or one video, ticks the functions to
apply, and runs them as one job. Results land in the gallery, where they can be downloaded, shared
by link, or used as the source of the next run.

## Projects

| Project | What it is |
|---|---|
| `src/PoRedoMedia.Api` | ASP.NET Core host: minimal APIs, the SignalR run hub, background run queue, ffmpeg |
| `src/PoRedoMedia.Client` | Blazor WebAssembly UI (Radzen), served by the Api. All UI lives here |
| `src/PoRedoMedia.Shared` | Wire DTOs, the function-stack rules, option keys, price estimates |
| `tests/*` | Unit, Integration (Azurite in Docker), E2E API, E2E UI (Playwright) |

Inside the Api: `Features/<Slice>` holds each slice's endpoints and services, `Common/` holds what
slices share (`Domain/` entities and ids, `Storage/` blob and table access, `Ai/` provider
clients, and the cross-slice contracts), and `Configuration/` is the composition root.

## How a run flows

1. The browser uploads straight to blob storage through a signed link, then confirms. The server
   measures what arrived (type, size, pixels, duration) before the item becomes usable.
2. `POST /api/runs` validates the stack and options, reserves the source, spends one quota credit,
   and queues the run. One run executes at a time.
3. Each function is an `IRunStep`. Image steps run in a fixed order; the three video functions are
   one step that ends in a single ffmpeg render.
4. Progress goes out over the hub. The client's `RunTracker` follows every unfinished run for as
   long as the app is open, so the header tray and the Create page show the same state and a run
   survives leaving the page.

## Things worth knowing before changing code

- **Mock mode** (`Mocks__UseMockAi=true`, Development and Test only) replaces every AI provider.
  All tests use it; no test spends tokens.
- **Deny by default.** Every endpoint needs a signed-in user unless it says `.AllowAnonymous()`,
  and `RoutingContractApiTests` fails when the anonymous list changes. Writes need the antiforgery
  token, sign-out included.
- **Who may sign in** is `Auth:AllowedEmails` (comma-separated). Empty means any Microsoft account.
- **Video analysis is stored with the video** (frames, labels, loudness, speech) and reused. The
  browser sends frames at upload; when it did not, the server samples them with ffmpeg at run time.
- **Speech** comes from the server's Whisper deployment, or from a Whisper model in the browser
  when the server has none.
- **The browser remembers** theme, sound settings, last-used options and saved recipes in
  `localStorage`. They are per browser, not per account.
- **Test budgets** are enforced by `scripts/check-test-budgets.ps1`: 100 unit, 50 integration,
  25 API, 25 UI.

## Running it

See "Running things" in [AGENTS.md](../AGENTS.md).

# PoRedoMedia in one page

A private media studio. A signed-in user picks one image or one video, ticks the functions to
apply, and runs them as one job. Results land in the Library, where they can be downloaded, shared
by link, or used as the source of the next run. How the code is built is in
[CLAUDE.md](../CLAUDE.md); this page is what the app does.

## What a user sees by default

| Media | Functions |
|---|---|
| Image | Restyle, Meme caption, Rap roast, Photo to video, Bulk styles |
| Video | Meme-ify, Insult roast, Auto-captions |

The header has Create, Library (your media) and Runs; the account button opens **Settings**.
Visual effects and interface sounds are off. A result can be shared by a private link (anyone with
the link can view it), and a video can be downloaded as a GIF, its captions (SRT) or its roast audio.
Meme-ify picks from a seeded sound library (`seed-sounds` command); it has no page of its own.
Rap roast has no options: it is always a stand-up set at full strength with strong language allowed.
Bulk styles skips any empty prompt box; its Clear button empties all ten to type your own.

## Settings → Advanced

Every switch starts off. Each adds to the app and none is needed for it to work. They are kept in
the browser, not the account.

| Switch | Adds |
|---|---|
| On-device AI | Describe pictures in the browser for free (about 230 MB the first time) |
| Recipes | Save a set of ticked functions and reuse it in one tap |
| Cost estimates | The estimated AI cost of a run, and the total for the tab (on Create and in Runs) |
| Visual effects | Glass backdrop, particles, the run orb, picture reveals and confetti |

## Configuration that decides what exists

A function is offered only when its provider is configured (see `Shared/ConfigKeys.cs`):
`Google:ApiKey` (Gemini images and vision, whole-clip video vision, Veo, Lyria), `OpenAI:*` (chat;
vision when there is no Google key), `AiFoundry:*` (sampled-frame video vision, director, roast
voice, speech). `Mocks:UseMockAi` (Test only) replaces every provider.

## Running it

See [AGENTS.md](../AGENTS.md#running-things) and the commands in [CLAUDE.md](../CLAUDE.md#commands).

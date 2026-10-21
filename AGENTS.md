# Rules for agents working in this repository

Task list: [tasks/todo.md](tasks/todo.md).
Overall summary of the project: [DOCS/README.md](DOCS/README.md).

## 1. Branching & version control

- **Master first:** Work only on the `master` branch. Use another branch only when explicitly asked to.
- **Human-like commits:** When asked for a git sync, make a short commit whose message uses
  non-technical American slang, so it reads as if a person wrote it, and push it.

## 2. Runtime, testing & quality

- **Startup verification:** After any code change, restart the app and verify it starts successfully.
- **Targeted testing:** Do not run the full test suite. Run only the tests related to the change,
  or none at all for a minor edit.
- **Zero warnings:** Treat every compiler warning as an error and fix it right away.

## 3. Architecture & configuration

- **Documentation check:** Read [DOCS/README.md](DOCS/README.md) first for architecture context.
  Update it in the same change whenever dependencies, configuration keys or major endpoints change.
- **Maximum automation:** Do not make the user type CLI commands or click through a web GUI when it
  can be done for them automatically.
- **Secrets:** Do not use `dotnet user-secrets`. Put local data in `appsettings` or in Azure Key
  Vault (`kv-poshared`, secrets prefixed `PoRedoMedia--`).
- **Build version:** The app shows its deployment build version as `YYMMDD` in the bottom-right
  corner. `Directory.Build.targets` derives it from the Git commit date (today's date when there is
  no commit); never type it by hand.

## 4. UI changes & reporting

- **Visual documentation:** For a UI change, capture the old and new states, annotate what changed,
  save them in `SCREENSHOTS/YYMMDD-feature-name.html`, and give the full valid path.
- **TL;DR:** End any answer longer than 100 words with a TL;DR of about 20 words.
- **3-tier diff summary:** For every code change over 20 lines, add a TL;DR with exactly one line per tier:
  1. Toddler: one sentence of simple, conceptual intuition.
  2. Child: one sentence on what it does.
  3. Adult: one sentence on the technical or architectural change.
- **Big removals:** If a prompt removes more than 100 lines of code overall, say so.
- **Direction questions:** After a code change over 100 lines, when the prompt asked no other
  questions, ask 3 optional high-level questions about the direction of the app's purpose that
  could lead to further changes.

## Running things

- App: `dotnet run --project src/PoRedoMedia.Api` (http://localhost:4100; needs `docker compose up -d` for Azurite).
- Browser tests: `scripts/run-e2e.ps1`. Test budgets: `scripts/check-test-budgets.ps1`.
- Package for App Service: `bash scripts/package.sh package.zip` (Linux or WSL; the same script `deploy.yml` runs).

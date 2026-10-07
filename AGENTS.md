# Rules for agents working in this repository

Task list: [tasks/todo.md](tasks/todo.md).
Overall summary of the project: [DOCS/README.md](DOCS/README.md).

## Working rules

- Only use the `master` branch for all work. Use another branch only when specifically asked to.
- After a code change, restart the app and verify it restarts successfully.
- Do not use `dotnet user-secrets` to store data locally. Put it in `appsettings` or in Azure Key
  Vault (`kv-poshared`, secrets prefixed `PoRedoMedia--`).
- When asked for a git sync, make a commit whose message is short, uses American slang and is not
  technical, so it reads as if a person wrote it, and push it.
- At the end of any answer longer than 100 words, add a TLDR of about 20 words.
- Do not run all tests after a code change. Run only the tests related to the change, or none at
  all if the change is simple.
- Avoid making the user type commands into a CLI or click through a web GUI when it can be done
  for them automatically.
- Treat compile warnings as errors and fix them.
- If a prompt removes more than 100 lines of code overall, say so.
- When the UI changes, take an annotated screenshot showing the old and new UI with the changes
  marked, place it in the `SCREENSHOTS` folder inside an HTML file, and give the full valid path.

## Running things

- App: `dotnet run --project src/PoRedoMedia.Api` (http://localhost:4100; needs `docker compose up -d` for Azurite).
- Browser tests: `scripts/run-e2e.ps1`. Test budgets: `scripts/check-test-budgets.ps1`.
- Package for App Service: `dotnet publish src/PoRedoMedia.Api -c Release -o artifacts/publish`,
  add a static Linux ffmpeg under `artifacts/publish/ffmpeg`, then `python scripts/package.py artifacts/publish artifacts/package.zip`.

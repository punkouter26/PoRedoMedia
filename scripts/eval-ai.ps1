# scripts/eval-ai.ps1
# Scores the AI's meme captions per variant (one draft, best of several, an optional local Ollama
# model) with the app's judge, and writes an HTML report under artifacts/eval/.
# This calls real AI providers and spends real money; the report says how much.
#
# Usage (from the repository root):
#   ./scripts/eval-ai.ps1                                  # the 10 newest uploaded pictures
#   ./scripts/eval-ai.ps1 -Dir C:\pics -Count 30           # a fixed set, for comparing changes
#   ./scripts/eval-ai.ps1 -Ollama qwen2.5vl:7b             # also score a local model (free)
param(
    [string]$Dir,
    [int]$Count = 10,
    [string]$Ollama,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

# The newest uploads live in storage, so Azurite must be up when no folder is given.
if (-not $Dir) { docker compose up -d | Out-Null }

# Built to its own folder, so it runs while the app holds its usual build output open.
$bin = 'artifacts/eval-bin'
dotnet build src/PoRedoMedia.Api -c Debug -o $bin --nologo -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$arguments = @("$bin/PoRedoMedia.Api.dll", 'eval-ai', '--count', $Count)
if ($Dir) { $arguments += @('--dir', (Resolve-Path $Dir).Path) }
if ($Ollama) { $arguments += @('--ollama', $Ollama) }
if ($Out) { $arguments += @('--out', $Out) }

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_CONTENTROOT = (Resolve-Path $bin).Path
dotnet @arguments
exit $LASTEXITCODE

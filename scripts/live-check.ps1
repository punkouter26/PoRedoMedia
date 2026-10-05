# scripts/live-check.ps1
# Runs one real function stack against a locally running app and saves what it produced.
# This calls real AI providers and spends real money and one quota credit per run.
#
# Start the app first:  dotnet run --project src/PoRedoMedia.Api
# Usage (from a PowerShell prompt): ./scripts/live-check.ps1 -File photo.jpg -Functions MemeCaption [-Options @{ 'Vision.model' = 'remote:azure-cv' }] [-Label cv]
param(
    [Parameter(Mandatory)][string]$File,
    [Parameter(Mandatory)][string[]]$Functions,
    [hashtable]$Options = @{},
    [string]$Label = ($Functions -join '+'),
    [string]$BaseUrl = 'http://localhost:4100',
    [string]$OutDir = 'artifacts/live'
)

$ErrorActionPreference = 'Stop'
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()

Invoke-WebRequest "$BaseUrl/dev-login?email=live-check%40localhost" -WebSession $session | Out-Null
$token = (Invoke-RestMethod "$BaseUrl/api/antiforgery/token" -WebSession $session).token
$headers = @{ 'X-CSRF-TOKEN' = $token }

# Upload: reserve, PUT to storage, confirm.
$item = Get-Item $File
$ticket = Invoke-RestMethod "$BaseUrl/api/media/sas" -Method Post -WebSession $session -Headers $headers -ContentType 'application/json' `
    -Body (@{ fileName = $item.Name; sizeBytes = $item.Length } | ConvertTo-Json)
Invoke-WebRequest $ticket.uploadUrl -Method Put -InFile $item.FullName -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } | Out-Null
$source = Invoke-RestMethod "$BaseUrl/api/media/$($ticket.id)/confirm" -Method Post -WebSession $session -Headers $headers

# A video is analysed from sampled frames. The browser does this in the app; here ffmpeg does.
if ($source.kind -eq 'Video') {
    $frameDir = Join-Path ([IO.Path]::GetTempPath()) "live-frames-$([guid]::NewGuid().ToString('N'))"
    [IO.Directory]::CreateDirectory($frameDir) | Out-Null
    ffmpeg -v error -i $item.FullName -vf "fps=1/3,scale=512:-2" -q:v 6 (Join-Path $frameDir 'f%03d.jpg')
    $files = Get-ChildItem $frameDir -Filter *.jpg | Sort-Object Name
    $frames = @($files | ForEach-Object { 'data:image/jpeg;base64,' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName)) })
    $times = @(0..($files.Count - 1) | ForEach-Object { 1.5 + 3 * $_ })
    $analysed = Invoke-RestMethod "$BaseUrl/api/media/$($source.id)/frames" -Method Post -WebSession $session -Headers $headers -ContentType 'application/json' `
        -Body (@{ frames = $frames; timestamps = $times } | ConvertTo-Json)
    Write-Host "[$Label] frames sent: $($files.Count), moments found: $($analysed.momentsFound)"
    [IO.Directory]::Delete($frameDir, $true)
}

$started = Get-Date
$run = Invoke-RestMethod "$BaseUrl/api/runs" -Method Post -WebSession $session -Headers $headers -ContentType 'application/json' `
    -Body (@{ sourceId = $source.id; functions = $Functions; options = $Options } | ConvertTo-Json)
while ($run.status -in 'Queued', 'Running') {
    Start-Sleep -Milliseconds 750
    $run = Invoke-RestMethod "$BaseUrl/api/runs/$($run.id)" -WebSession $session
}

$seconds = [int]((Get-Date) - $started).TotalSeconds
Write-Host "[$Label] $($run.status) in ${seconds}s$(if ($run.error) { ': ' + $run.error })"
$run.notes | ForEach-Object { Write-Host "[$Label] note: $_" }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$gallery = Invoke-RestMethod "$BaseUrl/api/media" -WebSession $session
$n = 0
foreach ($id in $run.outputIds) {
    $media = $gallery | Where-Object id -eq $id
    $extension = switch ($media.kind) { 'Video' { '.mp4' } 'Audio' { '.mp3' } default { '.png' } }
    $n++
    $path = Join-Path $OutDir "$Label-$n-$($media.origin)$extension"
    Invoke-WebRequest "$BaseUrl$($media.url)" -WebSession $session -OutFile $path
    Write-Host "[$Label] saved $path ($($media.kind), $([int]((Get-Item $path).Length / 1KB)) KB)"
    if ($media.text) { Write-Host "[$Label] text: $($media.text)" }
}

if ($run.status -ne 'Complete') { exit 1 }

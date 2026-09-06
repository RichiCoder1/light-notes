param([switch] $Fresh, [switch] $PrepareOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$reviewProfile = Join-Path $root 'artifacts/review/data'
if ($Fresh) { $reviewProfile = Join-Path $root ('artifacts/review/data-' + [Guid]::NewGuid().ToString('N')) }
$database = Join-Path $reviewProfile 'notes.db'
$executable = Join-Path $root 'artifacts/publish/LightNotes.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Run tools/Build.ps1 -Publish first.' }
if (-not (Test-Path -LiteralPath $database)) {
    & dotnet run --project (Join-Path $root 'tools/LightNotes.Review/LightNotes.Review.csproj') -c Release -- $database
    if ($LASTEXITCODE) { throw 'Review data preparation failed.' }
}
Write-Output "Review data: $reviewProfile"
if ($PrepareOnly) { return }
$previous = $env:LIGHT_NOTES_DATA_DIRECTORY
try {
    $env:LIGHT_NOTES_DATA_DIRECTORY = $reviewProfile
    # This launcher is explicitly invoked for an interactive manual review.
    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -PassThru
    Write-Output "Light Notes review is running (process $($process.Id))."
} finally { $env:LIGHT_NOTES_DATA_DIRECTORY = $previous }

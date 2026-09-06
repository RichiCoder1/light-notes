param([switch] $Publish, [switch] $Run, [switch] $UpdateLock)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$previousCredential = $env:NuGetPackageSourceCredentials_lucent
$previousHttpCache = $env:NUGET_HTTP_CACHE_PATH
try {
    # SDK resolution precedes restore switches, so isolate its HTTP cache during upgrades.
    if ($UpdateLock) {
        $env:NUGET_HTTP_CACHE_PATH = Join-Path $root ("artifacts/nuget-http/" + [Guid]::NewGuid().ToString("N"))
    }
    if (-not $previousCredential) {
        $token = $env:GITHUB_TOKEN
        if (-not $token) {
            $token = (& gh auth token --hostname github.com)
            if ($LASTEXITCODE) { throw 'Authenticate with gh auth login, then gh auth refresh -s read:packages.' }
        }
        $env:NuGetPackageSourceCredentials_lucent = "Username=RichiCoder1;Password=$token;ValidAuthenticationTypes=Basic"
        $token = $null
    }
    Push-Location $root
    try {
        $project = 'src/LightNotes/LightNotes.csproj'
        $restoreArguments = @('restore', $project)
        if ($UpdateLock) { $restoreArguments += '--force-evaluate' } else { $restoreArguments += '--locked-mode' }
        & dotnet @restoreArguments
        if ($LASTEXITCODE) { throw 'Restore failed. Check read:packages authorization, package access, and the exact version pins.' }
        if ($Publish) { & dotnet publish $project -c Release --no-restore -o artifacts/publish }
        else { & dotnet build $project -c Release --no-restore }
        if ($LASTEXITCODE) { throw 'Build failed.' }
        if ($Run) {
            if ($Publish) { & ./artifacts/publish/LightNotes.exe }
            else { & dotnet run --project $project -c Release --no-build --no-restore }
            if ($LASTEXITCODE) { throw 'Light Notes exited unsuccessfully.' }
        }
    } finally { Pop-Location }
} finally {
    $env:NuGetPackageSourceCredentials_lucent = $previousCredential
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
}

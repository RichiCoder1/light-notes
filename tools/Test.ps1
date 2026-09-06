param([switch] $UpdateLock)
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
            if ($LASTEXITCODE) { throw 'GitHub package authentication is required.' }
        }
        $env:NuGetPackageSourceCredentials_lucent = "Username=RichiCoder1;Password=$token;ValidAuthenticationTypes=Basic"
        $token = $null
    }
    Push-Location $root
    try {
        foreach ($project in @('tests/LightNotes.Storage.Tests/LightNotes.Storage.Tests.csproj', 'tests/LightNotes.Tests/LightNotes.Tests.csproj')) {
            $restoreMode = if ($UpdateLock) { '--force-evaluate' } else { '--locked-mode' }
            & dotnet restore $project $restoreMode
            if ($LASTEXITCODE) { throw "Restore failed: $project" }
            & dotnet test --project $project -c Release --no-restore
            if ($LASTEXITCODE) { throw "Tests failed: $project" }
        }
    } finally { Pop-Location }
} finally {
    $env:NuGetPackageSourceCredentials_lucent = $previousCredential
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
}

param([switch] $Publish, [switch] $Run, [switch] $UpdateLock, [switch] $FormatCheck)
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
        & (Join-Path $PSScriptRoot 'Assert-SdkPins.ps1') -Root $root
        $project = 'src/LightNotes/LightNotes.csproj'
        $restoreArguments = @('restore', $project)
        if ($UpdateLock) { $restoreArguments += '--force-evaluate' } else { $restoreArguments += '--locked-mode' }
        & dotnet @restoreArguments
        if ($LASTEXITCODE) { throw 'Restore failed. Check read:packages authorization, package access, and the exact version pins.' }
        if ($FormatCheck) {
            $luiFiles = @(& git ls-files --cached '*.lui' | Where-Object { Test-Path -LiteralPath $_ })
            if ($LASTEXITCODE) { throw 'Could not enumerate tracked LUI sources.' }
            if ($luiFiles.Count -eq 0) { throw 'No tracked Light Notes .lui files were found.' }
            $outsideApp = @($luiFiles | Where-Object { $_.Replace('\', '/') -notlike 'src/LightNotes/*.lui' })
            if ($outsideApp.Count) { throw "Tracked .lui files outside src/LightNotes are not covered by the app SDK build: $($outsideApp -join ', ')" }
        }
        $buildArguments = @($project, '-c', 'Release', '--no-restore')
        if ($FormatCheck) { $buildArguments += '-p:LucentLuiFormatCheck=true' }
        if ($Publish) { & dotnet publish @buildArguments -o artifacts/publish }
        else { & dotnet build @buildArguments }
        if ($LASTEXITCODE) { throw 'Build failed.' }
        if ($FormatCheck) { Write-Output "Lucent SDK tracked .lui check: PASS ($($luiFiles.Count) files)" }
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

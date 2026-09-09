param([string] $Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

$propsPath = Join-Path $Root 'Directory.Build.props'
$globalPath = Join-Path $Root 'global.json'
if (-not (Test-Path -LiteralPath $propsPath)) { throw "Missing runtime pin file: $propsPath" }
if (-not (Test-Path -LiteralPath $globalPath)) { throw "Missing SDK pin file: $globalPath" }

[xml] $props = Get-Content -LiteralPath $propsPath -Raw
$runtimePin = $props.SelectSingleNode('/Project/PropertyGroup/LucentVersion').InnerText.Trim()
$global = Get-Content -LiteralPath $globalPath -Raw | ConvertFrom-Json
$sdkPin = ([string] $global.'msbuild-sdks'.'Lucent.Lui.Sdk').Trim()

if ([string]::IsNullOrWhiteSpace($runtimePin)) { throw 'Directory.Build.props has no LucentVersion.' }
if ([string]::IsNullOrWhiteSpace($sdkPin)) { throw 'global.json has no Lucent.Lui.Sdk pin.' }
if ($runtimePin -cne $sdkPin) {
    throw "Lucent SDK/runtime pins differ: global.json=$sdkPin; Directory.Build.props=$runtimePin."
}

Write-Output "Lucent SDK/runtime pins match: $sdkPin"

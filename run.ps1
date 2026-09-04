#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$app = Join-Path $PSScriptRoot 'artifacts\publish'
$exe = Join-Path $app 'DnfItemChecker.App.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw 'Run .\build.ps1 first.'
}
if (-not (Test-Path -LiteralPath (Join-Path $app 'config.json')) -and
    [string]::IsNullOrWhiteSpace($env:NEOPLE_API_KEY)) {
    throw 'Set NEOPLE_API_KEY or copy artifacts\publish\config.json.sample to config.json and enter your API key.'
}
Push-Location $app
try {
    & $exe
    if ($LASTEXITCODE -ne 0) { throw "Application exited with code $LASTEXITCODE." }
}
finally { Pop-Location }

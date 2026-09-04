#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'src\DnfItemChecker.App\DnfItemChecker.App.csproj'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$stage = Join-Path $artifacts ('staging\' + [Guid]::NewGuid().ToString('N'))
$runtime = Join-Path $stage 'no-kalbak-runtime'
$archive = Join-Path $artifacts 'no-kalbak-runtime.zip'

# PowerShell 5.1 does not turn native nonzero exit codes into exceptions.
& dotnet publish $project --configuration Release --runtime win-x64 `
    --self-contained true --property:PublishSingleFile=true `
    --property:IncludeNativeLibrariesForSelfExtract=true --output $publish
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. No release was created."
}

# Only these files may enter the release, even if publish/ contains local state.
$required = @(
    'DnfItemChecker.App.exe',
    'config.json.sample',
    'stattable.json',
    'models\ch_PP-OCRv5_mobile_det.onnx',
    'models\ch_ppocr_mobile_v2.0_cls_infer.onnx',
    'models\korean_PP-OCRv5_rec_mobile.onnx',
    'models\ppocrv5_korean_dict.txt'
)
foreach ($relative in $required) {
    $source = Join-Path $publish $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or (Get-Item -LiteralPath $source).Length -eq 0) {
        throw "Missing or empty release file: $relative"
    }
}
$sample = Get-Content -LiteralPath (Join-Path $publish 'config.json.sample') -Raw | ConvertFrom-Json
if (-not [string]::IsNullOrWhiteSpace($sample.apiKey)) {
    throw 'config.json.sample must contain an empty API key.'
}

New-Item -ItemType Directory -Path $runtime -Force | Out-Null
foreach ($relative in $required) {
    $destination = Join-Path $runtime $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish $relative) -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $root 'docs\RUNTIME.md') -Destination (Join-Path $runtime 'README.md')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$tempArchive = Join-Path $stage 'no-kalbak-runtime.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($runtime, $tempArchive,
    [System.IO.Compression.CompressionLevel]::Optimal, $true)
Move-Item -LiteralPath $tempArchive -Destination $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value "$hash  no-kalbak-runtime.zip" -Encoding ascii

# Only remove this invocation's generated staging directory, inside artifacts/.
$resolvedStage = (Resolve-Path -LiteralPath $stage).Path
$stagingRoot = [System.IO.Path]::GetFullPath((Join-Path $artifacts 'staging')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedStage.StartsWith($stagingRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected staging path: $resolvedStage"
}
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
Write-Host "Published: $publish"
Write-Host "Release ZIP: $archive"
Write-Host "SHA256: $hash"

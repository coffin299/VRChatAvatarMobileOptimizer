# Builds a .unitypackage from Packages/com.coffi.vrchat-mobile-optimizer without launching Unity.
# A .unitypackage is a gzipped tar where each asset lives in "<guid>/" with "asset", "asset.meta" and "pathname".
# Missing .meta files are generated with deterministic GUIDs (MD5 of the asset path) and written next to the
# source files so that GUIDs stay stable across builds. Commit the generated .meta files.

param(
    [string]$PackageName = "com.coffi.vrchat-mobile-optimizer",
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $repoRoot "Packages\$PackageName"
if (-not (Test-Path $packageRoot)) { throw "Package folder not found: $packageRoot" }

$manifest = Get-Content (Join-Path $packageRoot "package.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$version = $manifest.version

function Get-DeterministicGuid([string]$text) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text))
    return -join ($bytes | ForEach-Object { $_.ToString("x2") })
}

function Get-MetaBody([System.IO.FileSystemInfo]$item) {
    if ($item.PSIsContainer) {
        return "folderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
    }
    switch ($item.Extension.ToLowerInvariant()) {
        ".cs" {
            return "MonoImporter:`n  externalObjects: {}`n  serializedVersion: 2`n  defaultReferences: []`n  executionOrder: 0`n  icon: {instanceID: 0}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
        }
        ".asmdef" {
            return "AssemblyDefinitionImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
        }
        ".shader" {
            return "ShaderImporter:`n  externalObjects: {}`n  defaultTextures: []`n  nonModifiableTextures: []`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
        }
        ".json" {
            if ($item.Name -eq "package.json") {
                return "PackageManifestImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
            }
            return "TextScriptImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
        }
        default {
            return "DefaultImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
        }
    }
}

function Get-UnityPath([System.IO.FileSystemInfo]$item) {
    $relative = $item.FullName.Substring($packageRoot.Length).TrimStart('\', '/')
    $path = "Packages/$PackageName"
    if ($relative) { $path = "$path/$($relative -replace '\\', '/')" }
    return $path
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Collect the package root folder itself plus every file/folder under it, excluding .meta files.
$items = @(Get-Item $packageRoot) + @(Get-ChildItem $packageRoot -Recurse -Force | Where-Object { $_.Extension -ne ".meta" })

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("unitypackage_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $staging | Out-Null

$generated = 0
$entries = @()
try {
    foreach ($item in $items) {
        $unityPath = Get-UnityPath $item
        $metaPath = "$($item.FullName).meta"

        if (-not (Test-Path $metaPath)) {
            $guid = Get-DeterministicGuid $unityPath
            [System.IO.File]::WriteAllText($metaPath, "fileFormatVersion: 2`nguid: $guid`n" + (Get-MetaBody $item), $utf8NoBom)
            $generated++
        }

        $guidLine = Select-String -Path $metaPath -Pattern '^guid:\s*([0-9a-fA-F]{32})' | Select-Object -First 1
        if (-not $guidLine) { throw "No GUID found in $metaPath" }
        $guid = $guidLine.Matches[0].Groups[1].Value.ToLowerInvariant()

        $entryDir = Join-Path $staging $guid
        New-Item -ItemType Directory -Path $entryDir | Out-Null
        Copy-Item $metaPath (Join-Path $entryDir "asset.meta")
        [System.IO.File]::WriteAllText((Join-Path $entryDir "pathname"), $unityPath, $utf8NoBom)
        if (-not $item.PSIsContainer) { Copy-Item $item.FullName (Join-Path $entryDir "asset") }
        $entries += $guid
    }

    $outDir = Join-Path $repoRoot $OutputDir
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    $outFile = Join-Path $outDir "$PackageName-$version.unitypackage"
    if (Test-Path $outFile) { Remove-Item $outFile }

    & tar.exe -czf $outFile -C $staging @entries
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }

    if ($generated -gt 0) { Write-Host "Generated $generated missing .meta file(s). Commit them to keep GUIDs stable." }
    Write-Host "Built $outFile ($($entries.Count) entries)"
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

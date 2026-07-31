<#
.SYNOPSIS
    Builds the WebUI (React/shadcn) frontend and copies its output into the
    WebViewHost project's WebUiAssets folder, so it ships as Content next to the
    WebView2 control that serves it.

.DESCRIPTION
    Run this manually after changing anything under src/WebUI, then rebuild
    (dotnet build src/WebViewHost/WebViewHost.csproj, then src/AddIn/AddIn.csproj
    so its post-build copy picks up the refreshed WebViewHost output).
#>

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$webUiDir = Join-Path $repoRoot "src\WebUI"
$distDir = Join-Path $webUiDir "dist"
$targetDir = Join-Path $repoRoot "src\WebViewHost\WebUiAssets"

Write-Host "Building WebUI in $webUiDir..."
Push-Location $webUiDir
try {
    npm run build
}
finally {
    Pop-Location
}

if (-not (Test-Path $distDir)) {
    throw "Expected build output not found at $distDir"
}

Write-Host "Clearing $targetDir..."
if (Test-Path $targetDir) {
    Get-ChildItem $targetDir -Force | Where-Object { $_.Name -ne ".gitkeep" } | Remove-Item -Recurse -Force
}
else {
    New-Item -ItemType Directory -Path $targetDir | Out-Null
}

Write-Host "Copying $distDir -> $targetDir..."
Copy-Item (Join-Path $distDir "*") $targetDir -Recurse -Force

Write-Host "Done. Rebuild WebViewHost then AddIn to pick up the refreshed WebUiAssets."

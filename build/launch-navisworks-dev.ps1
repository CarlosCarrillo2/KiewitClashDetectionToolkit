<#
.SYNOPSIS
    Launches Navisworks Manage with the dock pane pointed at the local Vite dev server,
    so UI changes show up on refresh without rebuilding the AddIn or restarting Navisworks.

.DESCRIPTION
    Sets NAVISWORKSDOCKPANEL_DEV_URL for this process only (does not touch persistent
    user/system environment variables), then starts Navisworks. WebViewBootstrapper reads
    this variable at pane-open time and navigates there instead of the built WebUiAssets.

    Prerequisite: `npm run dev` must already be running in src/WebUI (default port 5173).

.PARAMETER Year
    Navisworks version year to launch. Defaults to 2026.

.PARAMETER DevUrl
    URL of the running Vite dev server. Defaults to http://localhost:5173.
#>

param(
    [string]$Year = "2026",
    [string]$DevUrl = "http://localhost:5173"
)

$ErrorActionPreference = "Stop"

$exePath = "C:\Program Files\Autodesk\Navisworks Manage $Year\Roamer.exe"
if (-not (Test-Path $exePath)) {
    throw "Navisworks executable not found at $exePath"
}

$env:NAVISWORKSDOCKPANEL_DEV_URL = $DevUrl
Write-Host "Launching Navisworks Manage $Year with dock pane pointed at $DevUrl ..."
Start-Process -FilePath $exePath

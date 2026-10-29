# Installs Alchemist for SOLIDWORKS — the SolidWorks MCP server for Claude.
#
#   powershell -ExecutionPolicy Bypass -File .\install.ps1                 # install / update
#   ... -Test              # + run the smoke test on a live SolidWorks (tools\smoke_test.json)
#   ... -SharedInstance    # work in the SolidWorks window the user has open (default: a separate hidden instance)
#   ... -NoRegister        # leave the Claude Desktop config alone
#   ... -Dev               # + pytest and ruff
#   ... -SwApiDir "D:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"   # SolidWorks not in the default folder
#
# Safe to run again: updates the dependencies, rebuilds the exe, re-registers the server.
param(
    [switch]$Test, [switch]$SharedInstance, [switch]$NoRegister, [switch]$Dev,
    [string]$SwApiDir = "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
function Step($t) { Write-Host "`n== $t" -ForegroundColor Cyan }

Step "SolidWorks"
$sldworks = Join-Path (Split-Path (Split-Path $SwApiDir)) "SLDWORKS.exe"
if (-not (Test-Path (Join-Path $SwApiDir "SolidWorks.Interop.sldworks.dll"))) { throw "SolidWorks API not found in $SwApiDir. Pass -SwApiDir." }
if (Test-Path $sldworks) { Write-Host ("SolidWorks " + (Get-Item $sldworks).VersionInfo.ProductVersion + "  ($sldworks)") }
$sim = Test-Path (Join-Path (Split-Path (Split-Path $SwApiDir)) "Simulation\cosworks.dll")
Write-Host ("SOLIDWORKS Simulation: " + $(if ($sim) { "found" } else { "not found (the sim_* tools will not work)" }))

Step "Python"
$py = $null
foreach ($c in @("py -3.13", "py -3.12", "py -3.11", "python")) {
    $exe, $arg = $c.Split(" ", 2)
    try {
        $v = & $exe $arg -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
        if ($LASTEXITCODE -eq 0 -and [version]$v -ge [version]"3.11") { $py = $c; break }
    } catch {}
}
if (-not $py) { throw "Python 3.11+ is needed (https://www.python.org/downloads/ - tick 'Add to PATH')." }
Write-Host "using: $py"

$venvPy = Join-Path $root ".venv\Scripts\python.exe"
if (-not (Test-Path $venvPy)) {
    Step "Virtual environment .venv"
    $exe, $arg = $py.Split(" ", 2)
    & $exe $arg -m venv (Join-Path $root ".venv")
    if ($LASTEXITCODE -ne 0) { throw "Could not create .venv" }
}
Step "Python dependencies"
& $venvPy -m pip install --disable-pip-version-check -q --upgrade pip
& $venvPy -m pip install --disable-pip-version-check -q -r (Join-Path $root "adapters\claude\requirements.txt")
if ($LASTEXITCODE -ne 0) { throw "pip install failed" }   # here, before the dev installs can reset it
if ($Dev) {
    & $venvPy -m pip install --disable-pip-version-check -q -r (Join-Path $root "adapters\claude\requirements-dev.txt")
}
if ($LASTEXITCODE -ne 0) { throw "pip install failed" }

Step "Building the execution server (SolidworksExecution.exe) for the installed SolidWorks"
Get-Process SolidworksExecution -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "execution\solidworks\build.ps1") -SwApiDir $SwApiDir
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Step "How Claude works with SolidWorks"
$flag = Join-Path $root "own_instance.flag"
if ($SharedInstance) {
    if (Test-Path $flag) { Remove-Item $flag }
    Write-Host "shared: Claude works in the SolidWorks window you have open"
} else {
    Set-Content -Encoding utf8 $flag "Claude: a separate hidden SolidWorks. Delete this file to work in the user's open window."
    Write-Host "its own hidden SolidWorks instance (does not get in your way)"
}
New-Item -ItemType Directory -Force (Join-Path $root "macros") | Out-Null

if (-not $NoRegister) {
    Step "Registering in Claude Desktop"
    & $venvPy (Join-Path $root "tools\register_claude.py")
}

Step "Claude skills (skills\*)"
# Claude Code takes skills from %USERPROFILE%\.claude\skills; in Claude Desktop upload the zip from dist\skills
# by hand: Settings -> Capabilities -> Skills -> Upload.
& $venvPy (Join-Path $root "tools\pack_skills.py") --install

if ($Test) {
    Step "Smoke test (starts SolidWorks, 1-3 min)"
    & $venvPy (Join-Path $root "tools\run_steps.py") (Join-Path $root "tools\smoke_test.json") 200
}

Write-Host "`nDone. Restart Claude Desktop - the tools appear under the 'solidworks' server." -ForegroundColor Green

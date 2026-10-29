# Build SolidworksExecution.exe (the C# server that drives SolidWorks) without Visual Studio / MSBuild.
# Compiles against the SolidWorks interop of the INSTALLED SolidWorks (default: 2024), so any API call that does
# not exist in that version fails here, at build time — not at runtime. Called by install.ps1.
#
#   .\build.ps1                       # build into SolidworksExecution\bin\Debug (stop the running server first)
#   .\build.ps1 -OutDir C:\tmp\sp     # build elsewhere (dry run)
#   .\build.ps1 -SwApiDir "D:\SW2024\api\redist"
param(
    [string]$SwApiDir = "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist",
    [string]$OutDir,
    [string]$Csc
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)        # repo root
$proj = Join-Path $PSScriptRoot "SolidworksExecution"
$lib  = Join-Path $PSScriptRoot "lib"                                # vendored Owin / WebApi / Newtonsoft
if (-not $OutDir) { $OutDir = Join-Path $proj "bin\Debug" }

# C# compiler: Microsoft.Net.Compilers.Toolset unpacked into <repo>\_roslyn (downloaded from nuget.org if missing)
if (-not $Csc) {
    $roslyn = Join-Path $root "_roslyn"
    $Csc = Get-ChildItem $roslyn -Recurse -Filter csc.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Csc) {
        $ver = "4.11.0"
        $pkg = Join-Path $env:TEMP "microsoft.net.compilers.toolset.$ver.zip"
        Write-Host "Downloading C# compiler (Microsoft.Net.Compilers.Toolset $ver, ~21 MB) from nuget.org ..."
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/$ver/microsoft.net.compilers.toolset.$ver.nupkg" -OutFile $pkg -UseBasicParsing
        Expand-Archive $pkg $roslyn -Force
        Remove-Item $pkg
        $Csc = Get-ChildItem $roslyn -Recurse -Filter csc.exe | Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $Csc -or -not (Test-Path $Csc)) { throw "csc.exe not found (pass -Csc)" }
if (-not (Test-Path (Join-Path $SwApiDir "SolidWorks.Interop.sldworks.dll"))) { throw "SolidWorks interop not found in $SwApiDir (is SolidWorks installed? pass -SwApiDir)" }

$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$refs = @("mscorlib","System","System.Core","System.Net.Http","Microsoft.CSharp","System.Xml","System.Xml.Linq",
          "System.Data","System.Runtime.Serialization","System.Web","System.ComponentModel.DataAnnotations","System.Drawing") |
        ForEach-Object { Join-Path $fw "$_.dll" }
$third = Get-ChildItem $lib -Filter *.dll | Select-Object -ExpandProperty FullName
$sw = @("SolidWorks.Interop.sldworks","SolidWorks.Interop.swconst","SolidWorks.Interop.swpublished","SolidWorks.Interop.cosworks") |
      ForEach-Object { Join-Path $SwApiDir "$_.dll" }
$refs += $third + $sw

$src = Get-ChildItem $proj -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
       Select-Object -ExpandProperty FullName

New-Item -ItemType Directory -Force $OutDir | Out-Null
$exe = Join-Path $OutDir "SolidworksExecution.exe"
$rsp = Join-Path $env:TEMP "swmcp_build.rsp"
$lines = @("/nostdlib+","/target:exe","/platform:anycpu","/debug:full","/define:DEBUG;TRACE",
           "/langversion:latest","/nowarn:1591,0168,0219,0649,0414,1701","/out:`"$exe`"")
$lines += $refs | ForEach-Object { "/r:`"$_`"" }
$lines += $src  | ForEach-Object { "`"$_`"" }
Set-Content -Encoding utf8 $rsp $lines

$ver = (Get-Item $sw[0]).VersionInfo.FileVersion
Write-Host "SolidWorks interop $ver from $SwApiDir"
& $Csc /noconfig /nologo "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }

# Runtime dependencies next to the exe: third-party libs, the SW interop we compiled against, the config.
foreach ($dll in $third + $sw) { Copy-Item $dll $OutDir -Force }
Copy-Item (Join-Path $proj "app.config") (Join-Path $OutDir "SolidworksExecution.exe.config") -Force
Write-Host "OK -> $exe"

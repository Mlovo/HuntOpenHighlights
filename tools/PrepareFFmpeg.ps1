#requires -Version 5.1
[CmdletBinding()]
param([string]$ArchivePath = '', [switch]$Offline)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = Split-Path $PSScriptRoot -Parent
$Lock = Get-Content (Join-Path $PSScriptRoot 'ffmpeg.lock.json') -Raw | ConvertFrom-Json
$Cache = Join-Path $Root '.cache/downloads'
New-Item $Cache -ItemType Directory -Force | Out-Null
if (!$ArchivePath) { $ArchivePath = Join-Path $Cache $Lock.name }
if (!(Test-Path $ArchivePath)) {
    if ($Offline) { throw "Offline: provide the pinned archive with -ArchivePath. Required SHA256: $($Lock.sha256)" }
    Write-Host "Downloading the pinned FFmpeg shared SDK (build machine only)..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Lock.url -OutFile ($ArchivePath + '.download') -UseBasicParsing
    Move-Item ($ArchivePath + '.download') $ArchivePath
}
$Hash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($Hash -ne $Lock.sha256) { throw "FFmpeg checksum mismatch. Refusing to compile or embed untrusted binaries. Expected $($Lock.sha256); got $Hash" }
$Extract = Join-Path $Root ('.cache/ffmpeg-' + [Guid]::NewGuid().ToString('N'))
$Destination = Join-Path $Root 'native/vendor/ffmpeg'
try {
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $Extract
    $Sdk = Get-ChildItem -LiteralPath $Extract -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'include/libavcodec/avcodec.h') } | Select-Object -First 1
    if (!$Sdk) { throw 'The verified archive does not contain an FFmpeg shared SDK.' }
    if (Test-Path $Destination) { Remove-Item -LiteralPath $Destination -Recurse -Force }
    New-Item $Destination -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $Sdk.FullName '*') -Destination $Destination -Recurse
    Set-Content (Join-Path $Destination '.hunt-source-sha256') $Hash -Encoding Ascii
} finally {
    if (Test-Path $Extract) { Remove-Item -LiteralPath $Extract -Recurse -Force }
}
# BtbN's MinGW import archives are not assumed to work with MSVC. Generate standard x64 .lib
# files from the verified DLL export tables using the user's Visual Studio tools.
$VsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path $VsWhere)) { throw 'Install Visual Studio 2022 / Build Tools with Desktop development with C++.' }
$Vs = & $VsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$Vs) { throw 'MSVC x64 tools not found. Install the Desktop development with C++ workload.' }
$Toolset = Get-ChildItem (Join-Path $Vs 'VC/Tools/MSVC') -Directory | Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
$Bin = Join-Path $Toolset.FullName 'bin/Hostx64/x64'
$Dumpbin = Join-Path $Bin 'dumpbin.exe'
$Lib = Join-Path $Bin 'lib.exe'
New-Item (Join-Path $Destination 'lib') -ItemType Directory -Force | Out-Null
foreach ($Component in @('avcodec','avformat','avutil','swresample','swscale')) {
    $Dll = Get-ChildItem (Join-Path $Destination "bin/$Component-*.dll") | Select-Object -First 1
    if (!$Dll) { throw "Missing runtime DLL: $Component" }
    $Output = & $Dumpbin /nologo /exports $Dll.FullName
    if ($LASTEXITCODE -ne 0) { throw "dumpbin failed for $($Dll.Name)" }
    $Symbols = @($Output | ForEach-Object {
        if ($_ -match '^\s+\d+\s+[0-9A-Fa-f]+\s+[0-9A-Fa-f]+\s+([_A-Za-z][_A-Za-z0-9]*)\s*$') { $Matches[1] }
    })
    if ($Symbols.Count -eq 0) { throw "No exports found in $($Dll.Name)" }
    $Def = Join-Path $Destination "lib/$Component.def"
    @("LIBRARY $($Dll.Name)", 'EXPORTS') + $Symbols | Set-Content -LiteralPath $Def -Encoding Ascii
    & $Lib /nologo /machine:x64 "/def:$Def" "/out:$(Join-Path $Destination "lib/$Component.lib")"
    if ($LASTEXITCODE -ne 0) { throw "lib.exe failed for $Component" }
}
Write-Host "Verified FFmpeg SDK prepared at $Destination"

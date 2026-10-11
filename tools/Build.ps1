#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$FFmpegArchive = '',
    [switch]$Offline,
    [switch]$SkipTests,
    [switch]$NoPublish
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'The WPF + WGC backend must be built on Windows x64.' }
$Root = Split-Path $PSScriptRoot -Parent
Set-Location $Root
function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    Write-Host "==> Executing: $Exe $($Arguments -join ' ')"
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Exe $($Arguments -join ' ') exited with $LASTEXITCODE"
    }
}
trap {
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $Detail = ($_ | Out-String).Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
        Write-Host "::error title=Windows build::$Detail"
    }
    throw
}
Get-Command dotnet, cmake -ErrorAction Stop | Out-Null
if (!(Test-Path 'bin/MessageBus.dll')) { throw 'The original signed MessageBus.dll is missing from the repository.' }
$ExpectedBus = '64e300997b5796da22ad8baa49e4eb919cdc170157cbe6770279f94508a84650'
if ((Get-FileHash 'bin/MessageBus.dll' -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedBus) { throw 'The NVIDIA payload hash changed. Review the binary before updating the pin.' }
$Signature = Get-AuthenticodeSignature 'bin/MessageBus.dll'
if ($Signature.Status -ne 'Valid' -or $Signature.SignerCertificate.Subject -notmatch 'NVIDIA Corporation') {
    throw "MessageBus.dll signature check failed: $($Signature.Status). No unsigned replacement will be embedded."
}
if (!$SkipTests) {
    Invoke-Checked 'dotnet' @('run','--project','tests/HuntReplay.Tests','--configuration',$Configuration)
    Invoke-Checked 'dotnet' @('build','src/HuntReplay.App','--configuration',$Configuration)
    Invoke-Checked 'dotnet' @('run','--project','tests/HuntReplay.WindowsTests','--configuration',$Configuration)
}
& (Join-Path $PSScriptRoot 'PrepareFFmpeg.ps1') -ArchivePath $FFmpegArchive -Offline:$Offline
$CmakeGenerator = if ($env:GITHUB_ACTIONS -eq 'true') { 'Visual Studio 17 2022' } else { 'Visual Studio 18 2026' }
try {
    Invoke-Checked 'cmake' @('-S','native','-B','artifacts/native','-G',$CmakeGenerator,'-A','x64',"-DFFMPEG_ROOT=$Root/native/vendor/ffmpeg")
} catch {
    if ($CmakeGenerator -eq 'Visual Studio 18 2026') {
        if (Test-Path 'artifacts/native') { Remove-Item -LiteralPath 'artifacts/native' -Recurse -Force }
        Invoke-Checked 'cmake' @('-S','native','-B','artifacts/native','-G','Visual Studio 17 2022','-A','x64',"-DFFMPEG_ROOT=$Root/native/vendor/ffmpeg")
    } else {
        throw
    }
}
Invoke-Checked 'cmake' @('--build','artifacts/native','--config',$Configuration,'--parallel','4')
$Runtime = Join-Path $Root 'artifacts/runtime'
if (Test-Path $Runtime) { Remove-Item -LiteralPath $Runtime -Recurse -Force }
New-Item $Runtime -ItemType Directory -Force | Out-Null
Copy-Item "artifacts/native/$Configuration/HuntReplay.Media.dll" $Runtime
Copy-Item 'native/vendor/ffmpeg/bin/*.dll' $Runtime
$Manifest = Get-ChildItem $Runtime -Filter '*.dll' | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
# ASCII without BOM. This manifest and every DLL are embedded resources, not sidecar dependencies.
$Manifest | Set-Content (Join-Path $Runtime 'native.sha256') -Encoding Ascii
if ($NoPublish) {
    Invoke-Checked 'dotnet' @('build','src/HuntReplay.App','--configuration',$Configuration)
    Write-Host 'Build complete (no portable executable requested).'
    exit 0
}
$Publish = Join-Path $Root 'artifacts/publish'
New-Item $Publish -ItemType Directory -Force | Out-Null
Invoke-Checked 'dotnet' @('publish','src/HuntReplay.App','--configuration',$Configuration,'--runtime','win-x64','--self-contained','true',
    '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=embedded','--output',$Publish)
Copy-Item 'LICENSE','THIRD_PARTY_NOTICES.md' $Publish
$Licenses = Join-Path $Publish 'licenses'
New-Item $Licenses -ItemType Directory -Force | Out-Null
Copy-Item 'licenses/*' $Licenses
Get-ChildItem 'native/vendor/ffmpeg' -File | Where-Object { $_.Name -match '^(LICENSE|COPYING)' } | Copy-Item -Destination $Licenses
$Exe = Join-Path $Publish 'HuntReplayStudio.exe'
(Get-FileHash $Exe -Algorithm SHA256).Hash.ToLowerInvariant() + '  HuntReplayStudio.exe' | Set-Content (Join-Path $Publish 'SHA256SUMS.txt') -Encoding Ascii
Write-Host "`nPortable self-contained executable: $Exe" -ForegroundColor Green
Write-Host 'No OBS, Python, FFmpeg executable, VC++ redistributable or .NET installation is needed on the target PC.'
Write-Host 'This build is unsigned unless YOU sign it. Complete docs/WINDOWS_VALIDATION.md and third-party redistribution checks before release.'

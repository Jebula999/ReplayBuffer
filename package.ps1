# Builds everything, then makes the two downloads in release\:
#   ReplayBuffer-Setup-<ver>.exe     per-user installer (Start menu, Apps & features, uninstaller)
#   ReplayBuffer-Portable-<ver>.zip  unzip anywhere and run; settings stay in the folder
param([switch]$NoBuild)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$ver = ([regex]'Version = "([\d.]+)"').Match((Get-Content "$root\installer\Setup.cs" -Raw)).Groups[1].Value
$dist = "$root\dist\ReplayBuffer"
$out = "$root\release"
$tmp = "$root\build\package"

if (-not $NoBuild) { cmd /c "`"$root\build.bat`""; if ($LASTEXITCODE) { throw "build failed" } }
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $tmp, $out | Out-Null
$files = "ReplayBuffer.exe", "capture.exe", "audio.exe", "ffmpeg.exe", "ffmpeg-LICENSE.txt", "THIRD-PARTY-NOTICES.txt"
foreach ($f in $files) { if (-not (Test-Path "$dist\$f")) { throw "missing $dist\$f" } }

# portable zip: a ReplayBuffer folder with portable.txt, so settings live in .\Data
$p = "$tmp\portable\ReplayBuffer"
New-Item -ItemType Directory -Force $p | Out-Null
foreach ($f in $files) { Copy-Item "$dist\$f" $p }
Set-Content "$p\portable.txt" "This copy is portable: settings and the log are kept in the Data folder next to ReplayBuffer.exe.`r`nDelete this file to use %APPDATA%\ReplayBuffer instead."
$zip = "$out\ReplayBuffer-Portable-$ver.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$tmp\portable\ReplayBuffer" -DestinationPath $zip -CompressionLevel Optimal

# installer: the same files (minus portable.txt) embedded in a small setup program
$payload = "$tmp\payload.zip"
Compress-Archive -Path ($files | ForEach-Object { "$dist\$_" }) -DestinationPath $payload -CompressionLevel Optimal
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$setup = "$out\ReplayBuffer-Setup-$ver.exe"
& $csc /nologo /target:winexe /optimize+ /platform:x64 "/out:$setup" "/win32icon:$root\src\app.ico" `
    "/win32manifest:$root\installer\setup.manifest" "/resource:$payload,payload.zip" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "$root\installer\Setup.cs"
if ($LASTEXITCODE) { throw "setup compile failed" }
Get-ChildItem $out | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }

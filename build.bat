@echo off
rem Builds Replay Buffer into dist\ReplayBuffer (needs Visual Studio 2022 C++ tools and the .NET 9 SDK)
cd /d "%~dp0"
call native\build.bat || exit /b 1
cd /d "%~dp0"
rem self-contained: runs on any Windows 10/11 x64 PC without installing .NET
dotnet publish src\ReplayBuffer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none ^
  -o dist\ReplayBuffer -nologo -v q || exit /b 1
if not exist dist\ReplayBuffer\ffmpeg.exe (
  echo Downloading FFmpeg ^(gyan.dev release essentials build^)...
  if not exist build mkdir build
  curl -L -o build\ffmpeg.zip https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip || exit /b 1
  tar -xf build\ffmpeg.zip -C build || exit /b 1
  for /d %%d in (build\ffmpeg-*) do copy /y "%%d\bin\ffmpeg.exe" dist\ReplayBuffer\ >nul
)
dist\ReplayBuffer\ffmpeg.exe -hide_banner -L > dist\ReplayBuffer\ffmpeg-LICENSE.txt 2>&1
copy /y THIRD-PARTY-NOTICES.txt dist\ReplayBuffer\ >nul
echo Built dist\ReplayBuffer

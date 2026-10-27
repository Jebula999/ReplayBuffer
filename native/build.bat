@echo off
rem Builds the native helpers into ..\..\dist\ReplayBuffer (needs Visual Studio C++ tools)
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>nul
cd /d "%~dp0"
if not exist ..\dist\ReplayBuffer mkdir ..\dist\ReplayBuffer
cl /nologo /O2 /EHsc /MT /std:c++17 capture.cpp /Fe:..\dist\ReplayBuffer\capture.exe || exit /b 1
cl /nologo /O2 /EHsc /MT /std:c++17 audio.cpp /Fe:..\dist\ReplayBuffer\audio.exe || exit /b 1
del *.obj 2>nul


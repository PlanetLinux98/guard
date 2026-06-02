@echo off
REM Publish GUARD as an unpackaged, self-contained, NativeAOT WinUI 3 app.
REM Runs inside the VC x64 developer environment so the NativeAOT link step
REM can find link.exe and the MSVC / Windows SDK libraries.
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 (
  echo Could not initialise the VC build environment.
  exit /b 1
)
REM The NativeAOT link target calls vswhere.exe by bare name, so the VS
REM Installer folder must be on PATH (vcvars does not add it).
set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"
dotnet publish "%~dp0GUARD-WUI3.csproj" -r win-x64 -c Release -p:PublishAot=true -p:PublishReadyToRun=false
exit /b %errorlevel%

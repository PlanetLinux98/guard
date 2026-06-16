@echo off
REM Build GUARD as a self-contained NativeAOT app and stage it into a GUARD\
REM folder alongside USER_GUIDE.md, then zip that folder to GUARD.zip (the
REM release asset). This is the shipping build.
REM
REM Why a folder, not one .exe: NativeAOT cannot be single-file. WinUI 3 /
REM Windows App SDK ship many native DLLs (Microsoft.UI.Xaml.dll, the WinAppSDK
REM runtime, etc.) that cannot be merged into the AOT binary, so the release is
REM the whole publish folder. GUARD already ships as a folder and is portable
REM (its runtime files - backup-settings.ini, guard-backup.cmd, Logs\ - land next
REM to GUARD.exe in that folder), so this changes only the folder's internals.
REM
REM For a quick non-packaged AOT build use publish-aot.cmd. For an R2R fallback
REM build (no C++ toolchain needed, not the shipped artifact) a plain
REM "dotnet publish -c Release -r win-x64" produces one.
REM
REM Runs inside the VC x64 developer environment so the NativeAOT link step can
REM find link.exe and the MSVC / Windows SDK libraries.
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 (
  echo Could not initialise the VC build environment.
  exit /b 1
)
REM The NativeAOT link target calls vswhere.exe by bare name, so the VS
REM Installer folder must be on PATH (vcvars does not add it).
set "PATH=C:\Program Files (x86)\Microsoft Visual Studio\Installer;%PATH%"

REM ROOT is the project dir without %~dp0's trailing backslash (a trailing "\"
REM before a closing quote would escape the quote and break tar's -C argument).
set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "STAGE=%ROOT%\GUARD"

REM Rebuild the staged folder from scratch so stale files never ship. Publish
REM straight into it (-o) so this works regardless of the bin\ layout AOT uses.
if exist "%STAGE%" rmdir /S /Q "%STAGE%"
dotnet publish "%ROOT%\GUARD-WUI3.csproj" -r win-x64 -c Release ^
  -p:PublishAot=true -p:PublishReadyToRun=false ^
  -o "%STAGE%"
if errorlevel 1 exit /b 1

REM Debug symbols are not shipped; the offline manual is.
del /Q "%STAGE%\*.pdb" >nul 2>nul
copy /Y "%ROOT%\USER_GUIDE.md" "%STAGE%\USER_GUIDE.md" >nul

REM Zip the folder (extracts to GUARD\GUARD.exe). This zip is the release asset.
REM Use bsdtar (built-in tar.exe), not PowerShell's Compress-Archive: the latter
REM on Windows PowerShell 5.1 writes non-spec backslash path separators that
REM break unzip and other extractors. tar -a picks zip format from the extension.
if exist "%ROOT%\GUARD.zip" del /Q "%ROOT%\GUARD.zip"
tar.exe -a -c -f "%ROOT%\GUARD.zip" -C "%ROOT%" GUARD
if errorlevel 1 exit /b 1

echo.
echo Built NativeAOT release: %STAGE%\GUARD.exe
echo Release zip:             %ROOT%\GUARD.zip

@echo off
REM Build GUARD as a single, self-contained, compressed .exe (ReadyToRun, not
REM AOT), then stage it inside a GUARD\ folder alongside README.md and zip that
REM folder to GUARD.zip. Shipping GUARD inside a folder keeps the app portable:
REM its runtime files (backup-settings.ini, guard-backup.cmd, Logs\) land next to
REM the exe in that folder instead of littering wherever a bare exe was saved.
dotnet publish "%~dp0GUARD-WUI3.csproj" -r win-x64 -c Release ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
if errorlevel 1 exit /b 1

REM ROOT is the project dir without %~dp0's trailing backslash (a trailing "\"
REM before a closing quote would escape the quote and break tar's -C argument).
set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "PUB=%ROOT%\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
set "STAGE=%ROOT%\GUARD"

REM Rebuild the staged folder from scratch so stale files never ship.
if exist "%STAGE%" rmdir /S /Q "%STAGE%"
mkdir "%STAGE%"
copy /Y "%PUB%\GUARD.exe" "%STAGE%\GUARD.exe" >nul
copy /Y "%ROOT%\README.md" "%STAGE%\README.md" >nul

REM Zip the folder (extracts to GUARD\GUARD.exe). This zip is the release asset.
REM Use bsdtar (built-in tar.exe), not PowerShell's Compress-Archive: the latter
REM on Windows PowerShell 5.1 writes non-spec backslash path separators that
REM break unzip and other extractors. tar -a picks zip format from the extension.
if exist "%ROOT%\GUARD.zip" del /Q "%ROOT%\GUARD.zip"
tar.exe -a -c -f "%ROOT%\GUARD.zip" -C "%ROOT%" GUARD
if errorlevel 1 exit /b 1

echo.
echo Built single-file:  %STAGE%\GUARD.exe
echo Release zip:        %ROOT%\GUARD.zip

@echo off
REM Build GUARD as a single, self-contained, compressed .exe (ReadyToRun, not
REM AOT) and copy it to the project root as GUARD.exe.
dotnet publish "%~dp0GUARD-WUI3.csproj" -r win-x64 -c Release ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
if errorlevel 1 exit /b 1
copy /Y "%~dp0bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\GUARD.exe" "%~dp0GUARD.exe"
echo.
echo Built single-file: %~dp0GUARD.exe

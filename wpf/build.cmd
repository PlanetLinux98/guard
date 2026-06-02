@echo off
REM ===========================================================================
REM  build.cmd  -  compile GUARD with the in-box .NET Framework C# compiler.
REM  No SDK or project file is required. Run from this folder.
REM ===========================================================================
setlocal
set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

"%CSC%" /target:winexe ^
  /r:WPF\PresentationFramework.dll /r:WPF\PresentationCore.dll ^
  /r:WPF\WindowsBase.dll /r:System.Xaml.dll /r:System.dll /r:System.Core.dll ^
  /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll ^
  /out:GUARD.exe Guard.cs

if errorlevel 1 (
  echo.
  echo BUILD FAILED.
  exit /b 1
)
echo.
echo Built GUARD.exe
endlocal

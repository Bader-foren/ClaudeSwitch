@echo off
rem Builds ClaudeSwitch with the C# compiler that ships with Windows (.NET Framework 4.8).
rem No Visual Studio or .NET SDK needed.
rem
rem   build.cmd                      builds bin\ClaudeSwitch.exe
rem   build.cmd test [dir] [--net]   builds and runs the self-test (fake Claude folders only;
rem                                  --net also checks the real GitHub release download)
rem   set OUTDIR=folder              builds into another folder instead of bin

setlocal
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo Could not find csc.exe. ClaudeSwitch needs .NET Framework 4.8, which comes with Windows 10 and 11.
  exit /b 1
)
set "WPF=%FW%\WPF"
if not defined OUTDIR set "OUTDIR=bin"
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

if /i "%~1"=="test" goto test

"%FW%\csc.exe" /nologo /codepage:65001 /optimize+ /platform:anycpu /target:winexe ^
  /out:"%OUTDIR%\ClaudeSwitch.exe" ^
  /win32icon:assets\icon.ico ^
  /win32manifest:src\app.manifest ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" /r:"%FW%\System.Xaml.dll" ^
  /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Windows.Forms.dll ^
  /resource:src\App\MainWindow.xaml,ClaudeSwitch.MainWindow.xaml ^
  src\AssemblyInfo.cs src\Core\*.cs src\App\*.cs
if errorlevel 1 exit /b 1
echo Built %OUTDIR%\ClaudeSwitch.exe
exit /b 0

:test
"%FW%\csc.exe" /nologo /codepage:65001 /platform:anycpu /target:exe ^
  /out:"%OUTDIR%\SelfTest.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Security.dll ^
  src\AssemblyInfo.cs src\Core\*.cs tests\SelfTest.cs
if errorlevel 1 exit /b 1
"%OUTDIR%\SelfTest.exe" %2 %3
exit /b %errorlevel%

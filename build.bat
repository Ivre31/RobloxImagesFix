@echo off
rem Builds bin\RobloxImagesFix.exe from src\.
rem Requires only Windows: the C# compiler csc.exe ships with .NET Framework 4.
rem Pass "ci" as the first argument to skip the final pause (used by GitHub Actions).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo csc.exe not found. Enable .NET Framework 4 in Windows Features.
    if /i not "%~1"=="ci" pause
    exit /b 1
)
if not exist bin mkdir bin
"%CSC%" /nologo /codepage:65001 /target:winexe /out:bin\RobloxImagesFix.exe /win32icon:src\app.ico /win32manifest:src\app.manifest /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll src\RobloxImagesFix.cs
if errorlevel 1 (
    echo Build failed.
    if /i not "%~1"=="ci" pause
    exit /b 1
)
echo Done: bin\RobloxImagesFix.exe
if /i not "%~1"=="ci" pause
exit /b 0

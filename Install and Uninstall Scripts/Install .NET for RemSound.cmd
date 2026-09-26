@echo off
title Install .NET for RemSound
echo.
echo  RemSound needs the Microsoft .NET 10 Desktop Runtime to run.
echo  This will install it for you using winget (the Windows package manager).
echo.

where winget >nul 2>nul
if errorlevel 1 goto nowinget

winget install --id Microsoft.DotNet.DesktopRuntime.10 -e --source winget --accept-package-agreements --accept-source-agreements
if errorlevel 1 goto failed

echo.
echo  All done. You can now run RemSound.exe.
echo.
echo  Press any key to close this window.
pause >nul
exit /b 0

:nowinget
echo  winget isn't available on this PC (it comes with Windows 10 version 1809 or newer,
echo  and Windows 11). Falling back to the manual download.
goto manual

:failed
echo.
echo  The automatic install didn't finish. Falling back to the manual download.
goto manual

:manual
echo.
echo  Opening the official Microsoft download page. On it, choose
echo  ".NET Desktop Runtime" for "x64", download and run it, then start RemSound.
echo.
echo     https://dotnet.microsoft.com/download/dotnet/10.0
echo.
start "" "https://dotnet.microsoft.com/download/dotnet/10.0"
echo  Press any key to close this window.
pause >nul
exit /b 1

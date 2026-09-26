# Installs the Microsoft .NET 10 Desktop Runtime that RemSound needs, via winget.
# The .cmd next to this file is the easy one - just double-click it. This .ps1 is here for
# people who prefer PowerShell; if Windows blocks it, run:
#   powershell -ExecutionPolicy Bypass -File ".\Install .NET for RemSound.ps1"

$downloadPage = 'https://dotnet.microsoft.com/download/dotnet/10.0'

Write-Host ''
Write-Host 'RemSound needs the Microsoft .NET 10 Desktop Runtime to run.'
Write-Host 'This will install it for you using winget (the Windows package manager).'
Write-Host ''

$winget = Get-Command winget -ErrorAction SilentlyContinue
if ($null -eq $winget) {
    Write-Host "winget isn't available on this PC (it comes with Windows 10 version 1809 or newer, and" -ForegroundColor Yellow
    Write-Host "Windows 11). Opening the manual download page - choose '.NET Desktop Runtime' for 'x64'." -ForegroundColor Yellow
    Start-Process $downloadPage
    Read-Host 'Press Enter to close'
    exit 1
}

winget install --id Microsoft.DotNet.DesktopRuntime.10 -e --source winget --accept-package-agreements --accept-source-agreements
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host "The automatic install didn't finish. Opening the manual download page instead." -ForegroundColor Yellow
    Start-Process $downloadPage
    Read-Host 'Press Enter to close'
    exit 1
}

Write-Host ''
Write-Host 'All done. You can now run RemSound.exe.' -ForegroundColor Green
Read-Host 'Press Enter to close'

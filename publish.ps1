# Builds dist\TextDock.exe. Needs the .NET 10 SDK.
#   .\publish.ps1                       standalone exe, same as the GitHub release (.NET included)
#   .\publish.ps1 -FrameworkDependent   small exe that needs the .NET 10 Desktop Runtime installed
#   .\publish.ps1 -Shortcut             also add a Start menu shortcut
param([switch]$FrameworkDependent, [switch]$Shortcut)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$exe = Join-Path $dist 'TextDock.exe'

$running = @(Get-Process -Name TextDock -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
if ($running) {
    # Ask it to exit cleanly so the WebView2 profile (your pairing) is flushed to disk.
    & $exe --quit
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    if (Get-Process -Id $running.Id -ErrorAction SilentlyContinue) { throw 'TextDock from dist\ is still running.' }
}

$mode = if ($FrameworkDependent) { '--no-self-contained' } else { '--self-contained' }
dotnet publish (Join-Path $root 'TextDock.csproj') -c Release -o $dist $mode
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Write-Host ("built {0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))

if ($Shortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'TextDock.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $dist
    $s.Description = 'Text messages in a compact, always-on-top window'
    $s.Save()
    Write-Host "shortcut $lnk"
}

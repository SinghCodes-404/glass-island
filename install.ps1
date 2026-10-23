# Builds Glass Island and installs it for the current user:
#   - %LOCALAPPDATA%\Programs\GlassIsland   (on the system drive, so it is available as soon as you sign in)
#   - a Start menu shortcut "Glass Island"
#   - starts with Windows (per-user Run entry; turn off in Theme > Island or Task Manager > Startup apps)
# Re-run it after changing the code to update the installed copy. Requires the .NET 10 SDK.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\GlassIsland'
$exe = Join-Path $dest 'GlassIsland.exe'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

# A running copy locks its files, so stop it before building and copying.
Get-Process GlassIsland -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host 'Building...'
& $dotnet publish (Join-Path $src 'GlassIsland.csproj') -c Release -o (Join-Path $src 'publish') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Host "Installing to $dest"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $src 'publish\*') $dest -Recurse -Force

$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Glass Island.lnk'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $dest
$s.IconLocation = "$exe,0"
$s.Description = 'Glass Island'
$s.Save()

Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'GlassIsland' -Value "`"$exe`""

Start-Process $exe
Write-Host 'Done: Glass Island is running, in the Start menu, and starts with Windows.'

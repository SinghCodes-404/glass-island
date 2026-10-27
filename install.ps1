# Builds Glass Island as ONE self-installing program: dist\GlassIsland.exe
# Double-click that file (or copy it anywhere and run it) to install or update Glass Island for your account.
# Pass -Install to also install it right away, without the confirmation dialog.
# Requires the .NET 10 SDK to build; the program itself needs the .NET 10 Desktop Runtime.
param([switch]$Install)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dist = Join-Path $src 'dist'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

Write-Host 'Building dist\GlassIsland.exe ...'
& $dotnet publish (Join-Path $src 'GlassIsland.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o $dist -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Get-ChildItem $dist -Exclude 'GlassIsland.exe' | Remove-Item -Recurse -Force   # keep just the one program file
Write-Host "Built $(Join-Path $dist 'GlassIsland.exe')"

if ($Install) {
    # Wait for the installer only (Start-Process -Wait would also wait for the Glass Island it launches).
    $installer = Start-Process (Join-Path $dist 'GlassIsland.exe') -ArgumentList '--install' -PassThru
    $installer.WaitForExit()
    Write-Host 'Installed: Glass Island is running, in the Start menu and Settings > Apps, and starts when you sign in.'
}

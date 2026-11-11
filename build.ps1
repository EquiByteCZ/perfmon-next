# Build, test, and publish a framework-dependent win-x64 app.
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root
dotnet test .\PerfMonNext.sln -c Release
dotnet publish .\src\PerfMonNext.App\PerfMonNext.App.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\win-x64
Write-Host "Published artifacts\win-x64\PerfMonNext.exe"
Write-Host "The target machine needs the .NET 8 desktop runtime."

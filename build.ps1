param([Parameter(Mandatory=$true)][string]$SdkPath)
$ErrorActionPreference = 'Stop'
$sdkRoot = [IO.Path]::GetFullPath($SdkPath)
$sdkFile = Join-Path $sdkRoot 'StreamUI.Contracts.dll'
if (-not (Test-Path -LiteralPath $sdkFile)) { throw 'Build the Core SDK first: npm run sdk:build. Then pass its sdk/native directory.' }
$sdkAssembly = [Reflection.AssemblyName]::GetAssemblyName($sdkFile)
if ($sdkAssembly.Name -ne 'StreamUI.Contracts' -or $sdkAssembly.Version.Major -ne 1) { throw 'This plugin requires StreamUI Contracts SDK major version 1.' }

& dotnet build (Join-Path $PSScriptRoot 'DesktopInput.csproj') -c Release --configfile (Join-Path $PSScriptRoot 'NuGet.Config') ('-p:StreamUISdkPath=' + $sdkRoot)
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
foreach ($file in @('DesktopInput.dll', 'DesktopInput.deps.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "bin/Release/net10.0-windows/$file") -Destination $PSScriptRoot -Force
}
Write-Output 'Plugin built. Enable desktop-input in Core plugin settings.'

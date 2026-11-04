# Gera o instalador em artifacts\installer. Uso: .\installer\build.ps1 [-SkipTests]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\Sirius\Sirius.csproj'
$publish = Join-Path $root 'artifacts\publish'
$output = Join-Path $root 'artifacts\installer'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup --scope user' }

function Check($step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed with exit code $LASTEXITCODE" }
}

$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (-not $SkipTests) {
    & $dotnet test (Join-Path $root 'Sirius.slnx') --nologo --blame-hang-timeout 120s
    Check 'Tests'
}

$version = (& $dotnet msbuild $project -getProperty:Version).Trim()
Check 'Reading version'

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# O .NET vai junto, para rodar em um Windows sem nada instalado.
# ReadyToRun deixa o programa abrir mais rapido, em troca de um pacote maior.
& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
    -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false --nologo -v quiet
Check 'Publish'

# As bibliotecas nativas trazem simbolos de depuracao enormes, inuteis para quem instala.
Get-ChildItem $publish -Recurse -Filter *.pdb | Remove-Item -Force

& $iscc /Qp "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutputDir=$output" (Join-Path $PSScriptRoot 'sirius.iss')
Check 'Inno Setup'

$installer = Join-Path $output "Sirius-$version-Setup.exe"
$size = [math]::Round((Get-Item $installer).Length / 1MB, 1)
$folder = [math]::Round(((Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Output "Installer: $installer ($size MB, installs $folder MB)"

<#
.SYNOPSIS
    Gera o instalador do OtimizadorPC em artifacts\OtimizadorPC-Setup-<versao>.exe

.DESCRIPTION
    1. Publica o aplicativo (depende do .NET 10 Desktop Runtime, que o instalador baixa se faltar).
    2. Compacta o aplicativo em src\OtimizadorPC.Setup\Payload\payload.zip.
    3. Publica o instalador como arquivo único autossuficiente, com o pacote embutido.

.EXAMPLE
    .\build\Publicar.ps1 -Versao 1.0.0
#>
param(
    [string]$Versao = '1.0.0',
    [string]$Configuracao = 'Release'
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
$artefatos = Join-Path $raiz 'artifacts'
$payload = Join-Path $raiz 'src\OtimizadorPC.Setup\Payload'

if (Test-Path $artefatos) { Remove-Item $artefatos -Recurse -Force }
New-Item -ItemType Directory -Force $payload | Out-Null

Write-Host "==> Publicando o aplicativo $Versao" -ForegroundColor Cyan
dotnet publish (Join-Path $raiz 'src\OtimizadorPC.App\OtimizadorPC.App.csproj') -c $Configuracao -r win-x64 --self-contained false `
    -p:Version=$Versao -o (Join-Path $artefatos 'app')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o aplicativo.' }

Write-Host '==> Compactando o pacote do instalador' -ForegroundColor Cyan
$zip = Join-Path $payload 'payload.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $artefatos 'app\*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host '==> Publicando o instalador' -ForegroundColor Cyan
dotnet publish (Join-Path $raiz 'src\OtimizadorPC.Setup\OtimizadorPC.Setup.csproj') -c $Configuracao -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:Version=$Versao -o (Join-Path $artefatos 'setup')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar o instalador.' }

$final = Join-Path $artefatos "OtimizadorPC-Setup-$Versao.exe"
Copy-Item (Join-Path $artefatos 'setup\OtimizadorPC.Setup.exe') $final
$tamanho = [math]::Round((Get-Item $final).Length / 1MB, 1)
Write-Host "==> Pronto: $final ($tamanho MB)" -ForegroundColor Green

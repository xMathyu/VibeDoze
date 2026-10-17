<#
.SYNOPSIS
  Compila VibeDoze en un único ejecutable autocontenido: dist\VibeDoze.exe

.PARAMETER InstallSdk
  Si no hay SDK de .NET 10, lo instala en %LOCALAPPDATA%\Microsoft\dotnet (sin permisos de administrador).
#>
param([switch]$InstallSdk)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Find-Dotnet {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'),
        (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($exe in $candidates) {
        $sdks = & $exe --list-sdks 2>$null
        if ($sdks -match '^1[0-9]\.') { return $exe }
    }
    return $null
}

$dotnet = Find-Dotnet
if (-not $dotnet) {
    if (-not $InstallSdk) {
        throw "No se encontró el SDK de .NET 10. Ejecuta: .\build.ps1 -InstallSdk"
    }
    Write-Host 'Instalando el SDK de .NET 10 en tu perfil (sin admin)...'
    $installer = Join-Path ([IO.Path]::GetTempPath()) 'dotnet-install.ps1'
    Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $installer -UseBasicParsing
    & $installer -Channel 10.0 -InstallDir (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet') -NoPath
    $dotnet = Find-Dotnet
    if (-not $dotnet) { throw 'La instalación del SDK falló.' }
}
Write-Host "Usando $dotnet"

$icon = Join-Path $root 'src\VibeDoze\Assets\VibeDoze.ico'
if (-not (Test-Path $icon)) { & (Join-Path $root 'scripts\make-icon.ps1') }

$dist = Join-Path $root 'dist'
& $dotnet publish (Join-Path $root 'src\VibeDoze\VibeDoze.csproj') -c Release -o $dist
if ($LASTEXITCODE -ne 0) { throw "La compilación falló ($LASTEXITCODE)." }

$exe = Join-Path $dist 'VibeDoze.exe'
Write-Host ''
Write-Host ("Listo: {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green

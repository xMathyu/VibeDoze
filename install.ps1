<#
.SYNOPSIS
  Instala VibeDoze para tu usuario (sin permisos de administrador).

.DESCRIPTION
  - Copia VibeDoze.exe a %LOCALAPPDATA%\Programs\VibeDoze
  - Crea el acceso directo en el menú Inicio
  - Lo registra para iniciar con Windows (a menos que uses -NoStartup)
  - Opcional: activa los hooks de Claude Code y/o Codex (-Hooks all|claude|codex)
  - Lo abre

.EXAMPLE
  .\install.ps1 -Hooks all
.EXAMPLE
  .\install.ps1 -Uninstall
#>
param(
    [switch]$Uninstall,
    [switch]$NoStartup,
    [ValidateSet('none', 'claude', 'codex', 'all')]
    [string]$Hooks = 'none'
)

$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\VibeDoze'
$exe = Join-Path $target 'VibeDoze.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'VibeDoze.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-VibeDoze {
    if (Test-Path $exe) { & $exe --quit; Start-Sleep -Seconds 2 }
    Get-Process VibeDoze -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    # The watchdog (VibeDoze.Hook.exe) may still be restoring settings; let it finish before touching files.
    Get-Process VibeDoze.Hook -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    # If anything was left applied (lid action / idle timeout), put it back.
    if (Test-Path $exe) { Start-Process $exe -ArgumentList 'restore-lid' -Wait -WindowStyle Hidden }
}

if ($Uninstall) {
    Write-Host 'Desinstalando VibeDoze...'
    Stop-VibeDoze
    if (Test-Path $exe) { Start-Process $exe -ArgumentList 'uninstall-hooks', 'all' -Wait -WindowStyle Hidden }
    Remove-ItemProperty -Path $runKey -Name 'VibeDoze' -ErrorAction SilentlyContinue
    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    Get-Process VibeDoze.Hook -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($dir in @($target, (Join-Path $env:LOCALAPPDATA 'VibeDoze'), (Join-Path $env:APPDATA 'VibeDoze'))) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }
    }
    Write-Host 'VibeDoze se desinstaló. Tus hooks de Claude Code / Codex se quitaron y la acción de la tapa quedó como estaba.' -ForegroundColor Green
    return
}

$source = Join-Path $PSScriptRoot 'dist\VibeDoze.exe'
if (-not (Test-Path $source)) {
    Write-Host 'No hay build; compilando...'
    & (Join-Path $PSScriptRoot 'build.ps1') -InstallSdk
}

Stop-VibeDoze
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $source $exe -Force

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = $target
$lnk.Description = 'Mantiene tu laptop despierta mientras Claude Code o Codex trabajan'
$lnk.Save()

if ($NoStartup) {
    Remove-ItemProperty -Path $runKey -Name 'VibeDoze' -ErrorAction SilentlyContinue
} else {
    Set-ItemProperty -Path $runKey -Name 'VibeDoze' -Value "`"$exe`" --background"
}

if ($Hooks -ne 'none') {
    $p = Start-Process $exe -ArgumentList 'install-hooks', $Hooks -Wait -PassThru -WindowStyle Hidden
    if ($p.ExitCode -ne 0) { Write-Warning 'No se pudieron instalar algunos hooks; revisa el registro desde el panel.' }
}

Start-Process $exe
Write-Host ''
Write-Host "VibeDoze instalado en $exe" -ForegroundColor Green
if ($Hooks -in 'codex', 'all') {
    Write-Host 'Codex: aprueba los hooks nuevos una vez con /hooks (CLI) o en la configuración de hooks de la extensión.' -ForegroundColor Yellow
}
if ($Hooks -in 'claude', 'all') {
    Write-Host 'Claude Code: los hooks se aplican a las sesiones nuevas (reinicia las que ya estaban abiertas).' -ForegroundColor Yellow
}
Write-Host 'Tip: fija el icono en la barra de tareas (Configuración > Personalización > Barra de tareas > Otros iconos).'

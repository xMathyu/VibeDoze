# VibeDoze

App de bandeja para Windows que **mantiene tu laptop despierta mientras Claude Code o Codex están trabajando**, aunque cierres la tapa, y la duerme sola cuando terminan. Incluye un límite de tiempo y protección térmica y de batería, para que no se recaliente dentro de una funda.

Funciona con la extensión de VS Code, Cursor, Windsurf, Antigravity, JetBrains y con cualquier terminal (CLI de `claude` o `codex`).

<p align="center">
  <img src="docs/panel-light.png" width="300" alt="Panel de VibeDoze en modo claro">
  &nbsp;
  <img src="docs/panel-dark.png" width="300" alt="Panel de VibeDoze en modo oscuro">
</p>

## Qué hace

| Función | Detalle |
|---|---|
| **Detección de agentes** | Claude Code y Codex, cada uno con su interruptor. Muestra cada sesión con su IDE o terminal, el proyecto y su estado (trabajando, esperando permiso, terminó). |
| **Tapa cerrada** | Mientras un agente trabaja, cerrar la tapa no suspende la laptop. Cuando termina (más un margen configurable), la laptop se suspende sola. |
| **Luz Slash** | En las ROG Zephyrus con barra Slash en la tapa (G14 y G16 de 2024), mientras trabaja con la tapa cerrada la barra muestra el estado: una luz que recorre la barra mientras un agente trabaja, doble parpadeo si espera tu permiso y respiración lenta al terminar. Después vuelve tu animación de Armoury Crate. Se prueba en *Ajustes → Tapa y energía → Probar*. |
| **Límite de tiempo** | 15 min, 30 min, 1 h, 2 h, 4 h, 8 h o un valor personalizado. Al llegar: suspender, hibernar o apagar. Si estás frente a la pantalla, aparece una cuenta regresiva de 60 s con *Posponer 15 min* y *Cancelar*. |
| **Protección térmica** | Lee los sensores térmicos ACPI de Windows (sin admin). Si se supera el límite (80 °C por defecto) durante un tiempo sostenido (1 min), suspende o hiberna. Con la tapa abierta avisa 30 s antes. Por defecto **solo actúa con batería**: enchufada es normal que se caliente (cargando, jugando con agentes corriendo). Se puede cambiar con "Solo con batería". |
| **Batería baja** | Por debajo del porcentaje elegido deja de mantenerla despierta y, si la tapa está cerrada, la suspende. |
| **Modo manual** | "Mantener despierta ahora", para cuando no hay agentes. |

## Descargar

**[⬇ Descargar VibeDoze.exe](https://github.com/xMathyu/VibeDoze/releases/latest/download/VibeDoze.exe)** (Windows 10/11 x64; todas las versiones están en [Releases](https://github.com/xMathyu/VibeDoze/releases))

Es un único ejecutable autocontenido: no hay que instalar .NET ni tener permisos de administrador.

1. Guárdalo donde quieras (por ejemplo, `%LOCALAPPDATA%\Programs\VibeDoze`) y ábrelo.
2. El ejecutable no está firmado, así que Windows SmartScreen puede advertir. Elige *Más información → Ejecutar de todas formas*.
3. Abre **Ajustes** desde el panel. En *General* activa **Iniciar con Windows** y, en *Agentes → Detección precisa (hooks)*, pulsa **Activar** para Claude Code y Codex.

### Actualizaciones automáticas

VibeDoze revisa las [Releases](https://github.com/xMathyu/VibeDoze/releases) de este repo a los 2 minutos de iniciar y luego cada 6 horas.

- Si hay una versión nueva, la descarga en segundo plano (`VibeDoze.zip`, más liviano) y verifica el `.exe` contra `VibeDoze.exe.sha256`. Si no coincide, la descarta.
- La instala sola cuando lleva 2 minutos sin agentes trabajando y sin cambios aplicados a la tapa. Así nunca reinicia en medio de una sesión.
- El reemplazo lo hace `VibeDoze.Hook.exe` cuando VibeDoze ya se cerró. Si algo falla, deja el ejecutable anterior y lo vuelve a abrir.
- En *Ajustes → Actualizaciones* puedes buscar o instalar al momento, o desactivar *Actualizar automáticamente*. El repositorio de origen se cambia con `UpdateRepository` en `settings.json`.

Para publicar una versión nueva:

```powershell
git tag v1.2.0
git push origin v1.2.0
```

GitHub Actions compila y adjunta `VibeDoze.exe`, `VibeDoze.zip` y `VibeDoze.exe.sha256` al release. Las copias instaladas se actualizan solas.

## Instalación desde el código

Requisitos: Windows 10/11 x64. No necesitas permisos de administrador.

```powershell
.\install.ps1 -Hooks all     # compila si hace falta, instala, activa hooks y abre la app
```

- Se instala en `%LOCALAPPDATA%\Programs\VibeDoze` y crea un acceso directo en el menú Inicio.
- Se registra para iniciar con Windows. Usa `-NoStartup` si no lo quieres.
- `-Hooks claude|codex|all|none` activa la detección precisa. También puedes activarla después desde el panel.
- Para compilar se necesita el SDK de .NET 10. `.\build.ps1 -InstallSdk` lo instala en tu perfil si no lo tienes. El resultado es `dist\VibeDoze.exe`, un único ejecutable autocontenido.

Windows 11 oculta los iconos nuevos de la bandeja. Para fijar el de VibeDoze ve a *Configuración → Personalización → Barra de tareas → Otros iconos de la bandeja del sistema*.

## Uso

- **Clic izquierdo** en el icono abre el panel: estado, sesiones, límite de tiempo y protecciones. Sigue el tema claro u oscuro de Windows.
- **Clic derecho** abre los accesos rápidos: activar o pausar, Claude Code, Codex, tapa cerrada, límite de tiempo, protección térmica, suspender ahora y salir.
- **Ajustes** (en el pie del panel o en el menú) abre la ventana con todas las opciones, agrupadas en General, Agentes, Tapa y energía, Límite de tiempo, Protección y Actualizaciones.

<p align="center">
  <img src="docs/settings-agents-light.png" width="640" alt="Ajustes: agentes y hooks">
</p>

El icono de la bandeja es una taza de café monocroma que combina con la barra de tareas (blanca en tema oscuro, grafito en claro):

<p align="center"><img src="docs/tray-icons.png" alt="Iconos de la bandeja"></p>

| Icono | Estado |
|---|---|
| Taza sin vapor | En reposo, esperando a que un agente trabaje |
| Taza con vapor y punto verde | Manteniendo despierta |
| Taza con punto ámbar | En espera tras una suspensión automática, o cuenta regresiva |
| Taza con vapor y punto rojo | Alerta térmica |
| Taza atenuada | En pausa |

Cuando se cumple el límite de tiempo con la pantalla encendida, aparece una cuenta regresiva de 60 s:

<p align="center"><img src="docs/countdown-light.png" width="300" alt="Cuenta regresiva"></p>

## Cómo sabe que un agente está trabajando

1. **Procesos.** Busca `claude.exe` (CLI nativo y extensiones), `node … @anthropic-ai/claude-code`, `codex.exe` (CLI y el `app-server` de la extensión) y `node … @openai/codex`. Excluye Claude Desktop. También identifica dónde corre cada agente: VS Code, Cursor, Windows Terminal y otros.
2. **Hooks (recomendado).** Cada evento del agente llega al instante por un *named pipe* a través de `VibeDoze.Hook.exe`, un ayudante de 21 KB que se ejecuta en segundo plano (`async`) y nunca frena al agente.
   - `UserPromptSubmit` / `PostToolUse` → trabajando
   - `PermissionRequest` / `Notification` de permiso → esperando
   - `Stop` / `StopFailure` / `Interrupt` → terminó
   - `SessionEnd` → cerrada
3. **CPU (respaldo sin hooks).** Si el árbol de procesos del agente usa más de ~1.5 % de un núcleo, cuenta como trabajando. Como el modelo puede "pensar" sin usar CPU, en este modo el margen mínimo es de 5 min.

Notas de los hooks:

- **Claude Code**: se agregan a `~/.claude/settings.json` sin tocar tus otros ajustes ni hooks, y se guarda un respaldo `settings.json.vibedoze-backup`. Los comentarios `//` del archivo no se conservan al reescribirlo; están en el respaldo. Aplican a las sesiones **nuevas**.
- **Codex**: se escriben en `~/.codex/hooks.json`. Codex exige **aprobar los hooks nuevos una vez**, con `/hooks` en la CLI o en la configuración de hooks de la extensión. Funcionan aunque uses el sandbox de Windows de Codex (`[windows] sandbox = "elevated"`). VibeDoze no toca tu `notify`.
- Si un turno se interrumpe sin aviso, se da por terminado tras 10 min sin señales de vida (`HookStaleMinutes`).

## Tapa cerrada y Modern Standby

Para seguir trabajando con la tapa cerrada, VibeDoze cambia temporalmente la acción *"Al cerrar la tapa"* a *"No hacer nada"* en **todos** tus planes de energía. Así se cubre también el caso en que la app del fabricante cambia de plan (Silent, Turbo…). Lo deshace cuando los agentes terminan.

**Siempre se restaura**, aunque la app se cierre de golpe:

- Los valores originales se guardan en `state.json` antes de cambiarlos.
- Un *watchdog* (unos 15 MB) los restaura si VibeDoze muere.
- Al iniciar sesión, la app restaura lo que haya quedado pendiente.
- Al apagar o cerrar sesión, Windows avisa a la app y esta también restaura.

La **luz Slash** sigue la misma regla: antes de animarla, VibeDoze guarda en `state.json` la animación, el brillo y los ajustes de la barra, y al terminar los vuelve a escribir tal cual. Para que se vea con la tapa cerrada activa *"Mantener la iluminación con la tapa cerrada"* mientras dura, y luego la deja como la tenías en Armoury Crate.

Muchas laptops modernas usan **Modern Standby (S0)**; `powercfg /a` te dice si la tuya es una de ellas. En ellas Windows no permite "suspender ahora" por API con la pantalla apagada. Cuando un agente termina con la tapa cerrada, VibeDoze baja temporalmente *"Suspender tras inactividad"* a 1 minuto para que Windows entre en reposo por sí mismo, y restaura el valor al despertar. Si en 2½ minutos la laptop sigue encendida (por ejemplo, porque otra app la mantiene despierta), la **hiberna**. Con la tapa abierta, "suspender" apaga la pantalla, que es lo que activa el Modern Standby.

## Archivos

| Ruta | Contenido |
|---|---|
| `%LOCALAPPDATA%\Programs\VibeDoze\VibeDoze.exe` | La app |
| `%LOCALAPPDATA%\VibeDoze\bin\VibeDoze.Hook.exe` | Ayudante de hooks y watchdog (lo extrae la app) |
| `%LOCALAPPDATA%\VibeDoze\vibedoze.log` | Registro de lo que hizo: cambios de tapa, acciones de energía, transiciones de estado |
| `%LOCALAPPDATA%\VibeDoze\state.json` | Valores originales mientras hay un cambio aplicado |
| `%LOCALAPPDATA%\VibeDoze\update\` | Versión nueva ya descargada y verificada, esperando a instalarse |
| `%APPDATA%\VibeDoze\settings.json` | Ajustes |

Ajustes avanzados, solo editables en `settings.json` (*Ajustes → General → Ajustes avanzados*):

| Clave | Por defecto | Qué hace |
|---|---|---|
| `CpuActiveThresholdPercent` | `1.5` | % de un núcleo que cuenta como "trabajando" |
| `CpuOnlyMinGraceMinutes` | `5` | Margen mínimo cuando no hay hooks |
| `HookStaleMinutes` | `10` | Turno sin señales (hooks ni CPU) → terminado |
| `HookHardCapMinutes` | `30` | Turno sin ningún hook → terminado, aunque haya CPU |
| `UpdateRepository` | `xMathyu/VibeDoze` | De qué repo de GitHub vienen las actualizaciones |
| `WatchOthers` / `OtherAgents` | `false` | Vigila otros agentes por nombre de exe (`opencode`, `aider`) o fragmento de comando (`@google/gemini-cli`) |

## Línea de comandos

```text
VibeDoze.exe                       abre la app (o su panel si ya está abierta)
VibeDoze.exe --background          inicia solo en la bandeja
VibeDoze.exe --quit                cierra la instancia abierta
VibeDoze.exe restore-lid           restaura la acción de la tapa, la suspensión por inactividad y la luz Slash
VibeDoze.exe slash-preview         muestra las animaciones de la luz Slash (~13 s) y restaura la tuya
VibeDoze.exe install-hooks [claude|codex|all]
VibeDoze.exe uninstall-hooks [claude|codex|all]
VibeDoze.exe snapshot [carpeta] [light|dark]
                                   guarda capturas PNG del panel, los ajustes, la cuenta regresiva y los iconos
```

## Solución de problemas

- **La laptop no se suspende al cerrar la tapa, ni siquiera sin agentes.** Revisa *Ajustes → General → Diagnóstico → Al cerrar la tapa, ahora*. Ejecuta `VibeDoze.exe restore-lid`, o restáuralo a mano: `powercfg /setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1` y `powercfg /setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1`.
- **Codex aparece como "detección por CPU" con los hooks instalados.** Aprueba los hooks en Codex (`/hooks`).
- **¿Qué está haciendo?** Abre el *Registro de actividad* en *Ajustes → General → Diagnóstico*. Con una consola de administrador, `powercfg /requests` muestra `VibeDoze: Claude Code trabajando` mientras la mantiene despierta.

## Desinstalar

```powershell
.\install.ps1 -Uninstall
```

Quita los hooks, la entrada de inicio, el acceso directo y los archivos, y deja la acción de la tapa como estaba.

Si usaste el `.exe` descargado, ejecuta `VibeDoze.exe uninstall-hooks` (o pulsa **Quitar** en *Ajustes → Agentes*), apaga *Iniciar con Windows*, sal de la app y borra el exe y las carpetas `%LOCALAPPDATA%\VibeDoze` y `%APPDATA%\VibeDoze`.

## Estructura

```text
src/VibeDoze/           App WPF (.NET 10, autocontenida)
  Core/Engine.cs          lógica principal: despierta, tapa, temporizador, térmica, batería
  Core/PowerController.cs solicitudes de energía, acción de la tapa, suspender/hibernar
  Core/AgentScanner.cs    detección de agentes por procesos y CPU
  Core/AgentTracker.cs    estado por sesión (hooks + CPU)
  Core/Hooks.cs           servidor del named pipe y ayudante
  Core/Integrations.cs    instalar/quitar hooks de Claude Code y Codex; inicio con Windows
  Core/ThermalMonitor.cs  sensores térmicos ACPI (contadores PDH)
  Core/SlashLight.cs      animación de la barra Slash (ROG) por HID y su restauración
  UI/                     panel, ajustes, cuenta regresiva, icono de bandeja, tema claro/oscuro
scripts/make-icon.ps1   genera el icono de la app (Assets/VibeDoze.ico)
docs/                   capturas del README (VibeDoze.exe snapshot)
src/VibeDoze.Hook/      Ayudante de hooks y watchdog (.NET Framework 4.8, 21 KB)
```

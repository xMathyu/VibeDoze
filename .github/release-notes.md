Mantiene tu laptop despierta mientras Claude Code o Codex trabajan, incluso con la tapa cerrada, y la duerme sola cuando terminan. Incluye límite de tiempo y protección térmica y de batería.

### Novedades

- **Con la tapa abierta, la laptop ya no se suspende mientras un agente trabaja.** En las laptops con Modern Standby, Windows entraba en reposo al vencer *Suspender tras inactividad* aunque VibeDoze pidiera mantenerla despierta. Ahora ese ajuste queda en *Nunca* mientras trabajan y se restaura al terminar. La pantalla se sigue apagando según tu configuración.
- **Luz Slash** (laptops ROG con barra Slash, como las Zephyrus G14 y G16 desde 2024): la barra de la tapa es una luz de estado. Mientras usas la laptop queda apagada; con la tapa cerrada muestra si un agente trabaja (una luz que la recorre), necesita tu respuesta (doble parpadeo) o terminó (respiración lenta). Funciona también con batería. Pruébala en *Ajustes → Tapa y energía*; si prefieres ver tu animación de Armoury Crate con la tapa abierta, desactiva *Apagada mientras usas la laptop*.
- Las sesiones abiertas pero inactivas (por ejemplo, una pestaña de Claude Code en VS Code) ya no aparecen como "Trabajando" cuando los hooks están activos, ni mantienen la laptop despierta.

### Instalación

1. Descarga **VibeDoze.exe** desde *Assets*. Es un único ejecutable que no necesita instalar .NET.
2. Guárdalo donde quieras (por ejemplo, `%LOCALAPPDATA%\Programs\VibeDoze`) y ábrelo.
3. Windows SmartScreen puede advertir porque el ejecutable no está firmado. Elige *Más información → Ejecutar de todas formas*.
4. Abre **Ajustes** desde el panel. En *General* activa **Iniciar con Windows** y, en *Agentes → Detección precisa (hooks)*, pulsa **Activar** para Claude Code y Codex. En Codex, aprueba los hooks una vez con `/hooks`.

Si ya tienes VibeDoze, se actualiza solo cuando no hay agentes trabajando.

Requisitos: Windows 10/11 x64.

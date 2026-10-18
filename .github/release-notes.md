Mantiene tu laptop despierta mientras Claude Code o Codex trabajan, incluso con la tapa cerrada, y la duerme sola cuando terminan. Incluye límite de tiempo y protección térmica y de batería.

### Novedades

- **1.3.1:** la luz Slash también se enciende con batería.
- **Luz Slash** (ROG Zephyrus G14 y G16 de 2024): con la tapa cerrada, la barra de la tapa muestra si un agente trabaja (una luz que la recorre), espera tu permiso (doble parpadeo) o terminó (respiración lenta). Después vuelve tu animación de Armoury Crate. Pruébala en *Ajustes → Tapa y energía*.
- Las sesiones abiertas pero inactivas (por ejemplo, una pestaña de Claude Code en VS Code) ya no aparecen como "Trabajando" cuando los hooks están activos, ni mantienen la laptop despierta.

### Instalación

1. Descarga **VibeDoze.exe** desde *Assets*. Es un único ejecutable que no necesita instalar .NET.
2. Guárdalo donde quieras (por ejemplo, `%LOCALAPPDATA%\Programs\VibeDoze`) y ábrelo.
3. Windows SmartScreen puede advertir porque el ejecutable no está firmado. Elige *Más información → Ejecutar de todas formas*.
4. Abre **Ajustes** desde el panel. En *General* activa **Iniciar con Windows** y, en *Agentes → Detección precisa (hooks)*, pulsa **Activar** para Claude Code y Codex. En Codex, aprueba los hooks una vez con `/hooks`.

Si ya tienes VibeDoze, se actualiza solo cuando no hay agentes trabajando.

Requisitos: Windows 10/11 x64.

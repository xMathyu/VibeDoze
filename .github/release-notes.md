Mantiene tu laptop despierta mientras Claude Code o Codex trabajan, incluso con la tapa cerrada, y la duerme sola cuando terminan. Incluye límite de tiempo y protección térmica y de batería.

### Novedades

- **Agentes en un servidor por SSH.** Si usas Claude Code o Codex en otra máquina, como una instancia EC2, a la que entras por SSH desde la laptop (en la terminal o con VS Code Remote-SSH), sus hooks llegan por el mismo túnel SSH. Así la laptop no se suspende mientras el agente trabaja, porque eso cortaría tu conexión. Actívalo en *Ajustes → Agentes → Agentes en un servidor (SSH)*: agrega la línea `RemoteForward` que te muestra a tu `~/.ssh/config` y pega en el servidor el comando que copia el botón. Las sesiones aparecen en el panel con el nombre del servidor.
- **Con la tapa abierta, la laptop ya no se suspende mientras un agente trabaja.** En las laptops con Modern Standby, Windows entraba en reposo al vencer *Suspender tras inactividad* aunque VibeDoze pidiera mantenerla despierta. Ahora ese ajuste queda en *Nunca* mientras trabajan y se restaura al terminar. La pantalla se sigue apagando según tu configuración.
- **Luz Slash** (laptops ROG con barra Slash, como las Zephyrus G14 y G16 desde 2024): la barra de la tapa es una luz de estado. Mientras usas la laptop queda apagada; con la tapa cerrada muestra si un agente trabaja (una luz que la recorre), necesita tu respuesta (doble parpadeo) o terminó (respiración lenta). Funciona también con batería. Pruébala en *Ajustes → Tapa y energía*; si prefieres ver tu animación de Armoury Crate con la tapa abierta, desactiva *Apagada mientras usas la laptop*.
- **Si la suspendes tú, se duerme.** Con el botón de encendido o *Inicio → Suspender*, la laptop se suspende y sigue dormida aunque un agente esté trabajando. VibeDoze deja de mantenerla despierta hasta que vuelvas a usarla (abres la tapa o la pantalla lleva unos segundos encendida, también un monitor externo). Antes, la luz Slash se volvía a encender justo al suspenderse y quedaba prendida toda la suspensión.
- Las sesiones abiertas pero inactivas (por ejemplo, una pestaña de Claude Code en VS Code) ya no aparecen como "Trabajando" cuando los hooks están activos, ni mantienen la laptop despierta.
- **Codex en VS Code ya no mantiene la laptop despierta si no lo usas.** La extensión de Codex arranca un proceso en segundo plano con cada ventana del editor, y su CPU sigue a la interfaz (arranque, abrir el panel, refrescos cada ~4,5 min), no a los turnos. Sin hooks, VibeDoze la leía como trabajo y la sesión aparecía como "Terminó" sin parar. Ahora la extensión de Codex se detecta solo con hooks; si la usas, actívalos en *Ajustes → Agentes*.
- La detección por CPU (sin hooks) ignora los picos cortos y el primer minuto de cada proceso, que es su arranque.
- Claude Code y Codex se muestran con sus logos en el panel y en *Ajustes → Agentes*.
- **Protección térmica al instante.** Apenas la temperatura llega al límite o lo supera, suspende o hiberna, sin esperar: ya no existe la opción *Durante al menos*. Con la tapa cerrada, si la suspensión no entra en 75 s, hiberna. Con la tapa abierta avisa 30 s antes. Al despertar espera unos segundos a saber si abriste la tapa o la enchufaste antes de decidir.
- El aviso de temperatura ya no se cancela solo cuando VibeDoze tarda unos segundos en responder. Antes, si la app quedaba ocupada más de 10 s, creía que la laptop acababa de despertar y anulaba la cuenta regresiva; ahora eso solo cuenta tras una suspensión de verdad.
- Si eliges *Ignorar 15 min* en el aviso de temperatura, la protección vuelve en cuanto cierras la tapa (o, con la tapa ya cerrada y un monitor externo, cuando se apaga la pantalla). Antes quedaba en pausa los 15 min aunque guardaras la laptop en la funda.
- El panel ya no redondea la temperatura hacia arriba: "60 °C" significa 60,0 o más, igual que el límite. Antes, 69,6 °C se veía como 70 y no contaba para un límite de 70.
- **Registro de temperatura.** Mientras hay agentes trabajando o la tapa está cerrada, el registro (`%LOCALAPPDATA%\VibeDoze\vibedoze.log`) anota la temperatura cada minuto con su pico, si está enchufada, la tapa y si la protección está activa (o por qué no). También anota cada vez que la protección actúa y los cambios de la configuración térmica.

### Instalación

1. Descarga **VibeDoze.exe** desde *Assets*. Es un único ejecutable que no necesita instalar .NET.
2. Guárdalo donde quieras (por ejemplo, `%LOCALAPPDATA%\Programs\VibeDoze`) y ábrelo.
3. Windows SmartScreen puede advertir porque el ejecutable no está firmado. Elige *Más información → Ejecutar de todas formas*.
4. Abre **Ajustes** desde el panel. En *General* activa **Iniciar con Windows** y, en *Agentes → Detección precisa (hooks)*, pulsa **Activar** para Claude Code y Codex. En Codex, aprueba los hooks una vez con `/hooks`.

Si ya tienes VibeDoze, se actualiza solo cuando no hay agentes trabajando.

Requisitos: Windows 10/11 x64.

# Hardware Monitor Widget (CPU / GPU / FPS)

Widget flotante y minimalista para Windows 10/11 (WPF / .NET 8) que muestra en tiempo real la temperatura y carga de **CPU**, **GPU** y los **FPS** de la ventana o juego activo, manteniéndose sobre la Barra de Tareas sin robar foco.

## Compatibilidad de hardware

- **CPU (Intel y AMD):** Compatible mediante `LibreHardwareMonitorLib` (`0.9.6`) y **PawnIO**. Lee temperaturas (`Tctl`/`Tdie`, `Package`, sensores de placa base) y uso total.
- **GPU (NVIDIA, AMD e Intel):** Compatible mediante `LibreHardwareMonitorLib`. Prioriza GPU dedicada NVIDIA si hay GPU integrada + dedicada.
- **FPS (cualquier GPU):** Captura eventos de presentación gráfica de Windows vía ETW (`Microsoft-Windows-DXGI`, `Microsoft-Windows-D3D9` y `Microsoft-Windows-DxgKrnl`), compatible con juegos DirectX, Vulkan y OpenGL sin requerir programas externos como RTSS.

## Características

- **Modo Normal y Modo Minimalista** (solo valores principales).
- **Disposición Vertical u Horizontal**.
- **Control de Opacidad y Tamaño (escala)**.
- **Siempre encima (`Topmost`)** sobre la Barra de Tareas sin parpadeos al hacer clic en ella.
- **Bloquear posición** y guardado automático de ajustes en `widget_settings.json`.
- **Inicio automático con Windows elevado sin UAC** tras conceder permisos de Administrador una sola vez (mediante tarea programada `HardwareWidget` en el Programador de tareas).
- **Opción `Mostrar FPS`** activable o desactivable desde el menú contextual (clic derecho).

## Requisitos

1. **Windows 10 / Windows 11 (x64)**.
2. **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** (para compilar y generar el `.exe`).
3. **[PawnIO](https://pawnio.eu/)** instalado en el sistema (necesario para que `LibreHardwareMonitorLib` `0.9.6` lea sensores de temperatura de CPU sin usar el controlador heredado `WinRing0`, que Windows Defender suele bloquear).

### Instalación rápida por terminal (`winget`)

```powershell
winget install Microsoft.DotNet.SDK.8
winget install namazso.PawnIO
```

> **Nota:** Si acabas de instalar `.NET 8 SDK`, cierra y vuelve a abrir la terminal antes de ejecutar `dotnet`. Si solo vas a ejecutar una compilación portable (`publish`) en otro PC sin compilar nada, en ese PC únicamente necesitas instalar **PawnIO** para leer la temperatura de la CPU.

## Cómo generar el `.exe`

1. Clona o descarga el repositorio y abre una terminal en la carpeta del proyecto:
   ```powershell
   cd ruta\al\repositorio\witget
   ```
2. Restaura dependencias y compila en modo `Release`:
   ```powershell
   dotnet build -c Release
   ```
   Esto generará el ejecutable en:
   ```text
   bin\Release\net8.0-windows\HardwareWidget.exe
   ```
   *(Este `.exe` depende del resto de archivos `.dll` en esa carpeta y de tener .NET 8 instalado en el equipo).*

### Generar un `.exe` único portable (para pasar a otro PC)

Si quieres compartir **un solo archivo `.exe`** que funcione en otro PC sin tener que instalar .NET 8 ni copiar carpetas adicionales:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

El ejecutable único listo para compartir se generará en:
```text
bin\Release\net8.0-windows\win-x64\publish\HardwareWidget.exe
```

## Cómo ejecutarlo

Puedes iniciarlo de cualquiera de estas formas:

- Ejecutando directamente `bin\Release\net8.0-windows\HardwareWidget.exe`.
- Usando `Iniciar_Widget_Silencioso.vbs` (sin ventana de consola).
- Usando `iniciar_widget.bat`.

### Primer arranque (permiso de Administrador una sola vez)

1. La primera vez que lo abras, si aún no tiene permisos elevados, verás el botón **🛡️ Clic para activar temp. CPU (Admin)** (o en el menú clic derecho: **🛡️ Reiniciar como Administrador**).
2. Haz clic y acepta el aviso de UAC **esa única vez**.
3. Al reiniciarse elevado, el widget registrará automáticamente la tarea programada `HardwareWidget` con privilegios elevados para que los siguientes arranques (al iniciar Windows o al abrirlo manualmente) inicien como Administrador sin volver a mostrar UAC y con lectura de **CPU** y **FPS** activa desde el inicio.

## Controles (clic derecho sobre el widget)

- **✨ Modo Minimalista (Solo Temp)**
- **🎮 Mostrar FPS**
- **📐 Disposición** (Vertical / Horizontal)
- **🔆 Opacidad** y **🔍 Tamaño del Widget**
- **📌 Siempre encima (Topmost)**
- **🔒 Bloquear posición**
- **🚀 Iniciar con Windows**
- **❌ Cerrar Widget**

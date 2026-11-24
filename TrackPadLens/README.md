# TrackPadLens

Intelligent Windows 10/11 (x64, .NET 8, WPF + Win32/HID + WinRT) desktop application unifying four cohesive workflows:
1. **Standard Overlay Mode (`Alt+D`)** — Lightweight transparent click-through desktop annotation overlay & floating pill toolbar.
2. **Trackpad Lens Mode (`Alt+T` / `Alt+D`)** — Precision Touchpad HID absolute writing tablet (`UsagePage 0x0D`, `Usage 0x05`) with a floating Writing Lens, independent external mouse movement, touchpad cursor suppression, Size Lock, Lens Box Color & Opacity, 1-Euro smoothing, adaptive palm rejection, and zero-dot 2-finger scroll filtering.
3. **Whiteboard Mode (`Alt+W`)** — Apple Notes-inspired infinite canvas & paged notebook (`Blank`, `Ruled`, `Dotted`, `Grid`) with SpatialIndex viewport culling, smooth zoom/pan, Pen/Pencil/Marker/Highlighter/Eraser/Shapes/Connectors/Text/Equations/Sticky Notes, Lasso & Marquee selection, contextual Magic Menu, Notebooks/Pages sidebar, Autosave & crash recovery, `.tlproject` ZIP archive persistence, and PNG/JPEG/Flattened PDF export.
4. **Smart Study Workspace** — Native read-only PDF (`Windows.Data.Pdf`) & DOCX (optional installed LibreOffice high-fidelity PDF conversion + native Open XML fallback) reader with **Document Focus / Split Workspace / Free Workspace** layouts, thumbnails, page rotation, **Send to Whiteboard** (preserving source document & page metadata), Region Screenshot Capture (`Alt+Shift+S`), Universal Clipboard Paste (`Ctrl+V`), Offline Windows OCR (`Windows.Media.Ocr`), Handwriting Recognition / Clean Handwriting / Typeset, Offline Symbolic Math Solver, and Smart Diagram Reconstruction (Clean Image + Editable Vector Objects).

---

## Requirements (Windows Build & Runtime)
- **OS**: Windows 10 (2004 / Build 19041+) or Windows 11, x64
- **Build SDK**: [.NET 8.0 SDK (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Hardware**: Built-in Windows Precision Touchpad exposing HID Digitizer (`UsagePage 0x0D`, `Usage 0x05`) + external USB or Bluetooth mouse (Whiteboard & Smart Study also work seamlessly with mouse, touchscreen, or stylus)
- **Optional**: Windows OCR Language Pack (for offline OCR/handwriting recognition) and LibreOffice (for high-fidelity Word DOCX pagination)
- **Privileges**: Standard user (`asInvoker`) — administrator rights are never required

---

## Quick Build & Launch Commands

All builds stage a directly launchable `TrackPadLens.exe` in the `TrackPadLens/` project root **only when the build succeeds** (failed builds never overwrite an existing root executable).

### 1. Debug Build (with `.pdb` Symbols)
```powershell
.\build.ps1 -Mode Debug
# Or:
.\Scripts\Build-Debug.ps1
# Or:
build-debug.cmd
```

### 2. Release Build (Framework-Dependent)
```powershell
.\build.ps1 -Mode Release
# Or:
.\Scripts\Build-Release.ps1
```

### 3. Standalone Self-Contained Single-File Publish (`win-x64`)
```powershell
.\build.ps1 -Mode Publish
# Or:
.\Scripts\Publish.ps1
```

### 4. Windows Setup Installer (`TrackPadLens-Setup-x64.exe`)
Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`):
```powershell
.\build.ps1 -Mode Installer
# Or:
build-installer.cmd
```
Outputs `dist\TrackPadLens-Setup-x64.exe` (per-user or all-users install, Start Menu & Desktop shortcuts, optional Start with Windows, single-instance upgrade safety, and full Apps & Features uninstaller).

### 5. Run Automated Unit Tests
```powershell
.\build.ps1 -Mode Test
# Or:
dotnet test Tests\TrackPadLens.Tests.csproj -c Release
```

### 6. Automated GitHub Actions Build (`.github/workflows/build-windows-exe.yml`)
Builds on `windows-latest`, runs the entire xUnit test suite, and uploads:
- `TrackPadLens-win-x64-Setup-Installer` (`TrackPadLens-Setup-x64.exe` + `SHA256SUMS.txt`)
- `TrackPadLens-win-x64-Standalone` (`TrackPadLens.exe` + `SHA256SUMS.txt`)
- `TrackPadLens-win-x64-Debug` (`TrackPadLens.exe` + `TrackPadLens.pdb`)
- `TrackPadLens-win-x64-FrameworkDependent`

---

## Architecture Overview

```text
TrackPadLens/
├── Core/           # WorkspaceModes & ModeManager (4 environments)
├── Input/          # NormalizedInputEvent model (Touchpad/Mouse/Touchscreen/Stylus)
├── Hardware/       # Precision Touchpad Raw HID discovery, parser & cursor isolation
├── Processing/     # 1-Euro Filter, CoordinateMapper, Adaptive PalmRejection, StrokeProcessor
├── Rendering/      # Low-latency WPF DrawingVisual InkEngine (Overlay)
├── Objects/        # Unified polymorphic DocumentObject model + ObjectManager
├── Selection/      # Uniform-grid SpatialIndex + SelectionManager (Marquee & Lasso)
├── Commands/       # CommandManager (200-step Undo/Redo) + ShortcutManager (scoped conflict check)
├── Storage/        # SHA-256 AssetStore, .tlproject ZIP ProjectSerializer, AutosaveService
├── Documents/      # Native Windows PdfEngine, DocxEngine, SimplePdfWriter, DocumentExporter
├── Clipboard/      # Universal ClipboardService (PNG alpha, DIB, files, Unicode text)
├── Intelligence/   # Offline Windows OcrService, SmartInkEngine, MathSolverEngine, DiagramRecognitionEngine
├── UI/             # OverlayWindow, FloatingToolbar, WorkspaceWindow, WorkspaceCanvasControl,
│                   # MagicDialogs, RegionCaptureWindow, SettingsWindow, CalibrationWindow
├── Native/         # Win32Interop, HotkeyManager, MouseHookManager, WindowStyles
├── Services/       # ConfigurationService, StartupService, ScreenshotService, TrayService
├── Scripts/        # Build-Debug.ps1, Build-Release.ps1, Publish.ps1
├── Installer/      # TrackPadLens.iss (Inno Setup 6)
└── Tests/          # xUnit test suite covering hardware math, workspace, storage, documents & math solver
```

---

## Default Controls & Shortcuts

### Global System Shortcuts
- **`Alt + D`**: Toggle Doodle Mode / Standard Overlay ON / OFF
- **`Alt + T`**: Activate Trackpad Lens Mode directly
- **`Alt + W`**: Open / Focus Whiteboard & Smart Study Workspace
- **`Alt + Shift + S`**: Multi-Monitor Region Screenshot Capture (inserts directly into Workspace)
- **`Ctrl + Z`** / **`Ctrl + S`**: Overlay Undo / Save Full Snapshot

### Doodle-Mode-Only Quick Keys (Active ONLY while Doodle Mode is ON)
- **`E`**: Whole-stroke Eraser
- **`1` / `2` / `3`**: Thin / Medium / Thick stroke size
- **`D`**: Return to last-used Pen color or cycle through Pen colors
- **`C`**: Clear all overlay ink (reversible via `Ctrl + Z`)
- **Mouse Wheel (when Lens Size Unlocked)**: Smoothly resize the Writing Lens; click padlock on floating bar to lock size
- **Hold `Shift` or Right-Click**: Anchor Lock (freeze Lens position while drawing)

### Workspace-Local Shortcuts (Active ONLY inside TrackPadLens Workspace — never stolen from other apps)
- **`V` / `L` / `P` / `H` / `E`**: Select / Lasso / Pen / Highlighter / Eraser
- **`Ctrl + Space`**: Toggle Contextual Magic Menu
- **`Ctrl + Shift + T`**: Convert Selected Handwriting to Text
- **`Ctrl + Enter`**: Recognize & Solve Selected Equation
- **`Ctrl + Z` / `Ctrl + Y`**: Undo / Redo
- **`Ctrl + V` / `Ctrl + D`**: Universal Paste / Duplicate Selection
- **`Ctrl + G` / `Ctrl + Shift + G`**: Group / Ungroup Selection
- **`Ctrl + +` / `Ctrl + -` / `Ctrl + 0`**: Zoom In / Zoom Out / Fit Canvas
- **`Ctrl + S` / `Ctrl + N` / `Ctrl + F` / `Tab`**: Save Workspace / New Page / Search / Toggle Toolbar

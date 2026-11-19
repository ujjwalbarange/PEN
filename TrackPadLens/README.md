# TrackPadLens

High-performance Windows 10/11 (x64, .NET 8, WPF + Win32/HID) writing tablet application that transforms a laptop's built-in **Precision Touchpad** into an absolute-positioned digital writing tablet while an external USB or Bluetooth mouse independently controls a floating transparent **Writing Lens** (`#00D2FF`).

---

## Requirements (Windows Build & Runtime)
- **OS**: Windows 10 (1903+) or Windows 11, x64
- **Build SDK**: [.NET 8.0 SDK (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Hardware**: Built-in Windows Precision Touchpad exposing HID Digitizer (`UsagePage 0x0D`, `Usage 0x05`) + external USB or Bluetooth mouse
- **Privileges**: Standard user (`asInvoker`) — administrator rights are never required

---

## Quick Build & Launch Commands

All builds stage a directly launchable `TrackPadLens.exe` in the `TrackPadLens/` project root **only when the build succeeds** (failed builds never overwrite an existing root executable).

### 1. Debug Build (with `.pdb` Symbols for Debugging)
```powershell
# PowerShell (builds Debug x64 and stages TrackPadLens.exe + TrackPadLens.pdb in root)
.\build.ps1 -Mode Debug

# Or double-click / run from cmd.exe:
build-debug.cmd

# Or directly via dotnet CLI:
dotnet build TrackPadLens.csproj -c Debug -p:Platform=x64 -r win-x64 --no-self-contained
```
Launch or attach debugger directly to:
```powershell
.\TrackPadLens.exe
```
You can also open `TrackPadLens.sln` in **Visual Studio 2022** or **VS Code (C# Dev Kit)** and press **F5** to step through `RawInputManager`, `HidTouchpadReader`, `OneEuroFilter`, and `OverlayWindow`.

### 2. Release Build (Framework-Dependent, Fast Build & Small Footprint)
```powershell
.\build.ps1 -Mode Release
# Or:
dotnet build TrackPadLens.csproj -c Release -p:Platform=x64 -r win-x64 --no-self-contained
```

### 3. Standalone Self-Contained Single-File Publish (`win-x64`)
```powershell
.\build.ps1 -Mode Publish
# Or:
dotnet publish TrackPadLens.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```
Outputs standalone executable to both `publish\TrackPadLens.exe` and `TrackPadLens.exe` in the project root.

### 4. Run Automated Unit Tests
```powershell
.\build.ps1 -Mode Test
# Or:
dotnet test Tests\TrackPadLens.Tests.csproj -c Release
```

### 5. Automated GitHub Actions Build (`.github/workflows/build-windows-exe.yml`)
Push to `main`/`master`, open a PR, tag `v*`, or click **Run workflow** in the GitHub **Actions** tab to build on `windows-latest`:
- Runs `dotnet test Tests\TrackPadLens.Tests.csproj -c Release`
- Uploads **`TrackPadLens-win-x64-Standalone`** (`TrackPadLens.exe` + `SHA256SUMS.txt`)
- Uploads **`TrackPadLens-win-x64-Debug`** (`TrackPadLens.exe` + `TrackPadLens.pdb`)
- Uploads **`TrackPadLens-win-x64-FrameworkDependent`**
- Automatically attaches `TrackPadLens.exe` to a GitHub Release when pushing a `v*` tag (e.g. `git tag v1.0.0 && git push origin v1.0.0`).

---

## Operational Hardware Modes (Honest Windows Compatibility)

Windows manages Precision Touchpads inside the system input stack, and `WH_MOUSE_LL` does **not** expose the originating Raw Input device handle (`LLMHF_INJECTED` only indicates synthetic injection, never touchpad vs. external mouse). TrackPadLens never blocks all mouse input or fabricates fake touchpad packets.

| Mode | Condition | Behavior |
|---|---|---|
| **Mode A — Native Absolute Touchpad** | Precision Touchpad exposes valid absolute X/Y HID reports **and** an optional WHQL-signed KMDF HID filter driver implements `ICursorIsolationProvider`. | Full native absolute coordinates with per-device touchpad cursor suppression. |
| **Mode B — Compatible Mode (Default User-Mode)** | Precision Touchpad exposes valid absolute X/Y HID reports via `RegisterRawInputDevices(0x0D:0x05)` with no kernel filter driver installed. | Full native absolute coordinates for drawing; Windows may still move the cursor on touch. Hold **Shift** or **Right-Click** (Anchor Lock) to freeze the Lens frame while writing; external USB/Bluetooth mouse remains 100% functional. |
| **Mode C — Hardware Unsupported** | No Precision Touchpad with usable absolute X/Y collections was discovered. | Touchpad ink drawing is disabled and the Diagnostics window explains the exact HID descriptor reason. |

---

## Default Controls & Shortcuts

- **Alt + D**: Toggle Doodle Mode ON / OFF
- **Mouse Wheel (while Doodle Mode ON)**: Smoothly resize the Writing Lens (`100px` min to `90%` screen width) preserving aspect ratio, with a 500ms scale badge (`1.0x`, `1.2x`, ...)
- **Hold Shift or Right-Click**: Anchor Lock — freezes the Lens position while continuing to draw inside it (without swallowing normal right-click interactions)
- **Ctrl + Z**: Undo last stroke (or immediately restore strokes after `Esc` Clear All)
- **Ctrl + S**: Save single-pass PNG snapshot to `Pictures\TrackPadLens\TrackPadLens_yyyyMMdd_HHmmss.png`
- **Esc**: Clear all ink (reversible via `Ctrl + Z`)
- **System Tray**: Right-click tray icon for Doodle Mode toggle, Calibration, Settings, Start with Windows (`HKCU\...\Run`), Diagnostics, and clean Exit

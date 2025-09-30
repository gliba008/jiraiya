# 🐸 Jiraiya - Simple Window Tiling for Windows

## Introduction
Jiraiya is a lightweight tiling assistant for Windows inspired by [David Heinemeier Hansson's Omarchy Linux setup](https://omarchy.org/). It works alongside the operating system instead of fighting it.

Jiraiya is also:

- The toad-riding protagonist from the [Japanese folk tale](https://en.wikipedia.org/wiki/Jiraiya_(folklore)).
- The legendary [Toad Sage](https://naruto.fandom.com/wiki/Jiraiya) of stupendous ninja skill from *Naruto*.

## How It Works
- Launch Jiraiya and it immediately arranges the visible application windows into its three-slot layout (A, B, C) per monitor.
- It keeps listening for window events—new windows, closures, focus changes, moves—and reapplies the layout only when necessary.
- Jiraiya runs headless and lives in the notification area. Press `Win + Alt + J` or use the tray menu to pause or resume the tiler; while paused, Windows behaves as if the tool were not running.
- The utility never overrides core Windows flows. You can still snap, resize, or move windows manually; Jiraiya simply automates the repetitive tiling work when it is enabled.

## Commands
| Action | Shortcut / Interaction | Description |
| --- | --- | --- |
| Toggle tiling | `Win + Alt + J` | Pause or resume Jiraiya and show a tray notification. |
| Focus next tiled window | `Alt + Tab` | Cycle forward through tiled windows on the current monitor (only while enabled). |
| Focus previous tiled window | `Alt + Shift + Tab` | Cycle backward through tiled windows on the current monitor. |
| Reorder window left/right | `Win + Shift + Left / Right` | Move the focused window between the left slot and the right-hand stack. |
| Reorder window vertically | `Win + Shift + Up / Down` | Shuffle the focused window inside the right-hand stack (top ↔ bottom). |
| Move with the mouse | Drag a window | Drop a window onto the desired slot (A, B, C) to swap positions automatically. |
| Minimise all tiled windows | `Win + Alt + M` | Minimise every managed window; press again (with all still minimised) to restore them in-place. |
| Exit the utility | Tray menu → Exit | Right-click the tray icon (or left-click to pop the menu) and choose **Exit**. |

## Tray Menu
- **Enable/Disable tiling** – toggling if Jiraiya should work, mirrors `Win + Alt + J`.
- **Enable/Disable start with Windows** – toggling if Jiraiya should start with Windows.
- **Config…** – opens the active `config.json` in the system’s default editor (supports both local and root-level copies).
- **Readme** – launches the online README at [github.com/gliba008/jiraiya](https://github.com/gliba008/jiraiya/blob/master/README.md).
- **Exit** – closes Jiraiya.

## Configuration
Jiraiya reads its settings from `config.json` in the application directory (the file is generated alongside the executable). A reference template with the default values lives in `config.default.json`; copy it when you want to reset or customise the configuration.

| Setting | Type | Description |
| --- | --- | --- |
| `ignore_apps` | `string[]` | Full paths to executables that Jiraiya should never tile. Paths are matched case-insensitively. |
| `ignore_dialogs` | `bool` | When `true`, dialog-style windows are skipped from tiling. |
| `center_ignored_windows` | `bool` | When `true`, ignored windows (apps or qualifying dialogs) are centred once when they appear. |
| `debounce_in_ms` | `int` | Delay before the layout is recomputed after a window event. Use `120` for the default behaviour. |

All settings are mandatory. If a value is missing or the file cannot be parsed, Jiraiya reports the configuration error and exits, ensuring the tiler never runs with partially defined behaviour.

## 🚀 Getting Started

To run this project locally, follow these steps:

1. **Clone the repository**  
   ```bash
   git clone https://github.com/gliba008/jiraiya.git
   cd jiraiya
   ```

2. **Install the .NET SDK**  
   You’ll need the latest .NET SDK. Follow the instructions here:  
   [Install .NET SDK (Windows)](https://learn.microsoft.com/en-us/dotnet/core/install/windows#install-with-visual-studio-code)

3. **Build the application**  
   ```bash
   dotnet build
   ```

4. **Run the application**  
   ```bash
   dotnet run
   ```

---

### 💻 Building for Windows

If you want to use the app directly (without `dotnet run`), you can create a release build and run the executable:

```bash
dotnet publish -c Release --self-contained true
```

This will generate a `Jiraiya.exe` file inside the `bin/Release/net*/*/` folder (replace `*` with real data, check your folders).  
Just double-click `Jiraiya.exe` to start it.  

➡️ Optionally, you can also set it to launch automatically with Windows if you right-click the frog icon 🐸 in Windows tray.

---

### ⚠️ Note

Right now this project is coded mainly to help me do the work while I’m on the move.  
As it becomes more stable, I’ll provide simple release packages so you won’t need to build it yourself.

---

### 🌟 Inspiration

On my other machine I use the **fantastic [Omarchy](https://world.hey.com/dhh/omakase-vs-omarchy-39e64848)** setup by DHH.  
I really like the window tiling workflow there, and I wanted something similar on Windows - 
but without breaking the usual Windows workflows.  

That’s why I built this lightweight solution I can easily enable when I’m on a single monitor.  
When I’m on multiple monitors, I usually just use the mouse and work without auto-tiling.

---
Built with .NET and the Win32 API to keep your desktop orderly without breaking your Windows workflow.

# 🐸 Jiraiya Window Tiling Utility

## Introduction
Jiraiya is a lightweight tiling assistant for Windows inspired by David Heinemeier Hansson's Omarchy Linux setup. It works alongside the operating system instead of fighting it: no exotic shell replacements, no hidden hacks, just tidy window placement across every monitor.

## How It Works
- Launch Jiraiya and it immediately arranges the visible application windows into its three-slot layout (A, B, C) per monitor.
- It keeps listening for window events—new windows, closures, focus changes, moves—and reapplies the layout only when necessary.
- Press `Win + Alt + J` at any time to pause or resume the tiler. While paused, Jiraiya stays out of the way; Windows behaves exactly as if the tool were not running.
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
| Exit the utility | Close console or tray menu | Press `Ctrl + C` in the console window or exit via the tray icon. |

## Configuration
Jiraiya reads its settings from `config.json` in the application directory (the file is generated alongside the executable). A reference template with the default values lives in `config.default.json`; copy it when you want to reset or customise the configuration.

| Setting | Type | Description |
| --- | --- | --- |
| `ignore_apps` | `string[]` | Full paths to executables that Jiraiya should never tile. Paths are matched case-insensitively. |
| `ignore_dialogs` | `bool` | When `true`, dialog-style windows are skipped from tiling. |
| `center_ignored_windows` | `bool` | When `true`, windows that are ignored (either because they match `ignore_apps` or are dialogs) are centred on their monitor instead of being left where Windows placed them. |
| `debounce_in_ms` | `int` | Delay before the layout is recomputed after a window event. Use `120` for the default behaviour. |

All settings are mandatory. If a value is missing or the file cannot be parsed, Jiraiya reports the configuration error in the console and exits, ensuring the tiler never runs with partially defined behaviour.

---
Built with .NET and the Win32 API to keep your desktop orderly without breaking your Windows workflow.

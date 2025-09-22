# 🐸 The Tale of The Jiraiya the Gallant

*A spiraling window tiling manager for Windows*

## ✨ Features

- **🌀 Spiral Tiling Layout** - Intelligent window arrangement that grows in a spiral pattern
- **⚡ Event-Driven** - No polling, responds instantly to window changes
- **🎯 Hotkey Control** - Windows + Alt + J to toggle ON/OFF
- **🐸 System Tray** - Minimizes to tray with frog icon
- **📱 Smart Layout** - Adapts to 1, 2, or many windows automatically

## 🏗️ Layout Algorithm

### Single Window
```
┌─────────────────────────┐
│                         │
│       Window 1          │
│      (100% width)       │
│                         │
└─────────────────────────┘
```

### Two Windows
```
┌────────────┬────────────┐
│            │            │
│  Window 1  │  Window 2  │
│  (50%)     │  (50%)     │
│            │            │
└────────────┴────────────┘
```

### Three+ Windows (Spiral)
```
┌────────────┬────────────┐
│            │  Window 2  │
│  Window 1  ├──────┬─────┤
│  (50%)     │ Win3 │Win4+│
│            │      │     │
└────────────┴──────┴─────┘
```

## 🚀 Installation

1. Make sure you have .NET 6.0 installed
2. Clone or download this repository
3. Open terminal in project folder
4. Run: `dotnet run`

## 🎮 Usage

- **Start**: Run `dotnet run` - program starts minimized in system tray
- **Toggle**: Press `Windows + Alt + J` to enable/disable tiling
- **View Console**: Double-click tray icon or right-click → Open
- **Exit**: Right-click tray icon → Exit

## 🐸 System Tray

- **🐸 Frog Icon** - Shows in system tray
- **Double-click** - Opens console window
- **Right-click** - Context menu with options:
  - 🐸 Open - Show console window
  - ⚡ Toggle ON/OFF - Enable/disable tiling
  - ❌ Exit - Close application

## ⚙️ How It Works

1. **Event Hooks** - Listens to Windows events (create, destroy, show, hide)
2. **Smart Filtering** - Only tiles valid application windows
3. **Spiral Algorithm** - Main window (50% left) + spiral arrangement (50% right)
4. **Multi-Monitor** - Works with the monitor where cursor is located

## 🛠️ Technical Details

- **Framework**: .NET 6.0 Windows Forms
- **API**: Windows User32 API for window management
- **Architecture**: Event-driven with Windows hooks
- **Performance**: Zero polling, minimal resource usage

## 📋 Requirements

- Windows 10/11
- .NET 6.0 Runtime
- Administrator privileges (for global hotkey registration)

## 🤝 Contributing

Feel free to submit issues and enhancement requests!

## 📜 License

This project is open source. Feel free to use and modify as needed.

---

*The tale continues... 🐸*
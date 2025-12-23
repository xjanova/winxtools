# WinXTools - Advanced Windows Network & System Management Tool

[![.NET](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-lightgrey)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)

> **WinXTools** is a professional-grade Windows utility application developed by **xman studio** for comprehensive network monitoring, bandwidth control, application management, and system cleanup.

---

## Features

### Network Monitor
- Real-time bandwidth monitoring per process
- TCP/UDP connection tracking
- Live charts with download/upload speeds
- Process-level network statistics
- Historical data logging

### Bandwidth Control
- **Basic Mode**: Windows Firewall API integration
  - Block/Allow internet access per application
  - Simple toggle interface
- **Advanced Mode**: Windows Filtering Platform (WFP)
  - Kernel-level packet filtering
  - Custom bandwidth limits per application
  - Priority-based traffic management

### Deep Uninstaller
- Complete application removal
- Registry cleanup
- Leftover file detection
- System application management
- Batch uninstall support

### System Cleaner
- Temporary files cleanup
- Browser cache removal
- Windows Update cache
- Recycle Bin management
- Thumbnail cache
- Log files cleanup
- Windows.old removal

### Settings & Customization
- Multi-language support (English / Thai)
- Theme customization
- Auto-start with Windows
- System tray integration
- Notification preferences

---

## System Requirements

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| OS | Windows 10 (1903+) | Windows 11 |
| CPU | Dual-core 1.5 GHz | Quad-core 2.0 GHz+ |
| RAM | 2 GB | 4 GB+ |
| Disk | 100 MB | 200 MB |
| .NET | 10.0 Runtime | Included (Self-contained) |

---

## Installation

### Portable Version (Recommended)
1. Download `WinXTools-Portable.zip` from [Releases](releases)
2. Extract to any folder
3. Run `WinXTools.exe` as Administrator

### Build from Source
```bash
# Clone repository
git clone https://github.com/xjanova/winxtools.git
cd winxtools

# Restore packages
dotnet restore

# Build Debug
dotnet build

# Build Release (Portable)
dotnet publish -c Release -r win-x64 --self-contained true
```

---

## Quick Start

### First Run
1. Launch `WinXTools.exe` with Administrator privileges
2. Select your preferred language (English/Thai)
3. The Dashboard will show real-time network statistics

### Navigation
- **Dashboard** - Overview of network activity and quick actions
- **Network Monitor** - Detailed per-process network usage
- **Bandwidth Control** - Manage application internet access
- **Uninstaller** - Remove applications with deep cleaning
- **Cleaner** - Free up disk space
- **Settings** - Configure application preferences

---

## Screenshots

### Dashboard
Real-time network statistics with live charts showing download/upload speeds.

### Network Monitor
Process-level bandwidth monitoring with connection details.

### Bandwidth Control
Two-mode bandwidth management system.

### System Cleaner
Category-based system cleanup utility.

---

## Technology Stack

| Component | Technology |
|-----------|------------|
| Framework | .NET 10.0 |
| UI | WPF (Windows Presentation Foundation) |
| Architecture | MVVM |
| Charts | LiveCharts2 (SkiaSharp) |
| Database | SQLite |
| Network API | Windows IP Helper API (iphlpapi.dll) |
| Firewall | Windows Filtering Platform (WFP) |

---

## Project Structure

```
winxtools/
├── NetX.sln                    # Solution file
├── README.md                   # This file
└── src/
    ├── NetX.App/               # WPF Application
    │   ├── Views/              # XAML Pages
    │   ├── ViewModels/         # View Models (MVVM)
    │   ├── Themes/             # Styles & Colors
    │   ├── Languages/          # Localization
    │   └── Assets/             # Images & Icons
    └── NetX.Core/              # Core Library
        ├── Network/            # Network monitoring
        ├── Data/               # Database services
        ├── Optimization/       # RAM optimizer, Process killer
        └── Helpers/            # Utility classes
```

---

## Configuration

### Database Location
```
{AppDirectory}/data/winxtools.db
```

### Settings Keys
| Key | Default | Description |
|-----|---------|-------------|
| Language | auto | UI language (en-US, th-TH, auto) |
| Theme | dark | Application theme |
| RefreshRateMs | 1000 | Network stats refresh interval |
| StartWithWindows | false | Auto-start on login |
| MinimizeToTray | true | Minimize to system tray |
| ShowNotifications | true | Desktop notifications |
| BandwidthMode | basic | Default bandwidth control mode |
| DataRetentionDays | 30 | Days to keep network history |

---

## Command Line Options

```bash
# Run normally
WinXTools.exe

# Run minimized to tray
WinXTools.exe --minimized

# Run with specific language
WinXTools.exe --lang=th-TH
```

---

## Troubleshooting

### "Access Denied" errors
- Ensure you're running as Administrator
- Some features require elevated privileges

### Network stats not updating
- Check Windows Firewall is not blocking WinXTools
- Verify network interfaces are enabled

### High CPU usage
- Increase RefreshRateMs in Settings
- Reduce number of monitored processes

### Database errors
- Delete `data/winxtools.db` to reset
- Check disk space availability

---

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

---

## License

MIT License - Copyright (c) 2024-2025 xman studio

---

## Changelog

### Version 0.1.0-beta (2025)
- Initial public release
- Real-time network monitoring with smooth UI
- Per-process bandwidth tracking
- Connection monitor
- System cleaner
- Deep uninstaller
- RAM optimizer with auto-optimization
- Process killer with auto-kill mode
- Status bar with live stats
- Thai and English language support
- Premium dark theme with lava lamp background

---

**Made with love by xman studio**

# NetX Architecture Documentation

## Overview

NetX follows a modular architecture separating concerns between the presentation layer (WPF Application) and business logic (Core Library).

```
┌─────────────────────────────────────────────────────────────┐
│                     NetX.App (WPF)                          │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │    Views    │  │ ViewModels  │  │   Themes/Languages  │  │
│  │   (XAML)    │◄─┤   (MVVM)    │  │   (Resources)       │  │
│  └─────────────┘  └──────┬──────┘  └─────────────────────┘  │
└──────────────────────────┼──────────────────────────────────┘
                           │
┌──────────────────────────┼──────────────────────────────────┐
│                     NetX.Core                               │
│  ┌─────────────┐  ┌──────┴──────┐  ┌─────────────────────┐  │
│  │   Network   │  │    Data     │  │      Helpers        │  │
│  │   Monitor   │  │  (SQLite)   │  │   (Admin, Utils)    │  │
│  └──────┬──────┘  └─────────────┘  └─────────────────────┘  │
└─────────┼───────────────────────────────────────────────────┘
          │
┌─────────┴───────────────────────────────────────────────────┐
│                   Windows APIs                              │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────────┐  │
│  │  IP Helper  │  │   Windows   │  │      Registry       │  │
│  │    API      │  │  Firewall   │  │        API          │  │
│  └─────────────┘  └─────────────┘  └─────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

---

## Project Structure

### NetX.App (Presentation Layer)

```
NetX.App/
├── App.xaml                    # Application entry & resources
├── App.xaml.cs                 # Startup logic, language init
├── app.manifest                # Admin elevation request
├── MainWindow.xaml             # Main window with navigation
├── MainWindow.xaml.cs          # Window controls, navigation
│
├── Views/                      # Page Views
│   ├── DashboardView.xaml      # Real-time stats dashboard
│   ├── NetworkMonitorView.xaml # Process network monitor
│   ├── BandwidthControlView.xaml # Bandwidth management
│   ├── UninstallerView.xaml    # App uninstaller
│   ├── CleanerView.xaml        # System cleaner
│   └── SettingsView.xaml       # App settings
│
├── ViewModels/                 # MVVM ViewModels
│   └── DashboardViewModel.cs   # Dashboard logic & bindings
│
├── Themes/                     # Visual Styling
│   ├── Colors.xaml             # Color palette & brushes
│   └── Controls.xaml           # Custom control styles
│
└── Languages/                  # Localization
    ├── en-US.xaml              # English strings
    └── th-TH.xaml              # Thai strings
```

### NetX.Core (Business Logic Layer)

```
NetX.Core/
├── Network/
│   └── NetworkMonitor.cs       # Network statistics service
│       ├── Singleton instance
│       ├── TCP/UDP connection tracking
│       ├── Per-process bandwidth estimation
│       └── Data models (NetworkStats, ProcessNetworkInfo)
│
├── Data/
│   └── DatabaseService.cs      # SQLite database service
│       ├── Singleton instance
│       ├── Settings management
│       ├── Network history logging
│       └── Bandwidth rules storage
│
└── Helpers/
    └── AdminHelper.cs          # Admin privilege utilities
        ├── IsRunAsAdmin()
        ├── RestartAsAdmin()
        └── SetHighPriority()
```

---

## Design Patterns

### Singleton Pattern
Used for services that require single instance across application:

```csharp
// NetworkMonitor.cs
public class NetworkMonitor
{
    private static readonly Lazy<NetworkMonitor> _instance =
        new(() => new NetworkMonitor());
    public static NetworkMonitor Instance => _instance.Value;

    private NetworkMonitor() { }
}

// DatabaseService.cs
public sealed class DatabaseService
{
    private static readonly Lazy<DatabaseService> _instance =
        new(() => new DatabaseService());
    public static DatabaseService Instance => _instance.Value;
}
```

### MVVM Pattern
Views bind to ViewModels with INotifyPropertyChanged:

```csharp
// DashboardViewModel.cs
public class DashboardViewModel : INotifyPropertyChanged, IDisposable
{
    private string _downloadSpeed = "0 B/s";

    public string DownloadSpeed
    {
        get => _downloadSpeed;
        set { _downloadSpeed = value; OnPropertyChanged(); }
    }

    public ICommand BlockAllCommand { get; }
}
```

### Command Pattern
RelayCommand for MVVM command binding:

```csharp
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public void Execute(object? parameter) => _execute();
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
}
```

---

## Data Flow

### Network Monitoring Flow

```
┌──────────────┐    ┌────────────────┐    ┌─────────────────┐
│  iphlpapi    │───►│ NetworkMonitor │───►│ DashboardView   │
│  (Native)    │    │  GetStats()    │    │  UpdateUI()     │
└──────────────┘    └────────────────┘    └─────────────────┘
                           │
                           ▼
                    ┌────────────────┐
                    │ DatabaseService│
                    │  LogUsage()    │
                    └────────────────┘
```

### Settings Flow

```
┌──────────────┐    ┌────────────────┐    ┌─────────────────┐
│ SettingsView │───►│ DatabaseService│───►│ SQLite Database │
│  OnChanged() │    │  SetSetting()  │    │  Settings table │
└──────────────┘    └────────────────┘    └─────────────────┘
        │
        ▼
┌──────────────┐
│    App       │
│ ChangeTheme/ │
│ Language()   │
└──────────────┘
```

---

## Database Schema

### Settings Table
```sql
CREATE TABLE Settings (
    Key TEXT PRIMARY KEY,
    Value TEXT NOT NULL,
    UpdatedAt TEXT DEFAULT CURRENT_TIMESTAMP
);
```

### NetworkHistory Table
```sql
CREATE TABLE NetworkHistory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ProcessName TEXT NOT NULL,
    ProcessId INTEGER NOT NULL,
    BytesSent INTEGER NOT NULL,
    BytesReceived INTEGER NOT NULL,
    Timestamp TEXT DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_network_timestamp ON NetworkHistory(Timestamp);
CREATE INDEX idx_network_process ON NetworkHistory(ProcessName);
```

### BandwidthRules Table
```sql
CREATE TABLE BandwidthRules (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ProcessName TEXT NOT NULL,
    ProcessPath TEXT,
    RuleType TEXT NOT NULL,        -- 'block', 'limit', 'priority'
    MaxDownload INTEGER,           -- bytes per second
    MaxUpload INTEGER,             -- bytes per second
    IsEnabled INTEGER DEFAULT 1,
    CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
);
```

### CleanupHistory Table
```sql
CREATE TABLE CleanupHistory (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Category TEXT NOT NULL,
    FilesDeleted INTEGER NOT NULL,
    BytesFreed INTEGER NOT NULL,
    CleanedAt TEXT DEFAULT CURRENT_TIMESTAMP
);
```

---

## Native API Integration

### IP Helper API (iphlpapi.dll)

Used for TCP/UDP connection enumeration:

```csharp
[DllImport("iphlpapi.dll", SetLastError = true)]
private static extern uint GetExtendedTcpTable(
    IntPtr pTcpTable,
    ref int dwOutBufLen,
    bool sort,
    int ipVersion,
    TcpTableClass tableClass,
    uint reserved);

[DllImport("iphlpapi.dll", SetLastError = true)]
private static extern uint GetExtendedUdpTable(
    IntPtr pUdpTable,
    ref int dwOutBufLen,
    bool sort,
    int ipVersion,
    UdpTableClass tableClass,
    uint reserved);
```

### Windows Registry API

Used for installed applications enumeration:

```csharp
// Registry paths for installed programs
string[] registryKeys = new[]
{
    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
};
```

---

## Threading Model

### UI Thread
- All WPF controls
- View updates
- User interaction handling

### Background Threads
- Network monitoring (Timer-based)
- Database operations
- File system scanning

### Thread Synchronization
```csharp
// Update UI from background thread
System.Windows.Application.Current?.Dispatcher.Invoke(() =>
{
    DownloadSpeed = FormatBytes(totalDown) + "/s";
    UploadSpeed = FormatBytes(totalUp) + "/s";
});
```

---

## Resource Management

### Theme System
```xml
<!-- Colors.xaml -->
<ResourceDictionary>
    <Color x:Key="AccentPrimaryColor">#00d4ff</Color>
    <SolidColorBrush x:Key="AccentPrimaryBrush"
                     Color="{StaticResource AccentPrimaryColor}"/>
</ResourceDictionary>
```

### Language System
```xml
<!-- en-US.xaml -->
<ResourceDictionary>
    <sys:String x:Key="Nav_Dashboard">Dashboard</sys:String>
    <sys:String x:Key="Nav_NetworkMonitor">Network Monitor</sys:String>
</ResourceDictionary>
```

### Dynamic Resource Loading
```csharp
public static void ChangeLanguage(string cultureCode)
{
    string langFile = cultureCode == "th-TH"
        ? "Languages/th-TH.xaml"
        : "Languages/en-US.xaml";

    var dict = new ResourceDictionary
    {
        Source = new Uri(langFile, UriKind.Relative)
    };

    // Remove old, add new
    var oldDict = Current.Resources.MergedDictionaries
        .FirstOrDefault(d => d.Source?.OriginalString.Contains("Languages/") == true);

    if (oldDict != null)
        Current.Resources.MergedDictionaries.Remove(oldDict);

    Current.Resources.MergedDictionaries.Add(dict);
}
```

---

## Security Considerations

### Administrator Privileges
Required for:
- Network packet capture
- Windows Firewall modification
- Registry access (certain keys)
- Process priority elevation

### Manifest Configuration
```xml
<!-- app.manifest -->
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

### Safe Operations
- Read-only registry access where possible
- Try-catch around all file/registry operations
- User confirmation for destructive actions

---

## Performance Optimization

### Process Priority
```csharp
using var process = Process.GetCurrentProcess();
process.PriorityClass = ProcessPriorityClass.High;
```

### Database Optimization
- Indexed tables for frequent queries
- Connection pooling via singleton
- Batch operations where possible

### Memory Management
- IDisposable implementation
- Timer disposal on page unload
- Process object disposal after enumeration

---

## Extensibility Points

### Adding New Views
1. Create `NewFeatureView.xaml` in Views/
2. Add navigation button in MainWindow.xaml
3. Handle navigation in MainWindow.xaml.cs
4. Add language keys in Languages/*.xaml

### Adding New Settings
1. Add default in DatabaseService.InsertDefaultSettings()
2. Add UI in SettingsView.xaml
3. Handle change in SettingsView.xaml.cs

### Adding New Languages
1. Copy en-US.xaml to new locale (e.g., ja-JP.xaml)
2. Translate all strings
3. Add ComboBoxItem in SettingsView.xaml
4. Update App.ChangeLanguage() if needed

---

## Build Configuration

### Debug Build
```xml
<PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
</PropertyGroup>
```

### Release Build (Portable)
```xml
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
</PropertyGroup>
```

---

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| CommunityToolkit.Mvvm | 8.3.2 | MVVM helpers |
| LiveChartsCore.SkiaSharpView.WPF | 2.0.0-rc2 | Charts |
| Microsoft.Data.Sqlite | 8.0.10 | Database |

---

**Document Version**: 1.0
**Last Updated**: December 2024
**Author**: xman studio

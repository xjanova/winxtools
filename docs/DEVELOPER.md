# NetX Developer Guide

## Prerequisites

### Required Tools
- **Visual Studio 2022** (17.8+) or **VS Code** with C# DevKit
- **.NET 8.0 SDK** ([Download](https://dotnet.microsoft.com/download/dotnet/8.0))
- **Git** for version control
- **Windows 10/11** (required for WPF development)

### Optional Tools
- **SQLite Browser** - For database inspection
- **Fiddler/Wireshark** - For network debugging
- **dnSpy** - For .NET debugging

---

## Development Environment Setup

### Clone Repository
```bash
git clone https://github.com/xmanstudio/NetX.git
cd NetX
```

### Restore Dependencies
```bash
dotnet restore
```

### Build
```bash
# Debug build
dotnet build

# Release build
dotnet build -c Release
```

### Run
```bash
# Run with dotnet
dotnet run --project src/NetX.App

# Or run the executable directly (as Admin)
.\src\NetX.App\bin\Debug\net8.0-windows\NetX.exe
```

---

## Project Configuration

### Solution Structure
```
NetX.sln
├── src/NetX.App/NetX.App.csproj    # WPF Application
└── src/NetX.Core/NetX.Core.csproj  # Core Library
```

### NetX.App.csproj Key Settings
```xml
<PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ApplicationManifest>app.manifest</ApplicationManifest>

    <!-- Fix for CommunityToolkit.Mvvm + WPF issue -->
    <IncludePackageReferencesDuringMarkupCompilation>false</IncludePackageReferencesDuringMarkupCompilation>
</PropertyGroup>

<!-- Release: Portable single-file -->
<PropertyGroup Condition="'$(Configuration)' == 'Release'">
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishSingleFile>true</PublishSingleFile>
</PropertyGroup>
```

### NetX.Core.csproj
```xml
<PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
</PropertyGroup>

<ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.10" />
</ItemGroup>
```

---

## Code Style Guidelines

### Naming Conventions
```csharp
// Classes: PascalCase
public class NetworkMonitor { }

// Interfaces: IPascalCase
public interface INetworkService { }

// Methods: PascalCase
public void GetNetworkStats() { }

// Properties: PascalCase
public string ProcessName { get; set; }

// Private fields: _camelCase
private readonly string _connectionString;

// Local variables: camelCase
var processStats = GetStats();

// Constants: PascalCase
private const int MaxDataPoints = 60;
```

### File Organization
```csharp
// 1. Using statements
using System;
using System.Collections.Generic;

// 2. Namespace
namespace NetX.Core.Network;

// 3. Class definition
public class NetworkMonitor
{
    // 4. Private fields
    private readonly object _lock = new();

    // 5. Public properties
    public static NetworkMonitor Instance => _instance.Value;

    // 6. Constructors
    private NetworkMonitor() { }

    // 7. Public methods
    public void Start() { }

    // 8. Private methods
    private void UpdateStats() { }

    // 9. Nested types
    private enum TcpTableClass { }
}
```

### XAML Guidelines
```xml
<!-- Use StaticResource for compile-time resources -->
<Button Style="{StaticResource PrimaryButtonStyle}" />

<!-- Use DynamicResource for runtime-switchable resources -->
<TextBlock Text="{DynamicResource Nav_Dashboard}" />

<!-- Element naming: x:Name with camelCase -->
<TextBox x:Name="searchBox" />

<!-- Margin/Padding order: Left, Top, Right, Bottom -->
<Border Margin="16,8,16,8" />
```

---

## Adding New Features

### Adding a New View

#### 1. Create XAML File
```xml
<!-- Views/NewFeatureView.xaml -->
<Page x:Class="NetX.App.Views.NewFeatureView"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      Title="New Feature"
      Background="{StaticResource BgPrimaryBrush}">

    <Grid Margin="24">
        <TextBlock Text="{DynamicResource NewFeature_Title}"
                   Style="{StaticResource HeaderTextStyle}"/>
    </Grid>
</Page>
```

#### 2. Create Code-Behind
```csharp
// Views/NewFeatureView.xaml.cs
using System.Windows.Controls;

namespace NetX.App.Views;

public partial class NewFeatureView : Page
{
    public NewFeatureView()
    {
        InitializeComponent();
    }
}
```

#### 3. Add Navigation
```xml
<!-- MainWindow.xaml - Add nav button -->
<Button x:Name="NavNewFeature"
        Style="{StaticResource NavButtonStyle}"
        Click="NavButton_Click"
        Tag="NewFeature">
    <StackPanel Orientation="Horizontal">
        <Path Data="{StaticResource NewFeatureIcon}" ... />
        <TextBlock Text="{DynamicResource Nav_NewFeature}"/>
    </StackPanel>
</Button>
```

#### 4. Handle Navigation
```csharp
// MainWindow.xaml.cs - Add case in NavButton_Click
case "NewFeature":
    MainFrame.Navigate(new NewFeatureView());
    break;
```

#### 5. Add Language Strings
```xml
<!-- Languages/en-US.xaml -->
<sys:String x:Key="Nav_NewFeature">New Feature</sys:String>
<sys:String x:Key="NewFeature_Title">New Feature</sys:String>

<!-- Languages/th-TH.xaml -->
<sys:String x:Key="Nav_NewFeature">ฟีเจอร์ใหม่</sys:String>
<sys:String x:Key="NewFeature_Title">ฟีเจอร์ใหม่</sys:String>
```

### Adding a New Core Service

#### 1. Create Service Class
```csharp
// NetX.Core/Services/NewService.cs
namespace NetX.Core.Services;

public class NewService
{
    // Singleton pattern
    private static readonly Lazy<NewService> _instance =
        new(() => new NewService());
    public static NewService Instance => _instance.Value;

    private NewService()
    {
        // Initialize
    }

    public void DoSomething()
    {
        // Implementation
    }
}
```

#### 2. Add Database Support (if needed)
```csharp
// Add table in DatabaseService.Initialize()
ExecuteNonQuery(connection, @"
    CREATE TABLE IF NOT EXISTS NewFeatureData (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        Name TEXT NOT NULL,
        CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
    )");
```

### Adding a New Setting

#### 1. Add Default Value
```csharp
// DatabaseService.cs - InsertDefaultSettings()
var defaultSettings = new Dictionary<string, string>
{
    // ... existing settings
    { "NewSetting", "default_value" },
};
```

#### 2. Add UI Control
```xml
<!-- SettingsView.xaml -->
<Grid>
    <StackPanel>
        <TextBlock Text="New Setting" />
        <TextBlock Text="Description of new setting" />
    </StackPanel>
    <CheckBox x:Name="NewSettingToggle"
              Style="{StaticResource ToggleSwitchStyle}"
              Checked="NewSetting_Changed"
              Unchecked="NewSetting_Changed"/>
</Grid>
```

#### 3. Handle Changes
```csharp
// SettingsView.xaml.cs
private void NewSetting_Changed(object sender, RoutedEventArgs e)
{
    var value = NewSettingToggle.IsChecked == true ? "true" : "false";
    DatabaseService.Instance.SetSetting("NewSetting", value);
}
```

---

## Working with Native APIs

### P/Invoke Declaration
```csharp
using System.Runtime.InteropServices;

[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool SetProcessPriority(
    IntPtr hProcess,
    uint dwPriorityClass);

// Struct for marshalling
[StructLayout(LayoutKind.Sequential)]
private struct NativeStruct
{
    public uint field1;
    public int field2;
}
```

### Error Handling
```csharp
var result = NativeMethod(...);
if (result == 0)
{
    var error = Marshal.GetLastWin32Error();
    throw new Win32Exception(error);
}
```

### Memory Management
```csharp
IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
try
{
    // Use buffer
    var data = Marshal.PtrToStructure<MyStruct>(buffer);
}
finally
{
    Marshal.FreeHGlobal(buffer);
}
```

---

## Database Operations

### Adding a Table
```csharp
ExecuteNonQuery(connection, @"
    CREATE TABLE IF NOT EXISTS TableName (
        Id INTEGER PRIMARY KEY AUTOINCREMENT,
        Column1 TEXT NOT NULL,
        Column2 INTEGER DEFAULT 0,
        CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
    )");

// Add index for performance
ExecuteNonQuery(connection, @"
    CREATE INDEX IF NOT EXISTS idx_tablename_column1
    ON TableName(Column1)");
```

### CRUD Operations
```csharp
// Create
public void Insert(string value)
{
    using var connection = GetConnection();
    ExecuteNonQuery(connection,
        "INSERT INTO Table (Column) VALUES (@value)",
        new SqliteParameter("@value", value));
}

// Read
public string? Get(int id)
{
    using var connection = GetConnection();
    using var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT Column FROM Table WHERE Id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    return cmd.ExecuteScalar()?.ToString();
}

// Update
public void Update(int id, string value)
{
    using var connection = GetConnection();
    ExecuteNonQuery(connection,
        "UPDATE Table SET Column = @value WHERE Id = @id",
        new SqliteParameter("@value", value),
        new SqliteParameter("@id", id));
}

// Delete
public void Delete(int id)
{
    using var connection = GetConnection();
    ExecuteNonQuery(connection,
        "DELETE FROM Table WHERE Id = @id",
        new SqliteParameter("@id", id));
}
```

---

## Testing

### Manual Testing Checklist

#### Network Monitor
- [ ] Process list populates on load
- [ ] Stats update in real-time
- [ ] Search filtering works
- [ ] Selection shows details

#### Bandwidth Control
- [ ] Basic mode toggle works
- [ ] Rules persist after restart
- [ ] Block actually blocks traffic

#### Uninstaller
- [ ] Programs list loads
- [ ] Search works
- [ ] Standard uninstall works
- [ ] Deep clean finds leftovers

#### Cleaner
- [ ] Scan finds files
- [ ] Sizes are accurate
- [ ] Clean removes files
- [ ] Progress shows correctly

#### Settings
- [ ] Language switch works immediately
- [ ] Settings persist after restart
- [ ] Reset restores defaults

### Debugging Tips

#### Enable Debug Output
```csharp
System.Diagnostics.Debug.WriteLine($"Value: {value}");
```

#### Attach Debugger to Running Instance
1. Run NetX.exe normally
2. Visual Studio > Debug > Attach to Process
3. Select NetX.exe
4. Set breakpoints

#### Database Inspection
```bash
# Location
%AppDir%/data/netx.db

# Use SQLite Browser or command line
sqlite3 netx.db ".schema"
sqlite3 netx.db "SELECT * FROM Settings"
```

---

## Publishing

### Debug Build
```bash
dotnet build
# Output: src/NetX.App/bin/Debug/net8.0-windows/
```

### Release Build
```bash
dotnet build -c Release
# Output: src/NetX.App/bin/Release/net8.0-windows/win-x64/
```

### Portable Publish
```bash
dotnet publish -c Release -r win-x64 --self-contained true
# Output: src/NetX.App/bin/Release/net8.0-windows/win-x64/publish/
```

### Single File Publish
```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
# Output: Single NetX.exe file
```

---

## Troubleshooting

### Build Errors

**"Could not find file icon.ico"**
- Comment out or add the icon file:
```xml
<!-- <ApplicationIcon>Assets\icon.ico</ApplicationIcon> -->
```

**"Duplicate definition" with CommunityToolkit.Mvvm**
- Ensure this is in csproj:
```xml
<IncludePackageReferencesDuringMarkupCompilation>false</IncludePackageReferencesDuringMarkupCompilation>
```
- Delete obj folder and rebuild

**"NetworkMonitor does not contain constructor that takes 0 arguments"**
- Use `NetworkMonitor.Instance` instead of `new NetworkMonitor()`

### Runtime Errors

**"Access Denied"**
- Run as Administrator
- Check app.manifest is properly configured

**"Database locked"**
- Ensure connections are disposed
- Use `using` statements
- Check for multiple instances

**UI not updating**
- Use Dispatcher.Invoke for cross-thread UI updates
- Verify property raises PropertyChanged

---

## Resources

### Documentation
- [WPF Documentation](https://docs.microsoft.com/wpf/)
- [.NET 8 Documentation](https://docs.microsoft.com/dotnet/)
- [SQLite Documentation](https://sqlite.org/docs.html)
- [LiveCharts2 Documentation](https://livecharts.dev/)

### API References
- [IP Helper API](https://docs.microsoft.com/windows/win32/iphlp/)
- [Windows Filtering Platform](https://docs.microsoft.com/windows/win32/fwp/)
- [Windows Registry](https://docs.microsoft.com/windows/win32/sysinfo/)

---

## Contact

For development questions:
- **Lead Developer**: xman studio
- **Email**: dev@xmanstudio.com

---

**Document Version**: 1.0
**Last Updated**: December 2024
**Author**: xman studio

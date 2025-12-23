using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace NetX.App.Views;

public partial class WindowsTricksView : Page
{
    private readonly List<WindowsTrick> _allTricks;
    private string _selectedCategory = "All";

    public WindowsTricksView()
    {
        InitializeComponent();
        _allTricks = GetAllTricks();
        BuildCategoryTabs();
        DisplayTricks(_allTricks);
    }

    private List<WindowsTrick> GetAllTricks()
    {
        return new List<WindowsTrick>
        {
            // ==================== GOD MODE & SPECIAL FOLDERS ====================
            new WindowsTrick
            {
                Category = "God Mode",
                Name = "God Mode (All Settings)",
                Description = "Access all Windows settings in one place - over 200+ settings",
                Command = "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}",
                CommandType = TrickCommandType.ShellCommand,
                Danger = TrickDanger.Safe,
                Icon = "SettingsIcon"
            },
            new WindowsTrick
            {
                Category = "God Mode",
                Name = "All Tasks (Alternative God Mode)",
                Description = "Another way to access all control panel items",
                Command = "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}",
                CommandType = TrickCommandType.ShellCommand,
                Danger = TrickDanger.Safe,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "God Mode",
                Name = "Network Connections",
                Description = "Direct access to network adapter settings",
                Command = "ncpa.cpl",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Safe,
                Icon = "NetworkIcon"
            },
            new WindowsTrick
            {
                Category = "God Mode",
                Name = "Programs and Features",
                Description = "Uninstall programs (classic view)",
                Command = "appwiz.cpl",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Safe,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "God Mode",
                Name = "Device Manager",
                Description = "Manage hardware devices",
                Command = "devmgmt.msc",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Safe,
                Icon = "SettingsIcon"
            },
            new WindowsTrick
            {
                Category = "God Mode",
                Name = "Disk Management",
                Description = "Partition and manage drives",
                Command = "diskmgmt.msc",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Moderate,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "God Mode",
                Name = "Services",
                Description = "Manage Windows services",
                Command = "services.msc",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Moderate,
                Icon = "SettingsIcon"
            },
            new WindowsTrick
            {
                Category = "God Mode",
                Name = "Group Policy Editor",
                Description = "Advanced Windows settings (Pro/Enterprise only)",
                Command = "gpedit.msc",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Dangerous,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "God Mode",
                Name = "Registry Editor",
                Description = "Edit Windows registry (advanced)",
                Command = "regedit",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Dangerous,
                Icon = "ShieldIcon"
            },

            // ==================== PERFORMANCE ====================
            new WindowsTrick
            {
                Category = "Performance",
                Name = "Disable Windows Search Indexing",
                Description = "Stop search indexer to save CPU/disk usage. Search will be slower but system faster.",
                Command = "sc stop WSearch && sc config WSearch start=disabled",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Moderate,
                Icon = "SpeedIcon",
                CanToggle = true,
                EnableCommand = "sc config WSearch start=auto && sc start WSearch",
                DisableCommand = "sc stop WSearch && sc config WSearch start=disabled",
                CheckCommand = "sc query WSearch"
            },
            new WindowsTrick
            {
                Category = "Performance",
                Name = "Disable Superfetch/SysMain",
                Description = "Reduce disk usage on HDDs. Not recommended for SSDs.",
                Command = "sc stop SysMain && sc config SysMain start=disabled",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Moderate,
                Icon = "SpeedIcon",
                CanToggle = true,
                EnableCommand = "sc config SysMain start=auto && sc start SysMain",
                DisableCommand = "sc stop SysMain && sc config SysMain start=disabled"
            },
            new WindowsTrick
            {
                Category = "Performance",
                Name = "High Performance Power Plan",
                Description = "Enable maximum performance power plan",
                Command = "powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Performance",
                Name = "Ultimate Performance Plan (Hidden)",
                Description = "Unlock hidden Ultimate Performance power plan",
                Command = "powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Performance",
                Name = "Disable Windows Tips",
                Description = "Stop Windows from showing tips and suggestions",
                Command = "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager\" /v \"SoftLandingEnabled\" /t REG_DWORD /d 0 /f",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Performance",
                Name = "Disable Background Apps",
                Description = "Prevent apps from running in background",
                Command = "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications\" /v \"GlobalUserDisabled\" /t REG_DWORD /d 1 /f",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Moderate,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Performance",
                Name = "Clear Standby Memory",
                Description = "Free up cached memory (requires RAMMap or similar)",
                Command = "Requires Sysinternals RAMMap -Et",
                CommandType = TrickCommandType.Info,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Performance",
                Name = "Disable Game DVR/Bar",
                Description = "Disable Xbox Game Bar for better gaming performance",
                Command = "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\GameDVR\" /v \"AppCaptureEnabled\" /t REG_DWORD /d 0 /f && reg add \"HKCU\\System\\GameConfigStore\" /v \"GameDVR_Enabled\" /t REG_DWORD /d 0 /f",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },

            // ==================== NETWORK ====================
            new WindowsTrick
            {
                Category = "Network",
                Name = "Flush DNS Cache",
                Description = "Clear DNS resolver cache",
                Command = "ipconfig /flushdns",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "NetworkIcon"
            },
            new WindowsTrick
            {
                Category = "Network",
                Name = "Reset Network Stack",
                Description = "Full network reset (Winsock, IP, Firewall)",
                Command = "netsh winsock reset && netsh int ip reset && netsh advfirewall reset",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Moderate,
                Icon = "NetworkIcon"
            },
            new WindowsTrick
            {
                Category = "Network",
                Name = "Show All Wi-Fi Passwords",
                Description = "Display saved Wi-Fi passwords",
                Command = "for /f \"skip=9 tokens=1,2 delims=:\" %i in ('netsh wlan show profiles') do @if \"%j\" NEQ \"\" (echo %j & netsh wlan show profiles %j key=clear | findstr \"Key Content\")",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "NetworkIcon"
            },
            new WindowsTrick
            {
                Category = "Network",
                Name = "Disable Nagle's Algorithm",
                Description = "Reduce network latency for gaming",
                Command = "Run registry edit for your network adapter",
                CommandType = TrickCommandType.Registry,
                RegistryPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{YOUR_ADAPTER_GUID}",
                RegistryValue = "TcpAckFrequency",
                RegistryData = "1",
                Danger = TrickDanger.Moderate,
                Icon = "NetworkIcon"
            },
            new WindowsTrick
            {
                Category = "Network",
                Name = "Enable Network Throttling Index",
                Description = "Remove network throttling limit",
                Command = "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Multimedia\\SystemProfile\" /v \"NetworkThrottlingIndex\" /t REG_DWORD /d 4294967295 /f",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "NetworkIcon"
            },
            new WindowsTrick
            {
                Category = "Network",
                Name = "Show Active Connections",
                Description = "Display all active network connections with process",
                Command = "netstat -b -n",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "NetworkIcon"
            },
            new WindowsTrick
            {
                Category = "Network",
                Name = "Release and Renew IP",
                Description = "Get a new IP address from DHCP",
                Command = "ipconfig /release && ipconfig /renew",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "NetworkIcon"
            },

            // ==================== PRIVACY ====================
            new WindowsTrick
            {
                Category = "Privacy",
                Name = "Disable Telemetry",
                Description = "Stop Windows from collecting usage data",
                Command = "sc stop DiagTrack && sc config DiagTrack start=disabled",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon",
                CanToggle = true,
                EnableCommand = "sc config DiagTrack start=auto && sc start DiagTrack",
                DisableCommand = "sc stop DiagTrack && sc config DiagTrack start=disabled"
            },
            new WindowsTrick
            {
                Category = "Privacy",
                Name = "Disable Cortana",
                Description = "Turn off Cortana completely",
                Command = "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Search\" /v \"AllowCortana\" /t REG_DWORD /d 0 /f",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Privacy",
                Name = "Disable Activity History",
                Description = "Stop Windows from tracking your activities",
                Command = "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\System\" /v \"EnableActivityFeed\" /t REG_DWORD /d 0 /f && reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\System\" /v \"PublishUserActivities\" /t REG_DWORD /d 0 /f",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Privacy",
                Name = "Disable Location Tracking",
                Description = "Turn off Windows location services",
                Command = "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\location\" /v \"Value\" /t REG_SZ /d \"Deny\" /f",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Privacy",
                Name = "Disable Advertising ID",
                Description = "Stop personalized ads tracking",
                Command = "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo\" /v \"Enabled\" /t REG_DWORD /d 0 /f",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Privacy",
                Name = "Disable Bing Search in Start Menu",
                Description = "Remove web search from Start menu",
                Command = "reg add \"HKCU\\SOFTWARE\\Policies\\Microsoft\\Windows\\Explorer\" /v \"DisableSearchBoxSuggestions\" /t REG_DWORD /d 1 /f",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },

            // ==================== SECURITY ====================
            new WindowsTrick
            {
                Category = "Security",
                Name = "Check for Rootkits",
                Description = "Scan system for rootkits using built-in tools",
                Command = "sfc /scannow && DISM /Online /Cleanup-Image /RestoreHealth",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Security",
                Name = "View Failed Login Attempts",
                Description = "Check security event log for failed logins",
                Command = "wevtutil qe Security /q:\"*[System[(EventID=4625)]]\" /c:20 /f:text",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Security",
                Name = "Enable Controlled Folder Access",
                Description = "Protect folders from ransomware",
                Command = "Set-MpPreference -EnableControlledFolderAccess Enabled",
                CommandType = TrickCommandType.PowerShell,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Security",
                Name = "Block Outbound Connections",
                Description = "Set firewall to block all outbound by default",
                Command = "netsh advfirewall set allprofiles firewallpolicy blockinbound,blockoutbound",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Dangerous,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Security",
                Name = "Enable Secure Boot Status",
                Description = "Check if Secure Boot is enabled",
                Command = "Confirm-SecureBootUEFI",
                CommandType = TrickCommandType.PowerShell,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Security",
                Name = "List Installed Certificates",
                Description = "View all installed security certificates",
                Command = "certmgr.msc",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },

            // ==================== HIDDEN FEATURES ====================
            new WindowsTrick
            {
                Category = "Hidden",
                Name = "Enable Old Photo Viewer",
                Description = "Bring back Windows 7 photo viewer",
                Command = @"reg add ""HKCR\Applications\photoviewer.dll\shell\open\command"" /ve /t REG_EXPAND_SZ /d ""%SystemRoot%\System32\rundll32.exe \""%ProgramFiles%\Windows Photo Viewer\PhotoViewer.dll\"", ImageView_Fullscreen %1"" /f",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "Hidden",
                Name = "Enable Dark Mode for Apps",
                Description = "Force dark mode for all apps",
                Command = "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize\" /v \"AppsUseLightTheme\" /t REG_DWORD /d 0 /f",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "SettingsIcon"
            },
            new WindowsTrick
            {
                Category = "Hidden",
                Name = "Show Seconds in Taskbar Clock",
                Description = "Display seconds in system tray clock",
                Command = "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" /v \"ShowSecondsInSystemClock\" /t REG_DWORD /d 1 /f",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "SettingsIcon"
            },
            new WindowsTrick
            {
                Category = "Hidden",
                Name = "Unlock Hidden Themes",
                Description = "Access hidden Windows themes folder",
                Command = @"explorer %windir%\Resources\Themes",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "Hidden",
                Name = "Enable Verbose Boot Messages",
                Description = "Show detailed startup/shutdown messages",
                Command = "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v \"VerboseStatus\" /t REG_DWORD /d 1 /f",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "TerminalIcon"
            },
            new WindowsTrick
            {
                Category = "Hidden",
                Name = "Enable Old Volume Mixer",
                Description = "Use classic volume mixer instead of modern one",
                Command = "sndvol.exe",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Safe,
                Icon = "SettingsIcon"
            },
            new WindowsTrick
            {
                Category = "Hidden",
                Name = "Reliability Monitor",
                Description = "View system stability and problem history",
                Command = "perfmon /rel",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Safe,
                Icon = "InfoIcon"
            },
            new WindowsTrick
            {
                Category = "Hidden",
                Name = "Resource Monitor",
                Description = "Detailed CPU, memory, disk, network usage",
                Command = "resmon",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Hidden",
                Name = "Problem Steps Recorder",
                Description = "Record steps to reproduce a problem",
                Command = "psr",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Safe,
                Icon = "InfoIcon"
            },

            // ==================== COMMAND LINE ====================
            new WindowsTrick
            {
                Category = "Commands",
                Name = "List All Running Processes",
                Description = "Show detailed process information",
                Command = "tasklist /v",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "TerminalIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "Kill Process by Name",
                Description = "Force close an application",
                Command = "taskkill /f /im processname.exe",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "TerminalIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "System File Checker",
                Description = "Scan and repair system files",
                Command = "sfc /scannow",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "DISM Health Check",
                Description = "Check Windows image health",
                Command = "DISM /Online /Cleanup-Image /CheckHealth",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "ShieldIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "Check Disk for Errors",
                Description = "Scan and fix disk errors on next restart",
                Command = "chkdsk C: /f /r",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Moderate,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "View System Information",
                Description = "Detailed hardware and software info",
                Command = "systeminfo",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "InfoIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "Driver Query",
                Description = "List all installed drivers",
                Command = "driverquery /v",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "SettingsIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "Battery Report",
                Description = "Generate detailed battery health report",
                Command = "powercfg /batteryreport",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "Energy Report",
                Description = "Analyze system power efficiency",
                Command = "powercfg /energy",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "Shutdown Timer",
                Description = "Schedule shutdown in X seconds",
                Command = "shutdown /s /t 3600",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "SettingsIcon"
            },
            new WindowsTrick
            {
                Category = "Commands",
                Name = "Cancel Shutdown",
                Description = "Cancel scheduled shutdown",
                Command = "shutdown /a",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "SettingsIcon"
            },

            // ==================== CLEANUP ====================
            new WindowsTrick
            {
                Category = "Cleanup",
                Name = "Disk Cleanup (Extended)",
                Description = "Run disk cleanup with all options",
                Command = "cleanmgr /sageset:1 && cleanmgr /sagerun:1",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "Cleanup",
                Name = "Clear Windows Update Cache",
                Description = "Remove old Windows Update files",
                Command = "net stop wuauserv && del /f /s /q %windir%\\SoftwareDistribution\\*.* && net start wuauserv",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Moderate,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "Cleanup",
                Name = "Clear Thumbnail Cache",
                Description = "Remove thumbnail database",
                Command = "del /f /s /q %LocalAppData%\\Microsoft\\Windows\\Explorer\\thumbcache_*.db",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "Cleanup",
                Name = "Clear Prefetch",
                Description = "Remove prefetch files (may slow initial app loads)",
                Command = "del /f /s /q %windir%\\Prefetch\\*.*",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Moderate,
                Icon = "FolderIcon"
            },
            new WindowsTrick
            {
                Category = "Cleanup",
                Name = "Remove Old Windows Installation",
                Description = "Delete Windows.old folder to free space",
                Command = "rd /s /q %SystemDrive%\\Windows.old",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Dangerous,
                Icon = "FolderIcon"
            },

            // ==================== STARTUP ====================
            new WindowsTrick
            {
                Category = "Startup",
                Name = "View All Startup Programs",
                Description = "Open Task Manager startup tab",
                Command = "taskmgr /0 /startup",
                CommandType = TrickCommandType.Command,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Startup",
                Name = "MSConfig",
                Description = "System configuration utility",
                Command = "msconfig",
                CommandType = TrickCommandType.RunCommand,
                Danger = TrickDanger.Moderate,
                Icon = "SettingsIcon"
            },
            new WindowsTrick
            {
                Category = "Startup",
                Name = "Autoruns (Download)",
                Description = "Microsoft Sysinternals Autoruns - best startup manager",
                Command = "https://docs.microsoft.com/en-us/sysinternals/downloads/autoruns",
                CommandType = TrickCommandType.Link,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon"
            },
            new WindowsTrick
            {
                Category = "Startup",
                Name = "Fast Startup (Toggle)",
                Description = "Enable/disable Windows fast startup",
                Command = "powercfg /h off",
                CommandType = TrickCommandType.AdminCommand,
                Danger = TrickDanger.Safe,
                Icon = "SpeedIcon",
                CanToggle = true,
                EnableCommand = "powercfg /h on",
                DisableCommand = "powercfg /h off"
            }
        };
    }

    private void BuildCategoryTabs()
    {
        var categories = new[] { "All" }.Concat(_allTricks.Select(t => t.Category).Distinct()).ToList();

        foreach (var category in categories)
        {
            var btn = new Button
            {
                Content = category,
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Background = category == _selectedCategory ? FindResource("AccentPrimaryBrush") as Brush : Brushes.Transparent,
                Foreground = category == _selectedCategory ? Brushes.White : FindResource("TextSecondaryBrush") as Brush,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = category
            };
            btn.Click += CategoryBtn_Click;
            CategoryTabs.Children.Add(btn);
        }
    }

    private void CategoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string category)
        {
            _selectedCategory = category;

            // Update tab styles
            foreach (Button tabBtn in CategoryTabs.Children)
            {
                var isSelected = tabBtn.Tag?.ToString() == category;
                tabBtn.Background = isSelected ? FindResource("AccentPrimaryBrush") as Brush : Brushes.Transparent;
                tabBtn.Foreground = isSelected ? Brushes.White : FindResource("TextSecondaryBrush") as Brush;
            }

            // Filter and display
            var filtered = category == "All" ? _allTricks : _allTricks.Where(t => t.Category == category).ToList();
            DisplayTricks(filtered);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var searchText = SearchBox.Text.ToLower();

        var filtered = string.IsNullOrWhiteSpace(searchText)
            ? (_selectedCategory == "All" ? _allTricks : _allTricks.Where(t => t.Category == _selectedCategory).ToList())
            : _allTricks.Where(t =>
                t.Name.ToLower().Contains(searchText) ||
                t.Description.ToLower().Contains(searchText) ||
                t.Command.ToLower().Contains(searchText) ||
                t.Category.ToLower().Contains(searchText)
            ).ToList();

        DisplayTricks(filtered);
    }

    private void DisplayTricks(List<WindowsTrick> tricks)
    {
        TricksContainer.Children.Clear();

        var groupedTricks = tricks.GroupBy(t => t.Category);

        foreach (var group in groupedTricks)
        {
            // Category header
            var header = new TextBlock
            {
                Text = group.Key,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = FindResource("AccentPrimaryBrush") as Brush,
                Margin = new Thickness(0, 16, 0, 12)
            };
            TricksContainer.Children.Add(header);

            // Tricks grid
            var grid = new WrapPanel { Orientation = Orientation.Horizontal };

            foreach (var trick in group)
            {
                var card = CreateTrickCard(trick);
                grid.Children.Add(card);
            }

            TricksContainer.Children.Add(grid);
        }
    }

    private Border CreateTrickCard(WindowsTrick trick)
    {
        var dangerColor = trick.Danger switch
        {
            TrickDanger.Safe => FindResource("SuccessBrush") as Brush,
            TrickDanger.Moderate => FindResource("WarningBrush") as Brush,
            TrickDanger.Dangerous => FindResource("DangerBrush") as Brush,
            _ => FindResource("TextTertiaryBrush") as Brush
        };

        var card = new Border
        {
            Background = FindResource("BgSecondaryBrush") as Brush,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 12, 12),
            Width = 380,
            MinHeight = 140
        };

        var stack = new StackPanel();

        // Header with icon and danger indicator
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

        // Icon
        var icon = new Path
        {
            Data = FindResource(trick.Icon) as Geometry ?? FindResource("InfoIcon") as Geometry,
            Fill = FindResource("AccentPrimaryBrush") as Brush,
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(icon, 0);
        headerGrid.Children.Add(icon);

        // Name
        var name = new TextBlock
        {
            Text = trick.Name,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = FindResource("TextPrimaryBrush") as Brush,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        headerGrid.Children.Add(name);

        // Danger indicator
        var dangerIndicator = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = dangerColor,
            ToolTip = trick.Danger.ToString()
        };
        Grid.SetColumn(dangerIndicator, 2);
        headerGrid.Children.Add(dangerIndicator);

        stack.Children.Add(headerGrid);

        // Description
        var desc = new TextBlock
        {
            Text = trick.Description,
            FontSize = 12,
            Foreground = FindResource("TextSecondaryBrush") as Brush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(desc);

        // Command preview
        var cmdBorder = new Border
        {
            Background = FindResource("BgTertiaryBrush") as Brush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 10, 0, 0)
        };
        var cmd = new TextBlock
        {
            Text = trick.Command.Length > 60 ? trick.Command.Substring(0, 57) + "..." : trick.Command,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = FindResource("TextTertiaryBrush") as Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = trick.Command
        };
        cmdBorder.Child = cmd;
        stack.Children.Add(cmdBorder);

        // Buttons
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };

        if (trick.CommandType == TrickCommandType.Link)
        {
            var openBtn = CreateCardButton("Open Link", "RunIcon", () => OpenLink(trick.Command));
            btnPanel.Children.Add(openBtn);
        }
        else if (trick.CommandType == TrickCommandType.Info)
        {
            var infoBtn = CreateCardButton("Info Only", "InfoIcon", null);
            infoBtn.IsEnabled = false;
            btnPanel.Children.Add(infoBtn);
        }
        else
        {
            var runBtn = CreateCardButton("Run", "RunIcon", () => RunTrick(trick));
            btnPanel.Children.Add(runBtn);

            if (trick.CanToggle)
            {
                var enableBtn = CreateCardButton("Enable", "ToggleOnIcon", () => RunCommand(trick.EnableCommand!, trick.CommandType));
                var disableBtn = CreateCardButton("Disable", "ToggleOffIcon", () => RunCommand(trick.DisableCommand!, trick.CommandType));
                btnPanel.Children.Add(enableBtn);
                btnPanel.Children.Add(disableBtn);
            }
        }

        var copyBtn = CreateCardButton("Copy", "CopyIcon", () => CopyCommand(trick.Command));
        btnPanel.Children.Add(copyBtn);

        stack.Children.Add(btnPanel);

        card.Child = stack;
        return card;
    }

    private Button CreateCardButton(string text, string iconKey, Action? action)
    {
        var btn = new Button
        {
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Background = FindResource("BgTertiaryBrush") as Brush,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Path
        {
            Data = FindResource(iconKey) as Geometry,
            Fill = FindResource("TextSecondaryBrush") as Brush,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 6, 0)
        };
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = FindResource("TextSecondaryBrush") as Brush
        });

        btn.Content = panel;

        if (action != null)
        {
            btn.Click += (s, e) => action();
        }

        return btn;
    }

    private void RunTrick(WindowsTrick trick)
    {
        if (trick.Danger == TrickDanger.Dangerous)
        {
            var result = MessageBox.Show(
                $"This command may cause system issues:\n\n{trick.Command}\n\nAre you sure you want to run it?",
                "Warning - Dangerous Command",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;
        }

        RunCommand(trick.Command, trick.CommandType);
    }

    private void RunCommand(string command, TrickCommandType type)
    {
        try
        {
            switch (type)
            {
                case TrickCommandType.RunCommand:
                    Process.Start(new ProcessStartInfo(command) { UseShellExecute = true });
                    break;

                case TrickCommandType.ShellCommand:
                    Process.Start(new ProcessStartInfo("explorer.exe", command) { UseShellExecute = true });
                    break;

                case TrickCommandType.Command:
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command}")
                    {
                        UseShellExecute = true,
                        CreateNoWindow = false
                    });
                    break;

                case TrickCommandType.AdminCommand:
                    Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command}")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        CreateNoWindow = false
                    });
                    break;

                case TrickCommandType.PowerShell:
                    Process.Start(new ProcessStartInfo("powershell.exe", $"-Command \"{command}\"")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        CreateNoWindow = false
                    });
                    break;

                case TrickCommandType.Registry:
                    Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to run command: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyCommand(string command)
    {
        Clipboard.SetText(command);
        MessageBox.Show("Command copied to clipboard!", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}

public class WindowsTrick
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Command { get; set; } = "";
    public TrickCommandType CommandType { get; set; }
    public TrickDanger Danger { get; set; }
    public string Icon { get; set; } = "InfoIcon";
    public bool CanToggle { get; set; }
    public string? EnableCommand { get; set; }
    public string? DisableCommand { get; set; }
    public string? CheckCommand { get; set; }
    public string? RegistryPath { get; set; }
    public string? RegistryValue { get; set; }
    public string? RegistryData { get; set; }
}

public enum TrickCommandType
{
    RunCommand,      // Direct executable (like calc.exe)
    ShellCommand,    // Shell command (like shell:::...)
    Command,         // CMD command
    AdminCommand,    // CMD command requiring admin
    PowerShell,      // PowerShell command
    Registry,        // Registry edit
    Link,            // External link
    Info             // Information only
}

public enum TrickDanger
{
    Safe,       // No risk
    Moderate,   // Some caution needed
    Dangerous   // Could cause issues
}

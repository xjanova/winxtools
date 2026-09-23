using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using NetX.Core.System;

using NetX.Core.Helpers;

namespace NetX.Core.Optimization;

/// <summary>
/// Windows Optimizer for disabling unnecessary services, AI features, and system bloat
/// </summary>
public class WindowsOptimizer : IDisposable
{
    private static WindowsOptimizer? _instance;
    public static WindowsOptimizer Instance => _instance ??= new WindowsOptimizer();

    private readonly string _settingsPath;

    public WindowsOptimizer()
    {
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetX", "windows_optimizer.json");

        LoadSettings();
        LoadServiceOriginals();
        InitializeOptimizationItems();
    }

    #region Optimization Items

    public List<OptimizationItem> Services { get; private set; } = new();
    public List<OptimizationItem> AIFeatures { get; private set; } = new();
    public List<OptimizationItem> TelemetryFeatures { get; private set; } = new();
    public List<OptimizationItem> StartupOptimizations { get; private set; } = new();

    private void InitializeOptimizationItems()
    {
        // Services ที่สามารถปิดได้อย่างปลอดภัย
        Services = new List<OptimizationItem>
        {
            // Telemetry & Data Collection
            new OptimizationItem
            {
                Id = "DiagTrack",
                Name = "Connected User Experiences and Telemetry",
                Description = "Microsoft telemetry service - collects usage data",
                Category = OptimizationCategory.Telemetry,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 20
            },
            new OptimizationItem
            {
                Id = "dmwappushservice",
                Name = "Device Management WAP Push Service",
                Description = "Push messaging service for device management",
                Category = OptimizationCategory.Telemetry,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },

            // Search & Indexing
            new OptimizationItem
            {
                Id = "WSearch",
                Name = "Windows Search",
                Description = "File indexing and search - disable if you don't use Windows Search",
                Category = OptimizationCategory.Performance,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Medium,
                RamSavingMB = 50
            },

            // Xbox Services
            new OptimizationItem
            {
                Id = "XblAuthManager",
                Name = "Xbox Live Auth Manager",
                Description = "Xbox authentication - disable if you don't play Xbox games",
                Category = OptimizationCategory.Gaming,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 10
            },
            new OptimizationItem
            {
                Id = "XblGameSave",
                Name = "Xbox Live Game Save",
                Description = "Xbox cloud saves - disable if you don't use Xbox",
                Category = OptimizationCategory.Gaming,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },
            new OptimizationItem
            {
                Id = "XboxGipSvc",
                Name = "Xbox Accessory Management",
                Description = "Xbox controller management",
                Category = OptimizationCategory.Gaming,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },
            new OptimizationItem
            {
                Id = "XboxNetApiSvc",
                Name = "Xbox Live Networking",
                Description = "Xbox network services",
                Category = OptimizationCategory.Gaming,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },

            // Print Services
            new OptimizationItem
            {
                Id = "Spooler",
                Name = "Print Spooler",
                Description = "Printing service - disable if you don't use printers",
                Category = OptimizationCategory.Hardware,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Medium,
                RamSavingMB = 15
            },

            // Fax
            new OptimizationItem
            {
                Id = "Fax",
                Name = "Fax Service",
                Description = "Fax functionality - rarely used",
                Category = OptimizationCategory.Hardware,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },

            // Remote Services
            new OptimizationItem
            {
                Id = "RemoteRegistry",
                Name = "Remote Registry",
                Description = "Remote registry editing - security risk if enabled",
                Category = OptimizationCategory.Security,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },
            new OptimizationItem
            {
                Id = "RemoteAccess",
                Name = "Routing and Remote Access",
                Description = "VPN/Remote access routing",
                Category = OptimizationCategory.Network,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 10
            },

            // SysMain (Superfetch)
            new OptimizationItem
            {
                Id = "SysMain",
                Name = "SysMain (Superfetch)",
                Description = "Preloads apps into RAM - disable on SSD for better performance",
                Category = OptimizationCategory.Performance,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Medium,
                RamSavingMB = 100
            },

            // Windows Error Reporting
            new OptimizationItem
            {
                Id = "WerSvc",
                Name = "Windows Error Reporting",
                Description = "Sends crash reports to Microsoft",
                Category = OptimizationCategory.Telemetry,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 10
            },

            // Bluetooth (if not used)
            new OptimizationItem
            {
                Id = "bthserv",
                Name = "Bluetooth Support Service",
                Description = "Bluetooth functionality - disable if not using Bluetooth",
                Category = OptimizationCategory.Hardware,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Medium,
                RamSavingMB = 15
            },

            // AllJoyn Router
            new OptimizationItem
            {
                Id = "AJRouter",
                Name = "AllJoyn Router Service",
                Description = "IoT device communication - rarely used",
                Category = OptimizationCategory.Network,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },

            // Geolocation
            new OptimizationItem
            {
                Id = "lfsvc",
                Name = "Geolocation Service",
                Description = "Location tracking - disable for privacy",
                Category = OptimizationCategory.Privacy,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 10
            },

            // Downloaded Maps Manager
            new OptimizationItem
            {
                Id = "MapsBroker",
                Name = "Downloaded Maps Manager",
                Description = "Offline maps - disable if not using Windows Maps",
                Category = OptimizationCategory.Apps,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 10
            },

            // Retail Demo
            new OptimizationItem
            {
                Id = "RetailDemo",
                Name = "Retail Demo Service",
                Description = "Store demo mode - not needed for personal use",
                Category = OptimizationCategory.Apps,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },

            // Secondary Logon
            new OptimizationItem
            {
                Id = "seclogon",
                Name = "Secondary Logon",
                Description = "Run as different user - keep if you use 'Run as administrator'",
                Category = OptimizationCategory.Security,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Medium,
                RamSavingMB = 5
            },

            // Smart Card
            new OptimizationItem
            {
                Id = "SCardSvr",
                Name = "Smart Card",
                Description = "Smart card reader support",
                Category = OptimizationCategory.Hardware,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },

            // Touch Keyboard
            new OptimizationItem
            {
                Id = "TabletInputService",
                Name = "Touch Keyboard and Handwriting",
                Description = "Tablet/touch input - disable if using keyboard/mouse only",
                Category = OptimizationCategory.Hardware,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 15
            },

            // Phone Service
            new OptimizationItem
            {
                Id = "PhoneSvc",
                Name = "Phone Service",
                Description = "Telephony API - rarely used on desktop",
                Category = OptimizationCategory.Apps,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },

            // Windows Biometric Service
            new OptimizationItem
            {
                Id = "WbioSrvc",
                Name = "Windows Biometric Service",
                Description = "Fingerprint/facial recognition - disable if not using biometrics",
                Category = OptimizationCategory.Security,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Medium,
                RamSavingMB = 10
            },

            // Windows Mobile Hotspot
            new OptimizationItem
            {
                Id = "icssvc",
                Name = "Windows Mobile Hotspot",
                Description = "Internet sharing - disable if not using mobile hotspot",
                Category = OptimizationCategory.Network,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5
            },

            // Diagnostic Services
            new OptimizationItem
            {
                Id = "diagsvc",
                Name = "Diagnostic Execution Service",
                Description = "Runs diagnostics - disable for performance",
                Category = OptimizationCategory.Telemetry,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 10
            },
            new OptimizationItem
            {
                Id = "DPS",
                Name = "Diagnostic Policy Service",
                Description = "Troubleshooting wizard",
                Category = OptimizationCategory.Telemetry,
                Type = OptimizationType.Service,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 10
            }
        };

        // AI Features (Windows 11+)
        AIFeatures = new List<OptimizationItem>
        {
            new OptimizationItem
            {
                Id = "Copilot",
                Name = "Windows Copilot",
                Description = "Microsoft AI assistant integrated into Windows",
                Category = OptimizationCategory.AI,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 200,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Policies\Microsoft\Windows\WindowsCopilot",
                        ValueName = "TurnOffWindowsCopilot",
                        DisableValue = 1,
                        EnableValue = 0
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot",
                        ValueName = "TurnOffWindowsCopilot",
                        DisableValue = 1,
                        EnableValue = 0
                    }
                }
            },
            new OptimizationItem
            {
                Id = "Recall",
                Name = "Windows Recall",
                Description = "AI feature that takes screenshots of everything you do",
                Category = OptimizationCategory.AI,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 150,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                        ValueName = "DisableAIDataAnalysis",
                        DisableValue = 1,
                        EnableValue = 0
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Policies\Microsoft\Windows\WindowsAI",
                        ValueName = "DisableAIDataAnalysis",
                        DisableValue = 1,
                        EnableValue = 0
                    }
                }
            },
            new OptimizationItem
            {
                Id = "Cortana",
                Name = "Cortana",
                Description = "Microsoft voice assistant",
                Category = OptimizationCategory.AI,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 100,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                        ValueName = "AllowCortana",
                        DisableValue = 0,
                        EnableValue = 1,
                        DeleteToRestoreDefault = true
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Microsoft\Windows\CurrentVersion\Search",
                        ValueName = "CortanaConsent",
                        DisableValue = 0,
                        EnableValue = 1
                    }
                }
            },
            new OptimizationItem
            {
                Id = "WebSearch",
                Name = "Web Search in Start Menu",
                Description = "Bing web search in Windows Search",
                Category = OptimizationCategory.AI,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 20,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Policies\Microsoft\Windows\Explorer",
                        ValueName = "DisableSearchBoxSuggestions",
                        DisableValue = 1,
                        EnableValue = 0
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Microsoft\Windows\CurrentVersion\Search",
                        ValueName = "BingSearchEnabled",
                        DisableValue = 0,
                        EnableValue = 1
                    }
                }
            },
            new OptimizationItem
            {
                Id = "SuggestedContent",
                Name = "Suggested Content & Tips",
                Description = "Windows suggestions, tips and ads",
                Category = OptimizationCategory.AI,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 10,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                        ValueName = "SubscribedContent-338389Enabled",
                        DisableValue = 0,
                        EnableValue = 1
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                        ValueName = "SubscribedContent-310093Enabled",
                        DisableValue = 0,
                        EnableValue = 1
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                        ValueName = "SoftLandingEnabled",
                        DisableValue = 0,
                        EnableValue = 1
                    }
                }
            }
        };

        // Telemetry Features
        TelemetryFeatures = new List<OptimizationItem>
        {
            new OptimizationItem
            {
                Id = "Telemetry",
                Name = "Windows Telemetry",
                Description = "Data collection and diagnostics sent to Microsoft",
                Category = OptimizationCategory.Telemetry,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 30,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                        ValueName = "AllowTelemetry",
                        DisableValue = 0,
                        EnableValue = 1,
                        DeleteToRestoreDefault = true
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection",
                        ValueName = "AllowTelemetry",
                        DisableValue = 0,
                        EnableValue = 1
                    }
                }
            },
            new OptimizationItem
            {
                Id = "AdvertisingId",
                Name = "Advertising ID",
                Description = "Unique ID used for personalized ads",
                Category = OptimizationCategory.Privacy,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                        ValueName = "Enabled",
                        DisableValue = 0,
                        EnableValue = 1
                    }
                }
            },
            new OptimizationItem
            {
                Id = "ActivityHistory",
                Name = "Activity History",
                Description = "Timeline and activity tracking",
                Category = OptimizationCategory.Privacy,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 20,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\System",
                        ValueName = "EnableActivityFeed",
                        DisableValue = 0,
                        EnableValue = 1,
                        DeleteToRestoreDefault = true
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\System",
                        ValueName = "PublishUserActivities",
                        DisableValue = 0,
                        EnableValue = 1,
                        DeleteToRestoreDefault = true
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\System",
                        ValueName = "UploadUserActivities",
                        DisableValue = 0,
                        EnableValue = 1,
                        DeleteToRestoreDefault = true
                    }
                }
            },
            new OptimizationItem
            {
                Id = "LocationTracking",
                Name = "Location Tracking",
                Description = "Windows location services",
                Category = OptimizationCategory.Privacy,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Medium,
                RamSavingMB = 10,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                        ValueName = "DisableLocation",
                        DisableValue = 1,
                        EnableValue = 0
                    }
                }
            },
            new OptimizationItem
            {
                Id = "DiagnosticData",
                Name = "Diagnostic Data Viewer",
                Description = "Tool to view collected diagnostic data",
                Category = OptimizationCategory.Telemetry,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 10,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Diagnostics\DiagTrack",
                        ValueName = "DiagTrackAuthorization",
                        DisableValue = 0,
                        EnableValue = 1
                    }
                }
            },
            new OptimizationItem
            {
                Id = "FeedbackFrequency",
                Name = "Feedback Notifications",
                Description = "Windows feedback prompts",
                Category = OptimizationCategory.Telemetry,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 5,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Microsoft\Siuf\Rules",
                        ValueName = "NumberOfSIUFInPeriod",
                        DisableValue = 0,
                        EnableValue = 1
                    }
                }
            }
        };

        // Startup Optimizations
        StartupOptimizations = new List<OptimizationItem>
        {
            new OptimizationItem
            {
                Id = "FastStartup",
                Name = "Fast Startup (Hybrid Boot)",
                Description = "Can cause issues with dual boot and updates - disable for stability",
                Category = OptimizationCategory.Performance,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Medium,
                RamSavingMB = 0,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power",
                        ValueName = "HiberbootEnabled",
                        DisableValue = 0,
                        EnableValue = 1
                    }
                }
            },
            new OptimizationItem
            {
                Id = "StartupDelay",
                Name = "Startup Apps Delay",
                Description = "Reduces delay for startup applications",
                Category = OptimizationCategory.Performance,
                Type = OptimizationType.Feature,
                RiskLevel = RiskLevel.Safe,
                RamSavingMB = 0,
                RegistryPaths = new List<RegistryPath>
                {
                    new RegistryPath
                    {
                        Hive = RegistryHive.CurrentUser,
                        Path = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize",
                        ValueName = "StartupDelayInMSec",
                        DisableValue = 0,
                        EnableValue = 0,
                        // Windows applies its own startup delay only while this value
                        // is absent, so "enable" deletes it (the old code wrote 10 ms,
                        // which still meant "no delay"). Any value up to 1 s — incl.
                        // that legacy 10 — counts as the delay being disabled.
                        DeleteToRestoreDefault = true,
                        DisabledAtOrBelow = 1000
                    }
                }
            }
        };

        // Drop features this Windows build doesn't have, so users never see
        // toggles that write inert registry keys (e.g. Recall on Windows 10).
        var os = WindowsVersionInfo.Current;
        if (!os.SupportsCopilot)
            AIFeatures.RemoveAll(i => i.Id == "Copilot");
        if (!os.SupportsRecall)
            AIFeatures.RemoveAll(i => i.Id == "Recall");
        if (os.IsWindows11 && os.Build >= 22631)
            AIFeatures.RemoveAll(i => i.Id == "Cortana"); // Cortana retired in Win11 23H2

        // Same for services this PC doesn't have (Fax, AllJoyn Router and the old
        // Touch Keyboard service are gone on current Windows 11). They can't be
        // changed, so listing them only made Safe Optimize end "with 3 error(s)".
        Services.RemoveAll(s => !ServiceExists(s.Id));

        // Enrich SSD/HDD-dependent advice off-thread (the WMI query can be slow).
        Task.Run(ApplySsdAdvice);

        // Load saved states
        LoadItemStates();
    }

    /// <summary>
    /// Tailors Superfetch/Search advice to the actual system drive type so the
    /// recommendation matches this PC instead of being generic.
    /// </summary>
    private void ApplySsdAdvice()
    {
        try
        {
            bool? ssd = WindowsVersionInfo.Current.SystemDriveIsSsd;
            if (ssd == null) return;

            var sysMain = Services.FirstOrDefault(s => s.Id == "SysMain");
            if (sysMain != null)
            {
                if (ssd == true)
                {
                    sysMain.Description = "Preloads apps into RAM. SSD detected on this PC — safe to disable, preloading gives little benefit on SSDs.";
                    sysMain.RiskLevel = RiskLevel.Safe;
                }
                else
                {
                    sysMain.Description = "Preloads apps into RAM. HDD detected on this PC — keep ENABLED unless you see constant 100% disk usage.";
                    sysMain.RiskLevel = RiskLevel.Medium;
                }
            }

            var wsearch = Services.FirstOrDefault(s => s.Id == "WSearch");
            if (wsearch != null && ssd == true)
            {
                wsearch.Description = "File indexing and search. SSD detected — indexing cost is small here; disable only if you never use Windows Search.";
            }
        }
        catch { }
    }

    #endregion

    #region System Restore

    /// <summary>True only when a NEW restore point was verifiably created.</summary>
    public bool CreateRestorePoint(string description)
    {
        return TryCreateRestorePoint(description).Outcome == RestorePointOutcome.Created;
    }

    /// <summary>
    /// Asks Windows for a restore point and reports what really happened. Windows
    /// returns "success" but silently makes no new point when one already exists
    /// from the last 24 hours, so the result is checked against the restore-point
    /// list instead of trusting the return code. Can take a minute — call it off
    /// the UI thread.
    /// </summary>
    public RestorePointAttempt TryCreateRestorePoint(string description)
    {
        var attempt = new RestorePointAttempt();
        var before = GetRestorePoints();
        var lastSequenceBefore = before.Count > 0 ? before.Max(p => p.SequenceNumber) : 0;
        attempt.LatestExisting = before.FirstOrDefault()?.CreationTime;

        try
        {
            using var restoreClass = new ManagementClass(
                new ManagementScope(@"\\localhost\root\default"),
                new ManagementPath("SystemRestore"),
                new ObjectGetOptions());

            using var inParams = restoreClass.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
            inParams["EventType"] = 100; // BEGIN_SYSTEM_CHANGE

            using var outParams = restoreClass.InvokeMethod("CreateRestorePoint", inParams, null);
            // uint32 — error HRESULTs such as 0x80070422 overflowed the old Convert.ToInt32.
            attempt.ReturnCode = Convert.ToUInt32(outParams["ReturnValue"]);
        }
        catch (Exception ex)
        {
            attempt.Outcome = RestorePointOutcome.Failed;
            attempt.Error = ex.Message;
            LastError = ex.Message;
            return attempt;
        }

        var created = FindNewRestorePoint(lastSequenceBefore);
        bool recentExists = attempt.LatestExisting is { } latest && DateTime.Now - latest < TimeSpan.FromHours(24);
        if (created == null && attempt.ReturnCode == 0 && !recentExists)
        {
            // Nothing to explain a skip — give the listing a moment to catch up.
            Thread.Sleep(2000);
            created = FindNewRestorePoint(lastSequenceBefore);
        }

        if (created != null)
        {
            attempt.Outcome = RestorePointOutcome.Created;
            attempt.CreatedAt = created.CreationTime;
        }
        else if (attempt.ReturnCode == 0 && recentExists)
        {
            attempt.Outcome = RestorePointOutcome.SkippedRecentExists;
        }
        else
        {
            attempt.Outcome = RestorePointOutcome.Failed;
            attempt.Error = attempt.ReturnCode == 0 ? "" : $"0x{attempt.ReturnCode:X8}";
            LastError = attempt.Error;
        }

        return attempt;
    }

    private RestorePointInfo? FindNewRestorePoint(int lastSequenceBefore) =>
        GetRestorePoints().FirstOrDefault(p => p.SequenceNumber > lastSequenceBefore);

    public List<RestorePointInfo> GetRestorePoints()
    {
        var points = new List<RestorePointInfo>();

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore");
            using var results = searcher.Get();
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    points.Add(new RestorePointInfo
                    {
                        SequenceNumber = Convert.ToInt32(obj["SequenceNumber"]),
                        Description = obj["Description"]?.ToString() ?? "",
                        CreationTime = ManagementDateTimeConverter.ToDateTime(obj["CreationTime"]?.ToString() ?? ""),
                        RestorePointType = Convert.ToInt32(obj["RestorePointType"])
                    });
                }
            }
        }
        catch { }

        return points.OrderByDescending(p => p.CreationTime).ToList();
    }

    #endregion

    #region Service Management

    // Windows 10/11 client default startup type of every service this page
    // manages: 2 = Automatic, 3 = Manual (incl. trigger start), 4 = Disabled;
    // Delayed true = "Automatic (Delayed Start)", null = leave that flag alone.
    // Used only when no original was recorded (e.g. a service disabled by an
    // older WinXTools build). Every service present on Windows 11 25H2 (build
    // 26200) was checked against this table; Fax, AJRouter and
    // TabletInputService no longer ship there and carry their Windows 10 value.
    // The table's keys are also the whitelist for the saved-originals file.
    private static readonly Dictionary<string, (int Start, bool? Delayed)> WindowsDefaultStart =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["DiagTrack"] = (2, false),
            ["dmwappushservice"] = (3, null),
            ["WSearch"] = (2, true),
            ["XblAuthManager"] = (3, null),
            ["XblGameSave"] = (3, null),
            ["XboxGipSvc"] = (3, null),
            ["XboxNetApiSvc"] = (3, null),
            ["Spooler"] = (2, false),
            ["Fax"] = (3, null),
            ["RemoteRegistry"] = (4, null),
            ["RemoteAccess"] = (4, null),
            ["SysMain"] = (2, false),
            ["WerSvc"] = (3, null),
            ["bthserv"] = (3, null),
            ["AJRouter"] = (3, null),
            ["lfsvc"] = (3, null),
            ["MapsBroker"] = (2, true),
            ["RetailDemo"] = (3, null),
            ["seclogon"] = (3, null),
            ["SCardSvr"] = (3, null),
            ["TabletInputService"] = (3, null),
            ["PhoneSvc"] = (3, null),
            ["WbioSrvc"] = (3, null),
            ["icssvc"] = (3, null),
            ["diagsvc"] = (3, null),
            ["DPS"] = (2, false)
        };

    private const int StartAutomatic = 2;
    private const int StartManual = 3;
    private const int StartDisabled = 4;
    private static readonly TimeSpan ServiceWaitTimeout = TimeSpan.FromSeconds(30);

    public static bool ServiceExists(string serviceName)
    {
        try
        {
            using var key = OpenServiceKey(serviceName);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private static RegistryKey? OpenServiceKey(string serviceName) =>
        Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");

    /// <summary>Raw "Start" value (2/3/4…), or -1 when it can't be read.</summary>
    private static int ReadStartValue(string serviceName)
    {
        using var key = OpenServiceKey(serviceName);
        return key?.GetValue("Start") is int start ? start : -1;
    }

    private static bool ReadDelayedAutostart(string serviceName)
    {
        using var key = OpenServiceKey(serviceName);
        return key?.GetValue("DelayedAutostart") is int delayed && delayed != 0;
    }

    public ServiceStatus GetServiceStatus(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            return new ServiceStatus
            {
                Name = serviceName,
                DisplayName = sc.DisplayName,
                Status = sc.Status,
                StartType = GetServiceStartType(serviceName),
                Exists = true
            };
        }
        catch
        {
            return new ServiceStatus { Name = serviceName, Exists = false };
        }
    }

    private ServiceStartMode GetServiceStartType(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key != null)
            {
                var start = key.GetValue("Start");
                if (start != null)
                {
                    return (int)start switch
                    {
                        0 => ServiceStartMode.Boot,
                        1 => ServiceStartMode.System,
                        2 => ServiceStartMode.Automatic,
                        3 => ServiceStartMode.Manual,
                        4 => ServiceStartMode.Disabled,
                        _ => ServiceStartMode.Manual
                    };
                }
            }
        }
        catch { }
        return ServiceStartMode.Manual;
    }

    /// <summary>
    /// Disables a service (saving its original configuration first) and stops it.
    /// Blocks while the service stops — call off the UI thread.
    /// </summary>
    public bool DisableService(string serviceName)
    {
        var item = FindItem(serviceName) ?? new OptimizationItem { Id = serviceName, Name = serviceName, Type = OptimizationType.Service };
        return RunServiceChange(item, disable: true, explicitEnable: false).Status is ChangeStatus.Changed or ChangeStatus.AlreadyInState;
    }

    /// <summary>
    /// Turns a service back on exactly as it was before WinXTools disabled it
    /// (startup type + delayed start, started again if it was running), or with
    /// the Windows default when nothing was recorded. Call off the UI thread.
    /// </summary>
    public bool EnableService(string serviceName)
    {
        var item = FindItem(serviceName) ?? new OptimizationItem { Id = serviceName, Name = serviceName, Type = OptimizationType.Service };
        return RunServiceChange(item, disable: false, explicitEnable: true).Status is ChangeStatus.Changed or ChangeStatus.AlreadyInState;
    }

    private OptimizationItem? FindItem(string id) =>
        GetAllOptimizations().FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    private ChangeOutcome RunServiceChange(OptimizationItem item, bool disable, bool explicitEnable)
    {
        try
        {
            return disable ? DisableServiceCore(item) : RestoreServiceCore(item, explicitEnable);
        }
        catch (Exception ex)
        {
            LastError = DescribeError(ex);
            return ChangeOutcome.Failed(LastError);
        }
    }

    private ChangeOutcome DisableServiceCore(OptimizationItem item)
    {
        var name = item.Id;
        if (!ServiceExists(name))
            return ChangeOutcome.NotAvailable();

        int currentStart = ReadStartValue(name);
        if (currentStart == StartDisabled)
        {
            item.IsDisabled = true;
            return ChangeOutcome.Already();
        }

        bool wasRunning;
        using (var sc = new ServiceController(name))
            wasRunning = sc.Status != ServiceControllerStatus.Stopped;

        // 1. Record exactly how it is configured BEFORE touching it, so Restore
        //    can put back Automatic / Delayed Start / Manual and restart it.
        bool remembered = currentStart is StartAutomatic or StartManual;
        if (remembered)
        {
            RememberServiceOriginal(name, new SavedServiceConfig
            {
                Start = currentStart,
                DelayedAutostart = ReadDelayedAutostart(name),
                WasRunning = wasRunning,
                SavedAt = DateTime.Now
            });
        }

        // 2. Change the startup type through the Service Control Manager. The old
        //    raw registry write only took effect after a reboot, and until then
        //    the SCM kept its cached value.
        bool changed;
        try
        {
            SetServiceStartType(name, StartDisabled, delayedAutostart: null);
            changed = ReadStartValue(name) == StartDisabled;
        }
        catch
        {
            if (remembered)
                ForgetServiceOriginal(name); // nothing changed — don't keep a stale "original"
            throw;
        }

        if (!changed)
        {
            if (remembered)
                ForgetServiceOriginal(name);
            return ChangeOutcome.Failed("");
        }
        item.IsDisabled = true;

        // 3. Stop it now. Failing to stop is not a failure to disable: it simply
        //    keeps running until the next restart, and the result says so.
        var outcome = ChangeOutcome.Changed();
        if (wasRunning)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
                    sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, ServiceWaitTimeout);
                item.IsRunning = false;
            }
            catch
            {
                outcome.StillRunning = true;
                item.IsRunning = true;
            }
        }

        return outcome;
    }

    private ChangeOutcome RestoreServiceCore(OptimizationItem item, bool explicitEnable)
    {
        var name = item.Id;
        if (!ServiceExists(name))
            return ChangeOutcome.NotAvailable();

        var plan = GetServiceRestorePlan(name, explicitEnable);
        int currentStart = ReadStartValue(name);

        if (plan.StartValue == StartDisabled)
        {
            // Its original / Windows default IS disabled (e.g. Remote Registry):
            // restoring means leaving it exactly as it is.
            item.IsDisabled = currentStart == StartDisabled;
            return ChangeOutcome.Already();
        }

        bool? delayedChange = plan.DelayedStart is bool wanted && wanted != ReadDelayedAutostart(name)
            ? wanted
            : null;

        if (currentStart != plan.StartValue || delayedChange.HasValue)
        {
            SetServiceStartType(name, plan.StartValue, delayedChange);
            if (ReadStartValue(name) != plan.StartValue)
                return ChangeOutcome.Failed("");
        }

        item.IsDisabled = false;
        ForgetServiceOriginal(name);

        var outcome = ChangeOutcome.Changed();
        if (plan.StartNow)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.StopPending)
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, ServiceWaitTimeout);
                if (sc.Status == ServiceControllerStatus.Stopped)
                    sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, ServiceWaitTimeout);
                item.IsRunning = true;
            }
            catch
            {
                // Startup type is restored; an Automatic service starts on next boot.
                outcome.NotStarted = true;
            }
        }

        return outcome;
    }

    /// <summary>
    /// What turning <paramref name="serviceName"/> back on will do: the startup
    /// type saved before WinXTools disabled it, else the Windows default. With
    /// <paramref name="explicitEnable"/> (the per-item Enable button) a service
    /// Windows ships disabled is set to Manual instead of staying disabled.
    /// </summary>
    public ServiceRestorePlan GetServiceRestorePlan(string serviceName, bool explicitEnable = false)
    {
        SavedServiceConfig? saved;
        lock (_originalsLock)
            _serviceOriginals.TryGetValue(serviceName, out saved);

        ServiceRestorePlan plan;
        if (saved != null)
        {
            plan = new ServiceRestorePlan
            {
                StartValue = saved.Start,
                DelayedStart = saved.DelayedAutostart,
                StartNow = saved.WasRunning || saved.Start == StartAutomatic,
                Source = ServiceRestoreSource.SavedOriginal
            };
        }
        else if (WindowsDefaultStart.TryGetValue(serviceName, out var windowsDefault))
        {
            plan = new ServiceRestorePlan
            {
                StartValue = windowsDefault.Start,
                DelayedStart = windowsDefault.Delayed,
                StartNow = windowsDefault.Start == StartAutomatic,
                Source = ServiceRestoreSource.WindowsDefault
            };
        }
        else
        {
            plan = new ServiceRestorePlan { StartValue = StartManual, Source = ServiceRestoreSource.ManualFallback };
        }

        if (plan.StartValue == StartDisabled && explicitEnable)
            plan = new ServiceRestorePlan { StartValue = StartManual, Source = ServiceRestoreSource.ManualFallback };

        return plan;
    }

    private static string DescribeError(Exception ex)
    {
        // ServiceController wraps the Win32 error; its message is already in the
        // Windows display language, unlike the .NET wrapper text.
        if (ex is Win32Exception win32) return win32.Message;
        if (ex.InnerException is Win32Exception inner) return inner.Message;
        return ex.Message;
    }

    #region Service Control Manager interop

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_CONFIG = 0x0001;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        public int fDelayedAutostart;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW")]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfigW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType,
        uint errorControl, string? binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel,
        ref SERVICE_DELAYED_AUTO_START_INFO info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    /// <summary>
    /// Sets the startup type (and optionally the delayed-start flag) via the SCM,
    /// which updates both its live configuration and the registry. Throws a
    /// Win32Exception (Windows-localized message) on failure.
    /// </summary>
    private static void SetServiceStartType(string serviceName, int startType, bool? delayedAutostart)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var service = OpenService(scm, serviceName, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
            if (service == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                if (!ChangeServiceConfig(service, SERVICE_NO_CHANGE, (uint)startType, SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (delayedAutostart.HasValue)
                {
                    var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayedAutostart.Value ? 1 : 0 };
                    if (!ChangeServiceConfig2(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    #endregion

    #region Saved service originals (admin-only store)

    private const string ServiceOriginalsFileName = "service_originals.json";
    private readonly object _originalsLock = new();
    private Dictionary<string, SavedServiceConfig> _serviceOriginals = new(StringComparer.OrdinalIgnoreCase);

    private void LoadServiceOriginals()
    {
        var state = AdminOnlyStore.Load<ServiceOriginalsState>(ServiceOriginalsFileName);
        var loaded = new Dictionary<string, SavedServiceConfig>(StringComparer.OrdinalIgnoreCase);

        if (state?.Services != null)
        {
            foreach (var (name, saved) in state.Services)
            {
                // Whitelist: only services this page manages, only "enabled"
                // startup types (a saved original can never be Disabled/Boot/System).
                if (saved == null || !WindowsDefaultStart.ContainsKey(name))
                    continue;
                if (saved.Start is not (StartAutomatic or StartManual))
                    continue;
                loaded[name] = saved;
            }
        }

        lock (_originalsLock)
            _serviceOriginals = loaded;
    }

    private void RememberServiceOriginal(string serviceName, SavedServiceConfig config)
    {
        lock (_originalsLock)
        {
            _serviceOriginals[serviceName] = config;
            PersistServiceOriginals();
        }
    }

    private void ForgetServiceOriginal(string serviceName)
    {
        lock (_originalsLock)
        {
            if (_serviceOriginals.Remove(serviceName))
                PersistServiceOriginals();
        }
    }

    // Caller holds _originalsLock.
    private void PersistServiceOriginals()
    {
        var state = new ServiceOriginalsState
        {
            Services = new Dictionary<string, SavedServiceConfig>(_serviceOriginals, StringComparer.OrdinalIgnoreCase)
        };

        // If the store is unavailable the originals still live in memory for this
        // session; after a restart Restore falls back to the Windows defaults.
        if (!AdminOnlyStore.Save(ServiceOriginalsFileName, state))
            Debug.WriteLine("[WindowsOptimizer] Service originals not persisted (admin-only store unavailable).");
    }

    #endregion

    #endregion

    #region Registry Feature Management

    public bool DisableFeature(OptimizationItem item)
    {
        if (item.RegistryPaths == null || item.RegistryPaths.Count == 0)
            return false;

        try
        {
            foreach (var regPath in item.RegistryPaths)
            {
                var rootKey = regPath.Hive switch
                {
                    RegistryHive.CurrentUser => Registry.CurrentUser,
                    RegistryHive.LocalMachine => Registry.LocalMachine,
                    _ => Registry.CurrentUser
                };

                // Create key if it doesn't exist
                using var key = rootKey.CreateSubKey(regPath.Path, true);
                if (key != null)
                {
                    key.SetValue(regPath.ValueName, regPath.DisableValue, RegistryValueKind.DWord);
                }
            }

            // Report what the registry now says, not what we meant to write.
            item.IsDisabled = IsFeatureDisabled(item);
            SaveSettings();
            if (!item.IsDisabled)
                LastError = null;
            return item.IsDisabled;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public bool EnableFeature(OptimizationItem item)
    {
        if (item.RegistryPaths == null || item.RegistryPaths.Count == 0)
            return false;

        try
        {
            foreach (var regPath in item.RegistryPaths)
            {
                var rootKey = regPath.Hive switch
                {
                    RegistryHive.CurrentUser => Registry.CurrentUser,
                    RegistryHive.LocalMachine => Registry.LocalMachine,
                    _ => Registry.CurrentUser
                };

                using var key = rootKey.OpenSubKey(regPath.Path, true);
                if (key != null)
                {
                    if (regPath.DeleteToRestoreDefault || regPath.EnableValue == 0)
                    {
                        // Delete the value so Windows falls back to its own default
                        // (policy values don't exist on a clean install).
                        key.DeleteValue(regPath.ValueName, throwOnMissingValue: false);
                    }
                    else
                    {
                        key.SetValue(regPath.ValueName, regPath.EnableValue, RegistryValueKind.DWord);
                    }
                }
            }

            // Report what the registry now says, not what we meant to write.
            item.IsDisabled = IsFeatureDisabled(item);
            SaveSettings();
            if (item.IsDisabled)
                LastError = null;
            return !item.IsDisabled;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public bool IsFeatureDisabled(OptimizationItem item)
    {
        if (item.RegistryPaths == null || item.RegistryPaths.Count == 0)
            return false;

        try
        {
            // Check first registry path
            var regPath = item.RegistryPaths[0];
            var rootKey = regPath.Hive switch
            {
                RegistryHive.CurrentUser => Registry.CurrentUser,
                RegistryHive.LocalMachine => Registry.LocalMachine,
                _ => Registry.CurrentUser
            };

            using var key = rootKey.OpenSubKey(regPath.Path);
            if (key != null)
            {
                var value = key.GetValue(regPath.ValueName);
                if (value != null)
                {
                    var number = Convert.ToInt32(value);
                    return regPath.DisabledAtOrBelow is int max
                        ? number <= max
                        : number == regPath.DisableValue;
                }
            }
        }
        catch { }

        return false;
    }

    #endregion

    #region Batch Operations

    /// <summary>
    /// Disables (or restores) a set of items and reports per item what really
    /// happened. Blocks for a long time (restore point, stopping/starting
    /// services) — always call it off the UI thread.
    /// </summary>
    /// <param name="explicitEnable">
    /// The user pressed Enable on one item (vs. Restore All): a service Windows
    /// ships disabled is then set to Manual instead of being left disabled.
    /// </param>
    public OptimizationResult ApplyOptimizations(List<OptimizationItem> items, bool disable, bool explicitEnable = false)
    {
        var result = new OptimizationResult
        {
            StartTime = DateTime.Now,
            TotalItems = items.Count
        };

        // Safety net before ANY disable — one item or many. The confirmation says
        // we try, so always try, and record what Windows actually did: it allows
        // one restore point per 24 h and silently skips the rest.
        if (disable && items.Count > 0)
        {
            result.RestorePoint = TryCreateRestorePoint(
                $"WinXTools - before disabling {items.Count} item(s) ({DateTime.Now:yyyy-MM-dd HH:mm})");
            result.RestorePointCreated = result.RestorePoint.Outcome == RestorePointOutcome.Created;
        }

        foreach (var item in items)
        {
            ChangeOutcome outcome;
            if (item.Type == OptimizationType.Service)
            {
                outcome = RunServiceChange(item, disable, explicitEnable);
            }
            else if (item.Type == OptimizationType.Feature)
            {
                bool success = disable ? DisableFeature(item) : EnableFeature(item);
                outcome = success ? ChangeOutcome.Changed() : ChangeOutcome.Failed(LastError);
            }
            else
            {
                outcome = ChangeOutcome.NotAvailable();
            }

            switch (outcome.Status)
            {
                case ChangeStatus.Changed:
                    result.SuccessCount++;
                    if (disable)
                        result.EstimatedRamSavedMB += item.RamSavingMB;
                    if (outcome.StillRunning)
                        result.StillRunningItems.Add(item.Name);
                    if (outcome.NotStarted)
                        result.NotStartedItems.Add(item.Name);
                    break;

                case ChangeStatus.AlreadyInState:
                    result.AlreadyInStateItems.Add(item.Name);
                    break;

                case ChangeStatus.NotAvailable:
                    // Not on this PC — nothing to change, and not an error.
                    result.NotAvailableItems.Add(item.Name);
                    break;

                default:
                    result.FailedItems.Add(string.IsNullOrWhiteSpace(outcome.Error)
                        ? item.Name
                        : $"{item.Name}: {outcome.Error}");
                    break;
            }
        }

        result.EndTime = DateTime.Now;
        result.Success = result.FailedItems.Count == 0;

        OnOptimizationComplete?.Invoke(result);
        return result;
    }

    public void RefreshAllStates()
    {
        // Refresh service states
        foreach (var item in Services)
        {
            var status = GetServiceStatus(item.Id);
            item.IsDisabled = status.StartType == ServiceStartMode.Disabled;
            item.IsRunning = status.Exists && status.Status != ServiceControllerStatus.Stopped;
            item.CurrentStatus = status.Exists ? status.Status.ToString() : "Not Found";
        }

        // Refresh feature states
        foreach (var item in AIFeatures.Concat(TelemetryFeatures).Concat(StartupOptimizations))
        {
            item.IsDisabled = IsFeatureDisabled(item);
        }
    }

    #endregion

    #region Quick Optimize Presets

    public List<OptimizationItem> GetSafeOptimizations()
    {
        return Services.Concat(AIFeatures).Concat(TelemetryFeatures).Concat(StartupOptimizations)
            .Where(i => i.RiskLevel == RiskLevel.Safe && !i.IsDisabled)
            .ToList();
    }

    public List<OptimizationItem> GetAllOptimizations()
    {
        return Services.Concat(AIFeatures).Concat(TelemetryFeatures).Concat(StartupOptimizations).ToList();
    }

    /// <summary>
    /// Disabled items that "Restore All" would actually change. Services whose
    /// original / Windows default is itself Disabled (Remote Registry, Routing
    /// and Remote Access) are already where they belong and are left out.
    /// </summary>
    public List<OptimizationItem> GetRestorableItems()
    {
        return GetAllOptimizations()
            .Where(i => i.IsDisabled)
            .Where(i => i.Type != OptimizationType.Service || GetServiceRestorePlan(i.Id).StartValue != StartDisabled)
            .ToList();
    }

    /// <summary>
    /// Sum of the catalogue's rough per-item RAM figures. These are typical-usage
    /// guesses, not measurements of this PC — label them as estimates in the UI.
    /// </summary>
    public long GetEstimatedRamSaving(List<OptimizationItem> items)
    {
        return items.Where(i => !i.IsDisabled).Sum(i => i.RamSavingMB);
    }

    #endregion

    #region Settings

    private Dictionary<string, bool> _savedStates = new();
    public string? LastError { get; private set; }

    private void LoadItemStates()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var settings = JsonSerializer.Deserialize<WindowsOptimizerSettings>(json);
                if (settings != null)
                {
                    _savedStates = settings.DisabledItems ?? new();
                }
            }
        }
        catch { }
    }

    private void LoadSettings()
    {
        LoadItemStates();
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Build current state
            _savedStates.Clear();
            foreach (var item in Services.Concat(AIFeatures).Concat(TelemetryFeatures).Concat(StartupOptimizations))
            {
                if (item.IsDisabled)
                    _savedStates[item.Id] = true;
            }

            var settings = new WindowsOptimizerSettings
            {
                DisabledItems = _savedStates
            };

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    #endregion

    public event Action<OptimizationResult>? OnOptimizationComplete;

    public void Dispose()
    {
        SaveSettings();
    }
}

#region Models

public class OptimizationItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public OptimizationCategory Category { get; set; }
    public OptimizationType Type { get; set; }
    public RiskLevel RiskLevel { get; set; }
    /// <summary>Rough typical-usage guess from the catalogue — never a measurement of this PC.</summary>
    public long RamSavingMB { get; set; }
    public bool IsDisabled { get; set; }

    /// <summary>Services only: the service process is running (or starting/stopping) right now.</summary>
    public bool IsRunning { get; set; }
    public string? CurrentStatus { get; set; }
    public List<RegistryPath>? RegistryPaths { get; set; }
}

public class RegistryPath
{
    public RegistryHive Hive { get; set; }
    public string Path { get; set; } = "";
    public string ValueName { get; set; } = "";
    public int DisableValue { get; set; }
    public int EnableValue { get; set; }

    /// <summary>
    /// "Enable" deletes the value instead of writing <see cref="EnableValue"/>, so
    /// Windows uses its own default (policy values and StartupDelayInMSec are
    /// absent on a clean install; writing them pins a non-default state).
    /// </summary>
    public bool DeleteToRestoreDefault { get; set; }

    /// <summary>When set, any value at or below this counts as "disabled".</summary>
    public int? DisabledAtOrBelow { get; set; }
}

public enum OptimizationCategory
{
    Telemetry,
    Privacy,
    AI,
    Performance,
    Gaming,
    Hardware,
    Network,
    Security,
    Apps
}

public enum OptimizationType
{
    Service,
    Feature,
    ScheduledTask
}

public enum RiskLevel
{
    Safe,      // No risk, can safely disable
    Medium,    // Some features may be affected
    High       // May cause issues, proceed with caution
}

public class ServiceStatus
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public ServiceControllerStatus Status { get; set; }
    public ServiceStartMode StartType { get; set; }
    public bool Exists { get; set; }
}

public class RestorePointInfo
{
    public int SequenceNumber { get; set; }
    public string Description { get; set; } = "";
    public DateTime CreationTime { get; set; }
    public int RestorePointType { get; set; }
}

public class OptimizationResult
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int TotalItems { get; set; }
    public int SuccessCount { get; set; }

    /// <summary>Sum of the catalogue's rough guesses — not measured; never show it as "RAM saved".</summary>
    public long EstimatedRamSavedMB { get; set; }

    /// <summary>"Name" or "Name: Windows error text" for items that could not be changed.</summary>
    public List<string> FailedItems { get; set; } = new();

    /// <summary>Nothing to do: already disabled, or already at its original/default state.</summary>
    public List<string> AlreadyInStateItems { get; set; } = new();

    /// <summary>Not present on this PC — skipped, not an error.</summary>
    public List<string> NotAvailableItems { get; set; } = new();

    /// <summary>Services disabled but still running until the next restart (stop timed out/refused).</summary>
    public List<string> StillRunningItems { get; set; } = new();

    /// <summary>Services turned back on but not started right now (they start on next boot if Automatic).</summary>
    public List<string> NotStartedItems { get; set; } = new();

    public bool Success { get; set; }

    /// <summary>What happened to the automatic pre-change restore point (disable only).</summary>
    public RestorePointAttempt? RestorePoint { get; set; }

    /// <summary>Whether a NEW restore point was verifiably created before the changes.</summary>
    public bool RestorePointCreated { get; set; }
}

public enum RestorePointOutcome
{
    /// <summary>A new restore point appeared in the list.</summary>
    Created,
    /// <summary>Windows skipped it because one was created within the last 24 hours.</summary>
    SkippedRecentExists,
    /// <summary>Windows refused (usually System Protection is off) or the call failed.</summary>
    Failed
}

public class RestorePointAttempt
{
    public RestorePointOutcome Outcome { get; set; }
    public DateTime? CreatedAt { get; set; }

    /// <summary>Newest restore point that existed before the attempt, if any.</summary>
    public DateTime? LatestExisting { get; set; }

    public uint ReturnCode { get; set; }

    /// <summary>Error detail (Windows message or hex code); may be empty.</summary>
    public string Error { get; set; } = "";
}

public enum ServiceRestoreSource
{
    /// <summary>Configuration recorded just before WinXTools disabled it.</summary>
    SavedOriginal,
    /// <summary>Nothing recorded — Windows' default startup type for this service.</summary>
    WindowsDefault,
    /// <summary>Unknown service, or one Windows ships disabled that the user explicitly enabled.</summary>
    ManualFallback
}

public class ServiceRestorePlan
{
    /// <summary>Raw startup type: 2 = Automatic, 3 = Manual, 4 = Disabled.</summary>
    public int StartValue { get; set; }

    /// <summary>Delayed-start flag to set, or null to leave it unchanged.</summary>
    public bool? DelayedStart { get; set; }

    /// <summary>Whether the service will be started right away.</summary>
    public bool StartNow { get; set; }

    public ServiceRestoreSource Source { get; set; }

    public ServiceStartMode StartMode => StartValue switch
    {
        2 => ServiceStartMode.Automatic,
        4 => ServiceStartMode.Disabled,
        _ => ServiceStartMode.Manual
    };

    public bool IsDelayedAutomatic => StartValue == 2 && DelayedStart == true;
}

/// <summary>A service's configuration recorded right before WinXTools disabled it.</summary>
public class SavedServiceConfig
{
    public int Start { get; set; }
    public bool DelayedAutostart { get; set; }
    public bool WasRunning { get; set; }
    public DateTime SavedAt { get; set; }
}

public class ServiceOriginalsState
{
    public int Version { get; set; } = 1;
    public Dictionary<string, SavedServiceConfig>? Services { get; set; }
}

internal enum ChangeStatus
{
    Changed,
    AlreadyInState,
    NotAvailable,
    Failed
}

internal sealed class ChangeOutcome
{
    public ChangeStatus Status { get; private init; }
    public string? Error { get; private init; }
    public bool StillRunning { get; set; }
    public bool NotStarted { get; set; }

    public static ChangeOutcome Changed() => new() { Status = ChangeStatus.Changed };
    public static ChangeOutcome Already() => new() { Status = ChangeStatus.AlreadyInState };
    public static ChangeOutcome NotAvailable() => new() { Status = ChangeStatus.NotAvailable };
    public static ChangeOutcome Failed(string? error) => new() { Status = ChangeStatus.Failed, Error = error };
}

public class WindowsOptimizerSettings
{
    public Dictionary<string, bool>? DisabledItems { get; set; }
}

#endregion

using System.Diagnostics;
using System.Management;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using NetX.Core.System;

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
                        EnableValue = 1
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
                        EnableValue = 1
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
                        EnableValue = 1
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\System",
                        ValueName = "PublishUserActivities",
                        DisableValue = 0,
                        EnableValue = 1
                    },
                    new RegistryPath
                    {
                        Hive = RegistryHive.LocalMachine,
                        Path = @"SOFTWARE\Policies\Microsoft\Windows\System",
                        ValueName = "UploadUserActivities",
                        DisableValue = 0,
                        EnableValue = 1
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
                        EnableValue = 10
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

    public bool CreateRestorePoint(string description)
    {
        try
        {
            // Use WMI to create restore point
            var oScope = new ManagementScope(@"\\localhost\root\default");
            var oPath = new ManagementPath("SystemRestore");
            var oGetOp = new ObjectGetOptions();
            var oClass = new ManagementClass(oScope, oPath, oGetOp);

            var oInParams = oClass.GetMethodParameters("CreateRestorePoint");
            oInParams["Description"] = description;
            oInParams["RestorePointType"] = 12; // APPLICATION_INSTALL
            oInParams["EventType"] = 100; // BEGIN_SYSTEM_CHANGE

            var oOutParams = oClass.InvokeMethod("CreateRestorePoint", oInParams, null);
            var result = Convert.ToInt32(oOutParams["ReturnValue"]);

            return result == 0;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public List<RestorePointInfo> GetRestorePoints()
    {
        var points = new List<RestorePointInfo>();

        try
        {
            var searcher = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore");
            foreach (ManagementObject obj in searcher.Get())
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
        catch { }

        return points.OrderByDescending(p => p.CreationTime).ToList();
    }

    #endregion

    #region Service Management

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

    public bool DisableService(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);

            // Stop service if running
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }

            // Set to disabled via registry
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", true);
            if (key != null)
            {
                key.SetValue("Start", 4, RegistryValueKind.DWord); // 4 = Disabled
                return true;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        return false;
    }

    public bool EnableService(string serviceName, ServiceStartMode startMode = ServiceStartMode.Manual)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", true);
            if (key != null)
            {
                int startValue = startMode switch
                {
                    ServiceStartMode.Automatic => 2,
                    ServiceStartMode.Manual => 3,
                    _ => 3
                };
                key.SetValue("Start", startValue, RegistryValueKind.DWord);
                return true;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        return false;
    }

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

            item.IsDisabled = true;
            SaveSettings();
            return true;
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
                    if (regPath.EnableValue == 0)
                    {
                        // Delete the value to enable
                        try { key.DeleteValue(regPath.ValueName, false); } catch { }
                    }
                    else
                    {
                        key.SetValue(regPath.ValueName, regPath.EnableValue, RegistryValueKind.DWord);
                    }
                }
            }

            item.IsDisabled = false;
            SaveSettings();
            return true;
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
                    return Convert.ToInt32(value) == regPath.DisableValue;
                }
            }
        }
        catch { }

        return false;
    }

    #endregion

    #region Batch Operations

    public OptimizationResult ApplyOptimizations(List<OptimizationItem> items, bool disable)
    {
        var result = new OptimizationResult
        {
            StartTime = DateTime.Now,
            TotalItems = items.Count
        };

        // Safety net: snapshot the system before any batch of disables.
        // Windows throttles restore-point creation (default: one per 24 h),
        // so this is best-effort — the result records whether it succeeded.
        if (disable && items.Count >= 3)
        {
            result.RestorePointCreated = CreateRestorePoint(
                $"WinXTools - before disabling {items.Count} items ({DateTime.Now:yyyy-MM-dd HH:mm})");
        }

        foreach (var item in items)
        {
            try
            {
                bool success = false;

                if (item.Type == OptimizationType.Service)
                {
                    success = disable ? DisableService(item.Id) : EnableService(item.Id);
                }
                else if (item.Type == OptimizationType.Feature)
                {
                    success = disable ? DisableFeature(item) : EnableFeature(item);
                }

                if (success)
                {
                    result.SuccessCount++;
                    result.EstimatedRamSavedMB += disable ? item.RamSavingMB : 0;
                }
                else
                {
                    result.FailedItems.Add(item.Name);
                }
            }
            catch (Exception ex)
            {
                result.FailedItems.Add($"{item.Name}: {ex.Message}");
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
    public long RamSavingMB { get; set; }
    public bool IsDisabled { get; set; }
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
    public long EstimatedRamSavedMB { get; set; }
    public List<string> FailedItems { get; set; } = new();
    public bool Success { get; set; }

    /// <summary>Whether the automatic pre-change restore point was created (best effort).</summary>
    public bool RestorePointCreated { get; set; }
}

public class WindowsOptimizerSettings
{
    public Dictionary<string, bool>? DisabledItems { get; set; }
}

#endregion

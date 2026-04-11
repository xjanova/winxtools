// This file is kept for backward compatibility.
// All update functionality has been moved to AutoUpdateService.
// License functionality is in XmanLicenseService.

using NetX.Core.System;

namespace NetX.Core.Optimization;

/// <summary>
/// Legacy wrapper - delegates to AutoUpdateService
/// </summary>
public class UpdateChecker
{
    private static UpdateChecker? _instance;
    public static UpdateChecker Instance => _instance ??= new UpdateChecker();

    private UpdateChecker() { }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        return await AutoUpdateService.Instance.CheckForUpdatesAsync();
    }

    public string GetCurrentVersion()
    {
        return AutoUpdateService.GetCurrentVersion();
    }
}

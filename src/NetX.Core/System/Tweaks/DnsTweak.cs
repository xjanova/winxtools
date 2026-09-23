using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetX.Core.System.Tweaks;

/// <summary>
/// "Use Cloudflare DNS" done so it can be undone properly: the DNS servers the
/// user had on every physical adapter (IPv4 and IPv6, manual or automatic) are
/// saved in the admin-only store first, and Restore puts exactly those back
/// instead of forcing DHCP. Uses Get/SetInterfaceDnsSettings (Windows 10 2004+).
/// </summary>
public static class DnsTweak
{
    public const string CloudflareV4 = "1.1.1.1,1.0.0.1";
    public const string CloudflareV6 = "2606:4700:4700::1111,2606:4700:4700::1001";

    private const string BackupFile = "dns_backup.json";
    private const int MaxServersPerFamily = 8;

    public sealed record Adapter(Guid Id, string Name, bool HasGlobalIPv6);

    public sealed class BackupEntry
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string V4 { get; set; } = "";
        public string V6 { get; set; } = "";
        public bool ChangedV6 { get; set; }
    }

    public sealed class Backup
    {
        public List<BackupEntry> Adapters { get; set; } = new();
    }

    /// <summary>Physical (NCF_PHYSICAL) Ethernet/Wi-Fi adapters that are connected.</summary>
    public static List<Adapter> GetActivePhysicalAdapters()
    {
        var physical = PhysicalAdapterIds();
        var list = new List<Adapter>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (!Guid.TryParse(ni.Id, out var id) || !physical.Contains(id)) continue;

                bool globalV6 = ni.GetIPProperties().UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                    (a.Address.GetAddressBytes()[0] & 0xE0) == 0x20); // 2000::/3 global unicast
                list.Add(new Adapter(id, ni.Name, globalV6));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DNS: skipping adapter: {ex.Message}");
            }
        }
        return list;
    }

    private static HashSet<Guid> PhysicalAdapterIds()
    {
        var ids = new HashSet<Guid>();
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Reg.OpenBase(RegistryHive.LocalMachine).OpenSubKey(classKey);
            if (root == null) return ids;
            foreach (var sub in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(sub);
                    if (k?.GetValue("NetCfgInstanceId") is not string idText || !Guid.TryParse(idText, out var id)) continue;
                    int characteristics = k.GetValue("Characteristics") is int c ? c : 0;
                    const int NCF_VIRTUAL = 0x1, NCF_PHYSICAL = 0x4;
                    if ((characteristics & NCF_PHYSICAL) != 0 && (characteristics & NCF_VIRTUAL) == 0) ids.Add(id);
                }
                catch { /* protected "Properties" subkey etc. */ }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"DNS: cannot read adapter class key: {ex.Message}");
        }
        return ids;
    }

    /// <summary>Manually configured servers ("" = automatic / from DHCP).</summary>
    public static string ReadServers(Guid adapter, bool v6)
    {
        var settings = new DNS_INTERFACE_SETTINGS { Version = DNS_INTERFACE_SETTINGS_VERSION1, Flags = v6 ? DNS_SETTING_IPV6 : 0 };
        uint rc = GetInterfaceDnsSettings(adapter, ref settings);
        if (rc != 0) throw new global::System.ComponentModel.Win32Exception((int)rc);
        try
        {
            return settings.NameServer == IntPtr.Zero ? "" : Normalize(Marshal.PtrToStringUni(settings.NameServer) ?? "");
        }
        finally
        {
            FreeInterfaceDnsSettings(ref settings);
        }
    }

    /// <summary>Sets manual servers, or "" to go back to automatic (DHCP).</summary>
    public static void WriteServers(Guid adapter, bool v6, string servers)
    {
        IntPtr text = Marshal.StringToHGlobalUni(servers);
        try
        {
            var settings = new DNS_INTERFACE_SETTINGS
            {
                Version = DNS_INTERFACE_SETTINGS_VERSION1,
                Flags = DNS_SETTING_NAMESERVER | (v6 ? DNS_SETTING_IPV6 : 0),
                NameServer = text
            };
            uint rc = SetInterfaceDnsSettings(adapter, ref settings);
            if (rc != 0) throw new global::System.ComponentModel.Win32Exception((int)rc);
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    public static string Normalize(string servers) =>
        string.Join(",", servers.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));

    private static bool IsValidList(string servers, AddressFamily family)
    {
        if (servers.Length == 0) return true;
        var parts = servers.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= MaxServersPerFamily &&
               parts.All(p => IPAddress.TryParse(p, out var ip) && ip.AddressFamily == family);
    }

    public static bool IsSupported => WindowsVersionInfo.Current.Build >= 19041;

    public static TrickStatus Detect()
    {
        if (!IsSupported)
            return TrickStatus.NotSupported(new LocText("Needs Windows 10 version 2004 or newer.", "ต้องใช้ Windows 10 เวอร์ชัน 2004 ขึ้นไป"));

        var adapters = GetActivePhysicalAdapters();
        if (adapters.Count == 0)
            return TrickStatus.Unknown(new LocText("No connected network adapter found.", "ไม่พบการ์ดเครือข่ายที่เชื่อมต่ออยู่"));

        int onCloudflare = 0;
        var notes = new List<LocText>();
        foreach (var a in adapters)
        {
            var v4 = ReadServers(a.Id, v6: false);
            if (v4 == CloudflareV4) onCloudflare++;
            notes.Add(v4.Length == 0
                ? new LocText($"{a.Name}: automatic (from router)", $"{a.Name}: อัตโนมัติ (จากเราเตอร์)")
                : v4 == CloudflareV4
                    ? new LocText($"{a.Name}: {v4}", $"{a.Name}: {v4}")
                    : new LocText($"{a.Name}: {v4} (set by hand)", $"{a.Name}: {v4} (ตั้งค่าเอง)"));
        }

        var note = LocText.Join(notes);
        if (onCloudflare == adapters.Count) return TrickStatus.Applied(note);
        if (onCloudflare == 0) return TrickStatus.Default(note);
        return TrickStatus.Custom(note);
    }

    public static async Task<TrickResult> ApplyAsync(CancellationToken ct)
    {
        if (!IsSupported) return TrickResult.Fail(new LocText("Needs Windows 10 version 2004 or newer.", "ต้องใช้ Windows 10 เวอร์ชัน 2004 ขึ้นไป"));
        var adapters = GetActivePhysicalAdapters();
        if (adapters.Count == 0)
            return TrickResult.Fail(new LocText("No connected network adapter found.", "ไม่พบการ์ดเครือข่ายที่เชื่อมต่ออยู่"));

        // 1) Back up what the user has now — before touching anything.
        var backup = LoadBackup() ?? new Backup();
        try
        {
            foreach (var a in adapters)
            {
                var id = a.Id.ToString("D");
                if (backup.Adapters.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                var v4 = ReadServers(a.Id, v6: false);
                var v6 = ReadServers(a.Id, v6: true);
                if (v4 == CloudflareV4) continue; // already ours: original unknown, restore will use automatic
                backup.Adapters.Add(new BackupEntry { Id = id, Name = a.Name, V4 = v4, V6 = v6, ChangedV6 = a.HasGlobalIPv6 });
            }
            SecureAppData.Save(BackupFile, backup);
        }
        catch (Exception ex)
        {
            return TrickResult.Fail(new LocText(
                "Could not save a backup of your current DNS settings, so nothing was changed. " + TweakErrors.Friendly(ex).En,
                "บันทึกสำรองค่า DNS เดิมไม่ได้ จึงยังไม่ได้เปลี่ยนอะไร " + TweakErrors.Friendly(ex).Th));
        }

        // 2) Switch every active physical adapter.
        var failed = new List<string>();
        foreach (var a in adapters)
        {
            try
            {
                WriteServers(a.Id, v6: false, CloudflareV4);
                if (a.HasGlobalIPv6) WriteServers(a.Id, v6: true, CloudflareV6);
                if (ReadServers(a.Id, v6: false) != CloudflareV4) failed.Add(a.Name);
            }
            catch
            {
                failed.Add(a.Name);
            }
        }

        await FlushDnsCacheAsync(ct).ConfigureAwait(false);

        if (failed.Count > 0)
            return TrickResult.Fail(new LocText(
                $"Could not change DNS on: {string.Join(", ", failed)}.",
                $"เปลี่ยน DNS ไม่สำเร็จบน: {string.Join(", ", failed)}"));

        return TrickResult.Ok(new LocText(
            "Done — Cloudflare DNS is set. Your previous DNS servers are saved for Restore.",
            "เรียบร้อย — ใช้ DNS ของ Cloudflare แล้ว ค่า DNS เดิมถูกเก็บไว้สำหรับปุ่มคืนค่า"));
    }

    public static async Task<TrickResult> RestoreAsync(CancellationToken ct)
    {
        if (!IsSupported) return TrickResult.Fail(new LocText("Needs Windows 10 version 2004 or newer.", "ต้องใช้ Windows 10 เวอร์ชัน 2004 ขึ้นไป"));

        var backup = LoadBackup();
        var failed = new List<string>();
        var restoredAny = false;
        var notConnected = 0;

        if (backup != null)
        {
            var present = NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => Guid.TryParse(n.Id, out var g) ? g : Guid.Empty)
                .ToHashSet();

            foreach (var entry in backup.Adapters.ToList())
            {
                var id = Guid.Parse(entry.Id);
                if (!present.Contains(id))
                {
                    // Unplugged/disabled right now: keep its backup for a later Restore.
                    notConnected++;
                    continue;
                }

                try
                {
                    WriteServers(id, v6: false, entry.V4);
                    if (entry.ChangedV6) WriteServers(id, v6: true, entry.V6);
                    if (ReadServers(id, v6: false) == entry.V4)
                    {
                        backup.Adapters.Remove(entry);
                        restoredAny = true;
                    }
                    else failed.Add(entry.Name);
                }
                catch
                {
                    failed.Add(entry.Name);
                }
            }

            try
            {
                if (backup.Adapters.Count == 0) SecureAppData.Delete(BackupFile);
                else SecureAppData.Save(BackupFile, backup);
            }
            catch { /* kept entries will be retried next time */ }
        }

        // Adapters still on Cloudflare with no saved original: back to automatic.
        bool usedAutomatic = false;
        foreach (var a in GetActivePhysicalAdapters())
        {
            try
            {
                if (ReadServers(a.Id, v6: false) != CloudflareV4) continue;
                WriteServers(a.Id, v6: false, "");
                if (ReadServers(a.Id, v6: true) == CloudflareV6) WriteServers(a.Id, v6: true, "");
                usedAutomatic = true;
            }
            catch
            {
                failed.Add(a.Name);
            }
        }

        await FlushDnsCacheAsync(ct).ConfigureAwait(false);

        if (failed.Count > 0)
            return TrickResult.Fail(new LocText(
                $"Could not restore DNS on: {string.Join(", ", failed)}. Please try again.",
                $"คืนค่า DNS ไม่สำเร็จบน: {string.Join(", ", failed)} กรุณาลองใหม่"));

        if (notConnected > 0)
            return TrickResult.Ok(new LocText(
                "Restored on connected adapters. Adapters that are unplugged right now keep their backup — press Restore again after reconnecting them.",
                "คืนค่าให้การ์ดเครือข่ายที่เชื่อมต่ออยู่แล้ว ส่วนการ์ดที่ยังไม่ได้เสียบ/ปิดอยู่ ค่าเดิมยังเก็บไว้ — กดคืนค่าอีกครั้งหลังเชื่อมต่อ"));

        if (usedAutomatic && !restoredAny)
            return TrickResult.Ok(new LocText(
                "No saved DNS settings were found, so DNS was set back to automatic (from your router).",
                "ไม่พบค่า DNS เดิมที่บันทึกไว้ จึงตั้ง DNS กลับเป็นอัตโนมัติ (จากเราเตอร์)"));

        if (!restoredAny)
            return TrickResult.Ok(new LocText(
                "Nothing to restore — no adapter is using the Cloudflare DNS set by WinXTools.",
                "ไม่มีอะไรต้องคืนค่า — ไม่มีการ์ดเครือข่ายที่ใช้ DNS ของ Cloudflare ที่ WinXTools ตั้งไว้"));

        return TrickResult.Ok(new LocText("Done — your previous DNS servers are back.", "เรียบร้อย — คืนค่า DNS เดิมของคุณแล้ว"));
    }

    private static Backup? LoadBackup()
    {
        var backup = SecureAppData.Load<Backup>(BackupFile);
        if (backup == null) return null;

        // Defense in depth: only well-formed entries are ever written back.
        backup.Adapters = backup.Adapters
            .Where(e => Guid.TryParse(e.Id, out _)
                        && IsValidList(e.V4 = Normalize(e.V4 ?? ""), AddressFamily.InterNetwork)
                        && IsValidList(e.V6 = Normalize(e.V6 ?? ""), AddressFamily.InterNetworkV6))
            .GroupBy(e => e.Id.ToLowerInvariant())
            .Select(g => g.First())
            .Take(32)
            .ToList();
        foreach (var e in backup.Adapters)
        {
            // Only ever displayed, but keep it short and printable.
            var name = new string((e.Name ?? "").Where(ch => !char.IsControl(ch)).Take(64).ToArray());
            e.Name = name.Length > 0 ? name : e.Id;
        }
        return backup;
    }

    private static async Task FlushDnsCacheAsync(CancellationToken ct)
    {
        try
        {
            await CommandRunner.RunToolAsync("ipconfig.exe", "/flushdns", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch { /* cache flush is best-effort */ }
    }

    #region Native

    private const uint DNS_INTERFACE_SETTINGS_VERSION1 = 1;
    private const ulong DNS_SETTING_IPV6 = 0x0001;
    private const ulong DNS_SETTING_NAMESERVER = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_INTERFACE_SETTINGS
    {
        public uint Version;
        public ulong Flags;
        public IntPtr Domain;
        public IntPtr NameServer;
        public IntPtr SearchList;
        public uint RegistrationEnabled;
        public uint RegisterAdapterName;
        public uint EnableLLMNR;
        public uint QueryAdapterName;
        public IntPtr ProfileNameServer;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetInterfaceDnsSettings(Guid interfaceId, ref DNS_INTERFACE_SETTINGS settings);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetInterfaceDnsSettings(Guid interfaceId, ref DNS_INTERFACE_SETTINGS settings);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeInterfaceDnsSettings(ref DNS_INTERFACE_SETTINGS settings);

    #endregion
}

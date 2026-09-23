namespace NetX.Core.System.Tweaks;

public static partial class TrickCatalog
{
    private static void AddNetwork(List<TrickDefinition> list, WindowsVersionInfo os)
    {
        list.Add(new TrickDefinition
        {
            Id = "flush-dns",
            Category = Network,
            Icon = "NetworkIcon",
            Name = L("Clear the DNS cache", "ล้างแคช DNS"),
            Description = L("Forgets remembered website addresses. Fixes \"site can't be reached\" after a website or DNS change.",
                            "ล้างที่อยู่เว็บไซต์ที่เครื่องจำไว้ ช่วยแก้ปัญหาเข้าเว็บไม่ได้หลังเว็บไซต์หรือ DNS เปลี่ยน"),
            Risk = TrickRisk.Safe,
            Technical = "ipconfig /flushdns",
            CopyText = "ipconfig /flushdns",
            RunLabel = L("Clear", "ล้างเลย"),
            Run = ctx => RunTool(ctx, "ipconfig.exe", "/flushdns", Short, L("Done — the DNS cache was cleared.", "เรียบร้อย — ล้างแคช DNS แล้ว"))
        });

        list.Add(new TrickDefinition
        {
            Id = "reset-network",
            Category = Network,
            Icon = "NetworkIcon",
            Name = L("Reset network (Winsock + TCP/IP)", "รีเซ็ตเครือข่าย (Winsock + TCP/IP)"),
            Description = L(
                "Repairs a broken internet connection by resetting Winsock and the TCP/IP settings. Your firewall rules are NOT touched. " +
                "A restart is needed afterwards.",
                "ซ่อมการเชื่อมต่ออินเทอร์เน็ตที่เสียด้วยการรีเซ็ต Winsock และค่า TCP/IP โดยไม่แตะกฎไฟร์วอลล์ ต้องรีสตาร์ทเครื่องหลังทำ"),
            Warning = L("If you typed a fixed (static) IP address or DNS by hand, write it down first — you may need to enter it again. " +
                        "VPN and proxy apps may need to be reinstalled.",
                        "ถ้าคุณตั้ง IP หรือ DNS แบบกำหนดเองไว้ ให้จดไว้ก่อน อาจต้องตั้งใหม่ และแอป VPN/Proxy บางตัวอาจต้องติดตั้งใหม่"),
            Risk = TrickRisk.Moderate,
            Restart = RestartScope.Reboot,
            Technical = "netsh winsock reset\nnetsh int ip reset",
            CopyText = "netsh winsock reset\r\nnetsh int ip reset",
            ShowsOutput = true,
            RunLabel = L("Reset", "รีเซ็ต"),
            Run = ResetNetworkAsync
        });

        list.Add(new TrickDefinition
        {
            Id = "winsock-reset",
            Category = Network,
            Icon = "NetworkIcon",
            Name = L("Reset Winsock only", "รีเซ็ตเฉพาะ Winsock"),
            Description = L("A lighter fix for internet problems left behind by VPN, proxy or \"internet booster\" apps. A restart is needed afterwards.",
                            "วิธีแก้แบบเบากว่า สำหรับปัญหาอินเทอร์เน็ตที่เกิดจากแอป VPN, Proxy หรือโปรแกรม \"เร่งเน็ต\" ต้องรีสตาร์ทเครื่องหลังทำ"),
            Warning = L("VPN and proxy apps may need to be reinstalled.", "แอป VPN/Proxy บางตัวอาจต้องติดตั้งใหม่"),
            Risk = TrickRisk.Moderate,
            Restart = RestartScope.Reboot,
            Technical = "netsh winsock reset",
            CopyText = "netsh winsock reset",
            ShowsOutput = true,
            RunLabel = L("Reset", "รีเซ็ต"),
            Run = ctx => RunTool(ctx, "netsh.exe", "winsock reset", Medium,
                L("Winsock was reset. Restart the PC to finish.", "รีเซ็ต Winsock แล้ว กรุณารีสตาร์ทเครื่องเพื่อให้เสร็จสมบูรณ์"),
                RestartScope.Reboot)
        });

        list.Add(new TrickDefinition
        {
            Id = "wifi-passwords",
            Category = Network,
            Icon = "NetworkIcon",
            Name = L("Show saved Wi-Fi passwords", "ดูรหัสผ่าน Wi-Fi ที่บันทึกไว้"),
            Description = L("Lists every Wi-Fi network this PC remembers, with its password — handy when connecting a new phone.",
                            "แสดงเครือข่าย Wi-Fi ทั้งหมดที่เครื่องจำไว้ พร้อมรหัสผ่าน สะดวกเวลาจะเชื่อมต่อมือถือเครื่องใหม่"),
            Risk = TrickRisk.Safe,
            Technical = "WlanGetProfile(WLAN_PROFILE_GET_PLAINTEXT_KEY) — read in-app, nothing is saved to disk",
            ShowsOutput = true,
            RunLabel = L("Show", "แสดง"),
            Run = ctx => Task.Run(() =>
            {
                var result = WifiProfiles.BuildReport(ctx.Thai);
                if (!string.IsNullOrEmpty(result.Output)) ctx.Output.Append(result.Output);
                return result;
            })
        });

        list.Add(RegToggle("network-throttling", Network, "NetworkIcon",
            L("Turn off multimedia network throttling", "ปิดการจำกัดเครือข่ายขณะเล่นสื่อ (Network Throttling)"),
            L("While music or video plays, Windows slightly limits other network traffic (MMCSS). Turning this off can help on very fast " +
              "connections; most people won't notice a difference.",
              "ขณะเล่นเพลงหรือวิดีโอ Windows จะจำกัดการรับส่งข้อมูลอื่นเล็กน้อย (MMCSS) การปิดอาจช่วยในเน็ตความเร็วสูงมาก " +
              "แต่คนส่วนใหญ่จะไม่รู้สึกต่าง"),
            TrickRisk.Safe, RestartScope.Reboot,
            new[] { Dword(HKLM, MultimediaProfile, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10) }));

        list.Add(new TrickDefinition
        {
            Id = "active-connections",
            Category = Network,
            Icon = "NetworkIcon",
            Name = L("Show active connections", "ดูการเชื่อมต่อที่กำลังใช้งาน"),
            Description = L("Lists open network connections and the program behind each one (netstat -b).",
                            "แสดงการเชื่อมต่อเครือข่ายที่เปิดอยู่ และโปรแกรมที่ใช้แต่ละการเชื่อมต่อ (netstat -b)"),
            Risk = TrickRisk.Safe,
            Technical = "netstat -b -n",
            CopyText = "netstat -b -n",
            ShowsOutput = true,
            Cancellable = true,
            RunLabel = L("Show", "แสดง"),
            Run = ctx => RunTool(ctx, "netstat.exe", "-b -n", Medium, L("Done.", "เสร็จแล้ว"))
        });

        list.Add(new TrickDefinition
        {
            Id = "renew-ip",
            Category = Network,
            Icon = "NetworkIcon",
            Name = L("Get a new IP address", "ขอ IP address ใหม่"),
            Description = L("Releases and renews the IP address from your router (DHCP). Fixes some \"no internet\" problems.",
                            "คืนและขอ IP ใหม่จากเราเตอร์ (DHCP) ช่วยแก้ปัญหา \"ไม่มีอินเทอร์เน็ต\" ได้บางกรณี"),
            Warning = L("You'll be offline for a few seconds: downloads, calls and online games will disconnect.",
                        "อินเทอร์เน็ตจะหลุดไปไม่กี่วินาที การดาวน์โหลด การโทร และเกมออนไลน์จะหลุด"),
            Risk = TrickRisk.Moderate,
            Technical = "ipconfig /release\nipconfig /renew",
            CopyText = "ipconfig /release\r\nipconfig /renew",
            ShowsOutput = true,
            RunLabel = L("Renew", "ขอ IP ใหม่"),
            Run = RenewIpAsync
        });

        list.Add(new TrickDefinition
        {
            Id = "tcp-autotuning",
            Category = Network,
            Icon = "NetworkIcon",
            Name = L("Restore TCP auto-tuning", "คืนค่า TCP Auto-Tuning เป็นค่าเริ่มต้น"),
            Description = L(
                "Sets TCP receive-window auto-tuning back to \"normal\", the Windows default. Fixes slow downloads caused by old " +
                "\"internet booster\" tweaks. If it is already normal, nothing changes.",
                "ตั้งค่า TCP Auto-Tuning กลับเป็น \"normal\" ซึ่งเป็นค่าเริ่มต้นของ Windows ช่วยแก้ดาวน์โหลดช้าที่เกิดจากโปรแกรมปรับแต่งเน็ตรุ่นเก่า " +
                "ถ้าเป็นค่าปกติอยู่แล้วจะไม่มีอะไรเปลี่ยน"),
            Risk = TrickRisk.Safe,
            Technical = "netsh int tcp set global autotuninglevel=normal",
            CopyText = "netsh int tcp set global autotuninglevel=normal",
            RunLabel = L("Restore", "คืนค่า"),
            Run = ctx => RunTool(ctx, "netsh.exe", "int tcp set global autotuninglevel=normal", Short,
                L("Done — TCP auto-tuning is set to normal.", "เรียบร้อย — ตั้ง TCP Auto-Tuning เป็น normal แล้ว"))
        });

        list.Add(RegToggle("delivery-optimization", Network, "NetworkIcon",
            L("Stop sharing updates with other PCs", "ไม่แชร์ไฟล์อัปเดตกับคอมเครื่องอื่น"),
            L("Delivery Optimization can upload Windows updates to other PCs. With this, Windows downloads updates from Microsoft only " +
              "and never uploads them.",
              "Delivery Optimization อาจอัปโหลดไฟล์อัปเดต Windows ให้คอมเครื่องอื่น ตัวเลือกนี้ให้ดาวน์โหลดจาก Microsoft เท่านั้นและไม่อัปโหลดเลย"),
            TrickRisk.Safe, RestartScope.None,
            new[] { Dword(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, null) }));

        list.Add(new TrickDefinition
        {
            Id = "dns-cloudflare",
            Category = Network,
            Icon = "NetworkIcon",
            Name = L("Use Cloudflare DNS (1.1.1.1)", "ใช้ DNS ของ Cloudflare (1.1.1.1)"),
            Description = L(
                "Switches your connected network adapters to Cloudflare's fast, private DNS. Your current DNS servers are saved, and " +
                "Restore puts exactly those back.",
                "เปลี่ยน DNS ของการ์ดเครือข่ายที่เชื่อมต่ออยู่เป็นของ Cloudflare ที่เร็วและเป็นส่วนตัว ค่า DNS เดิมจะถูกบันทึกไว้ " +
                "และปุ่มคืนค่าจะคืนค่าเดิมให้ทุกประการ"),
            Warning = L("Office or school networks, and some internet providers' own services, may need their own DNS. " +
                        "Use \"Restore previous DNS\" if something stops working.",
                        "เครือข่ายที่ทำงาน/โรงเรียน และบริการบางอย่างของผู้ให้บริการอินเทอร์เน็ต อาจต้องใช้ DNS ของตัวเอง " +
                        "ถ้ามีอะไรใช้งานไม่ได้ให้กด \"คืนค่า DNS เดิม\""),
            Risk = TrickRisk.Moderate,
            Technical = $"IPv4: {DnsTweak.CloudflareV4}\nIPv6: {DnsTweak.CloudflareV6} (only when the adapter has IPv6)",
            RevertLabel = L("Restore previous DNS", "คืนค่า DNS เดิม"),
            AppliedLabel = L("Cloudflare DNS in use", "ใช้ DNS ของ Cloudflare อยู่"),
            DefaultLabel = L("Not in use", "ไม่ได้ใช้อยู่"),
            CustomLabel = L("In use on some adapters", "ใช้อยู่บางการ์ดเครือข่าย"),
            Detect = DnsTweak.Detect,
            Apply = ctx => DnsTweak.ApplyAsync(ctx.Token),
            Revert = ctx => DnsTweak.RestoreAsync(ctx.Token)
        });

        list.Add(RegToggle("prefer-ipv4", Network, "NetworkIcon",
            L("Prefer IPv4 over IPv6", "ให้ใช้ IPv4 ก่อน IPv6"),
            L("When a site supports both, Windows tries IPv4 first. Helps when your IPv6 connection is slow or unreliable. " +
              "IPv6 stays turned on.",
              "เมื่อเว็บไซต์รองรับทั้งสองแบบ Windows จะลองใช้ IPv4 ก่อน ช่วยได้เมื่อ IPv6 ของคุณช้าหรือไม่เสถียร โดย IPv6 ยังเปิดอยู่ตามปกติ"),
            TrickRisk.Safe, RestartScope.Reboot,
            new[] { Dword(HKLM, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", 0x20, null, 0) }));
    }

    private static async Task<TrickResult> ResetNetworkAsync(TrickRunContext ctx)
    {
        // Run both steps even if the first reports a problem (no "&&" chain).
        var winsock = await RunTool(ctx, "netsh.exe", "winsock reset", Medium, TweakErrors.AppliedText).ConfigureAwait(false);
        if (winsock.Cancelled) return winsock;
        var ip = await RunTool(ctx, "netsh.exe", "int ip reset", Medium, TweakErrors.AppliedText).ConfigureAwait(false);
        if (ip.Cancelled) return ip;

        if (winsock.Success && ip.Success)
            return TrickResult.Ok(L("Network settings were reset. Restart the PC to finish.",
                                    "รีเซ็ตการตั้งค่าเครือข่ายแล้ว กรุณารีสตาร์ทเครื่องเพื่อให้เสร็จสมบูรณ์"), RestartScope.Reboot);

        return new TrickResult
        {
            Success = false,
            Restart = winsock.Success || ip.Success ? RestartScope.Reboot : RestartScope.None,
            Message = L(
                $"Winsock reset: {(winsock.Success ? "OK" : "failed")}. TCP/IP reset: {(ip.Success ? "OK" : "reported errors")}. " +
                "See the details above; restart the PC for the parts that worked to take effect.",
                $"รีเซ็ต Winsock: {(winsock.Success ? "สำเร็จ" : "ไม่สำเร็จ")} รีเซ็ต TCP/IP: {(ip.Success ? "สำเร็จ" : "มีข้อผิดพลาด")} " +
                "ดูรายละเอียดด้านบน และรีสตาร์ทเครื่องเพื่อให้ส่วนที่สำเร็จมีผล")
        };
    }

    private static async Task<TrickResult> RenewIpAsync(TrickRunContext ctx)
    {
        // Release may fail on adapters without DHCP; renew must run regardless
        // (and is never cancelled), otherwise the PC would stay offline.
        ctx.Output.AppendLine("> ipconfig /release");
        await CommandRunner.RunToolAsync("ipconfig.exe", "/release", TimeSpan.FromMinutes(1), CancellationToken.None, ctx.Output)
            .ConfigureAwait(false);
        ctx.Output.AppendLine();
        ctx.Output.AppendLine("> ipconfig /renew");
        var renew = await CommandRunner.RunToolAsync("ipconfig.exe", "/renew", TimeSpan.FromMinutes(2), CancellationToken.None, ctx.Output)
            .ConfigureAwait(false);

        return renew.Succeeded
            ? TrickResult.Ok(L("Done — a new IP address was requested.", "เรียบร้อย — ขอ IP ใหม่แล้ว"))
            : TrickResult.Fail(L("A new IP address couldn't be obtained. Check the cable/Wi-Fi and your router, then try again.",
                                 "ขอ IP ใหม่ไม่สำเร็จ ตรวจสอบสายแลน/Wi-Fi และเราเตอร์ แล้วลองอีกครั้ง"), renew.Output);
    }
}

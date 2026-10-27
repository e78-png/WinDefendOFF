using System.Diagnostics;
using Microsoft.Win32;

namespace WinDefendOFF.Core;

/// <summary>讀取 Defender 現況。所有讀取都不需要寫入權限，任何使用者皆可執行。</summary>
public static class DefenderStateReader
{
    private const string IfeoKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    private const string DefenderFeaturesKey = @"SOFTWARE\Microsoft\Windows Defender";

    public static DefenderState Read()
    {
        var services = new List<ServiceState>();
        foreach (var name in DefenderServices.All)
        {
            services.Add(ReadService(name));
        }

        var engineRunning = IsProcessRunning("MsMpEng");
        var ifeoPresent = DefenderServices.IfeoTargets.Any(t =>
            ReadString($@"{IfeoKey}\{t}", "Debugger") is not null);

        var tamperOn = ReadDword(DefenderFeaturesKey, "TamperProtection") is { } tp && tp is 4 or 5;
        var rtp = ReadMpBool("RealTimeProtectionEnabled");
        var antisyware = ReadMpBool("AntispywareEnabled");
        var amService = ReadMpBool("AMServiceEnabled");

        var disabled = DefenderServices.EngineCritical.All(n =>
        {
            var s = services.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            return s is { IsDisabled: true };
        }) && !antisyware;

        return new DefenderState
        {
            Services = services,
            EngineRunning = engineRunning,
            IsDisabled = disabled,
            RealtimeProtectionEnabled = rtp,
            AntispywareEnabled = antisyware,
            AmServiceEnabled = amService,
            TamperProtectionEnabled = tamperOn,
            IfeoBlockPresent = ifeoPresent,
        };
    }

    private static ServiceState ReadService(string name)
    {
        var key = $@"SYSTEM\CurrentControlSet\Services\{name}";
        var start = ReadDword(key, "Start");
        var status = 1;
        var display = name;

        // 服務的即時狀態需要 SCM；用 registry 只能拿到啟動型態。
        // 這裡用 Process 與 Start 值推導，避免依賴 System.ServiceProcess 套件。
        var sc = RunSc("query", name);
        if (sc is not null && sc.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            status = 4;
        else if (sc is not null && sc.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            status = 1;
        else if (sc is not null && sc.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
            status = 2;

        return new ServiceState
        {
            Name = name,
            DisplayName = display,
            Status = status switch { 4 => "Running", 2 => "StartPending", _ => "Stopped" },
            StartType = start switch
            {
                0 => "Boot",
                1 => "System",
                2 => "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Unknown",
            },
        };
    }

    private static string? RunSc(string verb, string service)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", $"{verb} {service}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return stdout;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool RegistryKeyExists(string subKey)
    {
        try
        {
            using var hk = Registry.LocalMachine.OpenSubKey(subKey);
            return hk is not null;
        }
        catch
        {
            return false;
        }
    }

    public static int? ReadDword(string subKey, string valueName)
    {
        try
        {
            using var hk = Registry.LocalMachine.OpenSubKey(subKey);
            var v = hk?.GetValue(valueName);
            return v is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>讀取字串型登錄檔值 (IFEO 的 Debugger 為 REG_SZ)。</summary>
    public static string? ReadString(string subKey, string valueName)
    {
        try
        {
            using var hk = Registry.LocalMachine.OpenSubKey(subKey);
            var v = hk?.GetValue(valueName);
            return v as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 透過 WMI (root\Microsoft\Windows\Defender\MSFT_MpComputerStatus) 讀取防護狀態。
    /// WMI provider 在 Defender 停用後仍可查詢，故為可靠的狀態來源。
    /// </summary>
    private static bool ReadMpBool(string propertyName)
    {
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null) return true;

            dynamic? locator = Activator.CreateInstance(locatorType);
            dynamic? services = locator?.ConnectServer(".", "root\\Microsoft\\Windows\\Defender");
            if (services is null) return true;

            foreach (dynamic obj in services.ExecQuery("SELECT * FROM MSFT_MpComputerStatus"))
            {
                try
                {
                    var value = obj[propertyName];
                    if (value is bool b) return b;
                }
                catch
                {
                    // 屬性不存在時忽略
                }
            }
        }
        catch
        {
            // 查詢失敗時保守假設防護仍在運作
        }
        return true;
    }
}

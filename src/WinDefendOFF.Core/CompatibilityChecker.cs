using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinDefendOFF.Core;

/// <summary>
/// 環境相容性檢查。在執行停用/啟用前先確認本機是否具備必要條件，
/// 讓使用者能立刻知道「能不能用」而不是在流程中途失敗。
/// </summary>
public sealed class CompatibilityChecker
{
    private const string IfeoKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>檢查結果的一個項目。</summary>
    public sealed record CheckItem(
        string Name,
        bool Passed,
        bool Critical,
        string Detail,
        string? Remedy = null);

    /// <summary>整體檢查結果。</summary>
    public sealed record Result(
        IReadOnlyList<CheckItem> Items,
        bool CanDisableWithoutReboot,
        bool CanEnableWithoutReboot)
    {
        public IEnumerable<CheckItem> Failures => Items.Where(i => !i.Passed);
        public IEnumerable<CheckItem> CriticalFailures => Items.Where(i => !i.Passed && i.Critical);
    }

    public Result Check()
    {
        var items = new List<CheckItem>
        {
            CheckArchitecture(),
            CheckWindowsVersion(),
            CheckAdmin(),
            CheckDefenderPresent(),
            CheckOfficialMechanismsAvailable(),
            CheckServiceControl(),
        };

        var criticalOk = items.All(i => i.Passed || !i.Critical);
        return new Result(items, criticalOk, criticalOk);
    }

    private static CheckItem CheckArchitecture()
    {
        var is64 = Environment.Is64BitOperatingSystem;
        var proc64 = Environment.Is64BitProcess;
        return new CheckItem(
            "系統架構",
            is64 && proc64,
            Critical: true,
            is64 && proc64
                ? "64 位元 (x64) — 相符"
                : "需要 64 位元 Windows（目前 OS 64 位元=" + is64 + "，程序 64 位元=" + proc64 + "）",
            "請改用 x64 版本");
    }

    private static CheckItem CheckWindowsVersion()
    {
        // Windows 10 1809 (build 17763) 起才有 Defender Platform 與相關 WMI 類別
        try
        {
            var build = Environment.OSVersion.Version.Build;
            var ok = build >= 17763;
            return new CheckItem(
                "Windows 版本",
                ok,
                Critical: true,
                "Build " + build + (ok ? " — 相符" : " — 過舊"),
                "需要 Windows 10 1809 (build 17763) 以上");
        }
        catch (Exception ex)
        {
            return new CheckItem("Windows 版本", false, true, "無法判斷: " + ex.Message);
        }
    }

    private static CheckItem CheckAdmin()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            var isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            return new CheckItem(
                "系統管理員權限",
                isAdmin,
                Critical: true,
                isAdmin ? "已提升 — 相符" : "未提升",
                "以系統管理員身分執行（程式會自動觸發 UAC）");
        }
        catch (Exception ex)
        {
            return new CheckItem("系統管理員權限", false, true, "無法判斷: " + ex.Message);
        }
    }

    private static CheckItem CheckDefenderPresent()
    {
        var svc = GetServiceState("WinDefend");
        var exists = svc is not null;
        return new CheckItem(
            "Microsoft Defender 服務",
            exists,
            Critical: true,
            exists ? "WinDefend 存在 — 相符" : "WinDefend 服務不存在",
            "此電腦可能未安裝 Defender");
    }

    private static CheckItem CheckOfficialMechanismsAvailable()
    {
        // 本工具不使用任何第三方核心驅動，全部仰賴 Windows 官方機制：
        //   - Set-MpPreference -DisableRealtimeMonitoring
        //   - Image File Execution Options (IFEO)
        //   - 服務 Start 值
        // 因此只需確認這三者皆可用。
        var checks = new List<(string Name, bool Ok, string Detail)>();

        // Set-MpPreference 是否可用（由 WMI 能否查詢 MSFT_MpComputerStatus 推斷）
        var mpAvailable = CanQueryMpStatus();
        checks.Add(("Set-MpPreference", mpAvailable,
            mpAvailable ? "可用 — 可關閉即時掃描與行為監控" : "無法查詢 Defender 狀態"));

        // 群組原則是否可寫（管理員即可）
        var policyWritable = CanWritePolicyKey();
        checks.Add(("群組原則寫入", policyWritable,
            policyWritable ? "可用 — 可設定停用原則" : "無法寫入群組原則"));

        // IFEO 鍵是否可存取
        var ifeoAccessible = CanAccessIfeoKey();
        checks.Add(("IFEO 封鎖機制", ifeoAccessible,
            ifeoAccessible ? "可用 — 可阻止引擎載入" : "無法存取 IFEO 機碼"));

        // 服務 Start 值是否可寫（需 SYSTEM，透過排程工作達成）
        var svcWritable = CanWriteServiceStart();
        checks.Add(("服務啟動型態", svcWritable,
            svcWritable ? "可用 — 可設定服務開機啟動" : "需以 SYSTEM 權限處理（程式會自動建立排程工作）"));

        var allOk = checks.All(c => c.Ok);
        var detail = string.Join("；", checks.Select(c => $"{c.Name}: {(c.Ok ? "可用" : "不可用")}"));

        return new CheckItem(
            "官方停用機制",
            allOk,
            Critical: true,
            detail,
            allOk ? null : "缺少關鍵機制，無法保證停用效果");
    }

    private static bool CanQueryMpStatus()
    {
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (locatorType is null) return false;
            dynamic? locator = Activator.CreateInstance(locatorType);
            dynamic? services = locator?.ConnectServer(".", "root\\Microsoft\\Windows\\Defender");
            if (services is null) return false;
            foreach (dynamic _ in services.ExecQuery("SELECT * FROM MSFT_MpComputerStatus")) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanWritePolicyKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                RegistryPaths.PolicyRootKey(), writable: true);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanAccessIfeoKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPaths.ToSubKey(RegistryPaths.IfeoLive));
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanWriteServiceStart()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                RegistryPaths.ToSubKey(RegistryPaths.ServicesLive) + "\\WdNisSvc", writable: true);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static CheckItem CheckServiceControl()
    {
        // WinDefend 為 protected service：sc stop 會被拒，但 sc start 可以。
        // 這是「不需重開機」的關鍵前提。
        var canStart = CanOpenServiceForStart("WinDefend");
        return new CheckItem(
            "服務控制 (protected service)",
            canStart,
            Critical: true,
            canStart
                ? "sc start 允許 — 可即時切換"
                : "無法以 SERVICE_START 開啟 WinDefend 服務",
            "請以系統管理員身分執行");
    }

    private static bool CanOpenServiceForStart(string serviceName)
    {
        try
        {
            var scm = OpenSCManager(IntPtr.Zero, IntPtr.Zero, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return false;
            try
            {
                // SERVICE_START | SERVICE_QUERY_STATUS
                const int access = 0x0010 | 0x0004;
                var handle = OpenService(scm, serviceName, access);
                if (handle == IntPtr.Zero) return false;
                CloseServiceHandle(handle);
                return true;
            }
            finally
            {
                CloseServiceHandle(scm);
            }
        }
        catch
        {
            return false;
        }
    }

    private static ServiceState? GetServiceState(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            if (key is null) return null;
            var start = key.GetValue("Start") is int i ? i : -1;
            return new ServiceState
            {
                Name = name,
                DisplayName = name,
                Status = "Unknown",
                StartType = start switch
                {
                    0 => "Boot", 1 => "System", 2 => "Automatic", 3 => "Manual", 4 => "Disabled", _ => "Unknown",
                },
            };
        }
        catch
        {
            return null;
        }
    }

    // ---- Win32 SCM interop ----
    private const int SC_MANAGER_CONNECT = 0x0001;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenSCManager(IntPtr machine, IntPtr database, int access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string serviceName, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

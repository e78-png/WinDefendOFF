using System.Diagnostics;
using System.Text;

namespace WinDefendOFF.Core;

/// <summary>
/// 登錄檔子樹的離線編輯器。
///
/// 背景：Defender 對下列位置施加 WRP (Windows Resource Protection)，任何使用者/管理員
/// (含 SYSTEM) 都無法直接 SetValue：
///   - HKLM\SYSTEM\CurrentControlSet\Services\WinDefend
///   - HKLM\SYSTEM\CurrentControlSet\Services\WdNisSvc
///   - HKLM\SOFTWARE\Microsoft\Windows Defender\Features
///
/// 可行手法 (與 DefenderControl / WinDefCtl 開源專案相同)：
///   1. reg save  匯出受保護的「父」子樹 (父鍵可讀，save 需要 SeBackupPrivilege)
///   2. reg load  以暫時名載入成可寫的 hive
///   3. reg add   在暫時 hive 上修改數值
///   4. reg unload + reg restore  整樹還原 (restore 需要 SeRestorePrivilege)
///
/// 還原父樹時，reg restore 會以匯出時的內容覆寫整個子樹，包含先前被 DACL 保護的子鍵，
/// 因此可成功寫入那些直接寫不進去的鍵。
/// </summary>
public sealed class HiveEditor : IRegistryEditor
{
    private readonly string _hiveDir;

    public HiveEditor(string hiveDir)
    {
        _hiveDir = hiveDir;
        Directory.CreateDirectory(_hiveDir);
    }

    /// <summary>對單一登錄檔值執行的修改。</summary>
    /// <param name="SubKey">相對於被載入 hive 的子鍵路徑；<c>@ROOT</c> 代表 hive 根。</param>
    /// <param name="ValueName">值名稱。</param>
    /// <param name="DwordValue">DWORD 值；非 null 時以此為準。</param>
    /// <param name="StringValue">REG_SZ 值。</param>
    public sealed record ValueEdit(string SubKey, string ValueName, int? DwordValue, string? StringValue);

    /// <summary>要刪除的登錄檔值。</summary>
    public sealed record ValueDelete(string SubKey, string ValueName);

    public int? ReadDword(string subKey, string valueName)
    {
        try
        {
            using var hk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
            var v = hk?.GetValue(valueName);
            return v is int i ? i : null;
        }
        catch
        {
            return null;
        }
    }

    public string? ReadString(string subKey, string valueName)
    {
        try
        {
            using var hk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
            return hk?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public bool WriteDword(string subKey, string valueName, int value)
    {
        try
        {
            using var hk = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(subKey, writable: true);
            if (hk is null) return false;
            hk.SetValue(valueName, value, Microsoft.Win32.RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            // WRP 保護的位置會拋出 UnauthorizedAccessException
            return false;
        }
    }

    public bool DeleteValue(string subKey, string valueName)
    {
        try
        {
            using var hk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey, writable: true);
            if (hk is null) return true; // 鍵不存在，視為已刪除
            hk.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool WriteString(string subKey, string valueName, string value)
    {
        try
        {
            using var hk = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(subKey, writable: true);
            if (hk is null) return false;
            hk.SetValue(valueName, value, Microsoft.Win32.RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public HiveEditResult EditSubtree(
        string liveKeyPath,
        IReadOnlyList<ValueEdit> edits,
        IReadOnlyList<ValueDelete> deletes,
        Action<string>? log = null)
    {
        // 使用唯一檔名：先前殘留的 SYSTEM 工作可能仍持有舊檔案，
        // 共用檔名會導致刪除時被鎖定而拋出 IOException。
        var tempName = "WdcHive_" + Guid.NewGuid().ToString("N")[..8];
        var hiveFile = Path.Combine(_hiveDir, tempName + ".hiv");
        var tempKey = $"HKLM\\{tempName}";
        var messages = new List<string>();

        void Log(string m)
        {
            messages.Add(m);
            log?.Invoke(m);
        }

        try
        {
            // 清理可能殘留的暫存 hive
            RunReg("unload", tempKey);
            foreach (var extra in new[] { hiveFile, hiveFile + ".LOG1", hiveFile + ".LOG2" })
                TryDelete(extra);

            var save = RunReg("save", liveKeyPath, hiveFile, "/y");
            if (save.ExitCode != 0 || !File.Exists(hiveFile))
            {
                Log($"[失敗] reg save {liveKeyPath}: {save.Output.Trim()}");
                return new HiveEditResult(false, messages, "匯出登錄檔子樹失敗");
            }
            Log($"[成功] 匯出 {liveKeyPath}");

            var load = RunReg("load", tempKey, hiveFile);
            if (load.ExitCode != 0)
            {
                Log($"[失敗] reg load: {load.Output.Trim()}");
                return new HiveEditResult(false, messages, "載入暫存 hive 失敗");
            }
            Log($"[成功] 載入暫存 hive {tempName}");

            foreach (var e in edits)
            {
                var sub = ResolveSubKey(tempKey, e.SubKey);
                var args = e.DwordValue.HasValue
                    ? new[] { "add", sub, "/v", e.ValueName, "/t", "REG_DWORD", "/d", e.DwordValue.Value.ToString(), "/f" }
                    : new[] { "add", sub, "/v", e.ValueName, "/t", "REG_SZ", "/d", e.StringValue ?? string.Empty, "/f" };
                var r = RunReg(args);
                Log(r.ExitCode == 0
                    ? $"[成功] 設定 {e.SubKey}\\{e.ValueName}"
                    : $"[失敗] 設定 {e.SubKey}\\{e.ValueName}: {r.Output.Trim()}");
            }

            foreach (var d in deletes)
            {
                var sub = ResolveSubKey(tempKey, d.SubKey);
                var r = RunReg("delete", sub, "/v", d.ValueName, "/f");
                Log(r.ExitCode == 0
                    ? $"[成功] 刪除 {d.SubKey}\\{d.ValueName}"
                    : $"[失敗] 刪除 {d.SubKey}\\{d.ValueName}: {r.Output.Trim()}");
            }

            RunReg("unload", tempKey);

            var restore = RunReg("restore", liveKeyPath, hiveFile, "/f");
            if (restore.ExitCode != 0)
            {
                Log($"[失敗] reg restore: {restore.Output.Trim()}");
                return new HiveEditResult(false, messages, "還原登錄檔子樹失敗");
            }
            Log($"[成功] 還原 {liveKeyPath}");
            return new HiveEditResult(true, messages, null);
        }
        finally
        {
            RunReg("unload", tempKey);
            foreach (var extra in new[] { hiveFile, hiveFile + ".LOG1", hiveFile + ".LOG2" })
                TryDelete(extra);
        }
    }

    /// <summary>
    /// 解析子鍵路徑。<see cref="RegistryEditPlan.RootSubKey"/> 代表 hive 根
    /// （用於群組原則根層的值）。
    /// </summary>
    internal static string ResolveSubKey(string tempKey, string subKey) =>
        subKey == RegistryEditPlan.RootSubKey ? tempKey : tempKey + "\\" + subKey;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 檔案可能被殘留的 SYSTEM 工作鎖定，忽略即可
        }
    }

    private static (int ExitCode, string Output) RunReg(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("reg.exe", string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, "cannot start reg.exe");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string Quote(string arg) =>
        arg.Contains(' ') && !arg.StartsWith('"') ? $"\"{arg}\"" : arg;
}

public sealed record HiveEditResult(bool Success, IReadOnlyList<string> Messages, string? Error)
{
    public string Summary => string.Join(Environment.NewLine, Messages);
}

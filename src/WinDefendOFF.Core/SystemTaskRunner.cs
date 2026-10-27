using System.Diagnostics;
using System.Text;

namespace WinDefendOFF.Core;

/// <summary>
/// 以 SYSTEM 身分執行腳本。
///
/// 為何需要 SYSTEM：Defender 對自身登錄檔 (HKLM\SYSTEM\CurrentControlSet\Services\WinDefend 等)
/// 施加 WRP 保護，即使是本機管理員也無法寫入；而能繞過的 reg save/load/restore 手法
/// 需要 SeBackupPrivilege + SeRestorePrivilege，這兩個權限一般使用者與管理員預設皆未啟用，
/// 只有 SYSTEM 具備。因此以 schtasks 建立一次性 SYSTEM 工作來執行實際操作。
/// </summary>
public sealed class SystemTaskRunner : ISystemExecutor
{
    private readonly string _taskName = "WinDefendOFF_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _workDir;
    private readonly Action<string>? _log;

    public SystemTaskRunner(string workDir, Action<string>? log = null)
    {
        _workDir = workDir;
        _log = log;
        Directory.CreateDirectory(workDir);
    }

    /// <summary>以 SYSTEM 身分執行 PowerShell 腳本，回傳腳本輸出與結束碼。</summary>
    public TaskRunResult RunScript(
        string scriptPath,
        TimeSpan timeout,
        IReadOnlyList<RegistryEditPlan>? plans = null)
    {
        // 使用唯一檔名：先前殘留的 SYSTEM 工作可能仍持有舊檔案，
        // 共用檔名會導致刪除時被鎖定而拋出 IOException。
        var runId = Guid.NewGuid().ToString("N")[..10];
        var stdoutPath = Path.Combine(_workDir, $"sysout_{runId}.txt");
        var stderrPath = Path.Combine(_workDir, $"syserr_{runId}.txt");
        var exitPath = Path.Combine(_workDir, $"sysexit_{runId}.txt");

        TryDelete(stdoutPath);
        TryDelete(stderrPath);
        TryDelete(exitPath);

        // 包裝腳本：導向輸出並寫出結束碼
        var wrapper = Path.Combine(_workDir, "wrapper.ps1");
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Continue'");
        sb.AppendLine($"& '{scriptPath}' *>> '{stdoutPath}'");
        sb.AppendLine("'0' | Out-File -FilePath '" + exitPath + "' -Encoding ascii");
        // UTF-8 BOM：確保 Windows PowerShell 5.1 以正確的編碼解析腳本
        File.WriteAllText(wrapper, sb.ToString(), new UTF8Encoding(true));

        _log?.Invoke($"建立 SYSTEM 工作 {_taskName}");

        var taskCommand = $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{wrapper}\"";
        var create = RunProcess("schtasks.exe", new[]
        {
            "/Create", "/TN", _taskName, "/SC", "ONCE", "/ST", "23:59",
            "/TR", taskCommand, "/RU", "SYSTEM", "/RL", "HIGHEST", "/F",
        }, TimeSpan.FromSeconds(30));

        if (create.ExitCode != 0)
        {
            Cleanup();
            return new TaskRunResult(create.ExitCode, create.Output, create.Output);
        }

        var run = RunProcess("schtasks.exe", new[] { "/Run", "/TN", _taskName }, TimeSpan.FromSeconds(30));
        if (run.ExitCode != 0)
        {
            Cleanup();
            return new TaskRunResult(run.ExitCode, run.Output, run.Output);
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1500);
            if (File.Exists(exitPath))
            {
                Thread.Sleep(600);
                break;
            }
        }

        var output = ReadAllSafe(stdoutPath);
        var errors = ReadAllSafe(stderrPath);
        var exitText = ReadAllSafe(exitPath).Trim();

        Cleanup();
        TryDelete(stdoutPath);
        TryDelete(stderrPath);
        TryDelete(exitPath);

        int exitCode = int.TryParse(exitText, out var ec) ? ec : (File.Exists(exitPath) ? 0 : -1);
        return new TaskRunResult(exitCode, output, errors);
    }

    private void Cleanup()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/Delete");
            psi.ArgumentList.Add("/TN");
            psi.ArgumentList.Add(_taskName);
            psi.ArgumentList.Add("/F");

            using var p = Process.Start(psi);
            p?.WaitForExit(10000);
        }
        catch
        {
            // 忽略清理失敗
        }
    }

    private static string ReadAllSafe(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>刪除檔案，忽略被其他行程鎖定的情況。</summary>
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

    private static (int ExitCode, string Output) RunProcess(string file, string[] args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (-1, "無法啟動 " + file);
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(true); } catch { }
                return (-1, stdout + stderr + "\n[逾時]");
            }
            return (p.ExitCode, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

public sealed record TaskRunResult(int ExitCode, string Output, string Errors)
{
    public bool Success => ExitCode == 0;
}

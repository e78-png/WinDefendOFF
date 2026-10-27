using System.Diagnostics;
using System.Security.Principal;

namespace WinDefendOFF.Core;

/// <summary>
/// 提權輔助。建立 SYSTEM 排程工作需要本機管理員權限；
/// 若目前程序未提升，則透過 ShellExecute runas 重新以管理員身分啟動自身。
/// </summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 若未提升，則以管理員身分重新啟動目前執行檔並傳入相同參數。
    /// 回傳 true 代表已重新啟動，呼叫端應立即結束。
    /// </summary>
    public static bool RelaunchElevated(string[] args)
    {
        if (IsElevated()) return false;

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return false;

            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            Process.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            // 使用者拒絕 UAC
            return false;
        }
    }
}

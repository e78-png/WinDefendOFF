using WinDefendOFF.Core;
using Xunit;

namespace WinDefendOFF.Tests;

/// <summary>
/// 驗證登錄檔路徑的處理。
///
/// reg.exe 需要 "HKLM\" 前綴，但 Microsoft.Win32.Registry.LocalMachine 不接受該前綴
/// （OpenSubKey("HKLM\\...") 直接失敗）。這是本專案最容易出錯的地方，
/// 一旦混用就會出現「讀取不到但腳本寫入成功」的矛盾狀況。
/// </summary>
public class RegistryPathsTests
{
    [Fact]
    public void ToSubKey_移除HKLM前綴()
    {
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services", RegistryPaths.ToSubKey(@"HKLM\SYSTEM\CurrentControlSet\Services"));
    }

    [Fact]
    public void ToSubKey_無前綴時原樣返回()
    {
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services", RegistryPaths.ToSubKey(@"SYSTEM\CurrentControlSet\Services"));
    }

    [Fact]
    public void ToSubKey_大小寫不敏感()
    {
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services", RegistryPaths.ToSubKey(@"hklm\SYSTEM\CurrentControlSet\Services"));
    }

    [Fact]
    public void ServiceStartKey_產生控制器讀取用的鍵()
    {
        var key = RegistryPaths.ServiceStartKey("WinDefend");
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services\WinDefend", key);
        Assert.DoesNotContain("HKLM", key);
    }

    [Fact]
    public void PolicyRootKey_不含HKLM前綴()
    {
        var key = RegistryPaths.PolicyRootKey();
        Assert.Equal(@"SOFTWARE\Policies\Microsoft\Windows Defender", key);
        Assert.DoesNotContain("HKLM", key);
    }

    [Fact]
    public void IfeoTargetKey_產生完整目標鍵()
    {
        Assert.Equal(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\MsMpEng.exe",
            RegistryPaths.IfeoTargetKey("MsMpEng.exe"));
    }

    [Fact]
    public void 各Live常數皆以HKLM開頭_供reg工具使用()
    {
        Assert.StartsWith(@"HKLM\", RegistryPaths.ServicesLive);
        Assert.StartsWith(@"HKLM\", RegistryPaths.PoliciesLive);
        Assert.StartsWith(@"HKLM\", RegistryPaths.IfeoLive);
    }

    [Fact]
    public void EditPlan_儲存鍵格式與控制器讀取格式一致()
    {
        var plan = ScriptBuilder.ServiceStartTypePlan(isDisable: true);

        var storageKey = plan.GetStorageKey("WinDefend", "Start");
        Assert.Equal(@"SYSTEM\CurrentControlSet\Services\WinDefend\Start", storageKey);

        // 控制器讀取時用的鍵必須與儲存鍵完全相同
        Assert.Equal(RegistryPaths.ServiceStartKey("WinDefend") + "\\Start", storageKey);
    }

    [Fact]
    public void EditPlan_根層值儲存於父鍵根目錄()
    {
        var plan = ScriptBuilder.PolicyRootPlan(isDisable: true);

        var storageKey = plan.GetStorageKey(RegistryEditPlan.RootSubKey, "DisableAntiSpyware");
        Assert.Equal(@"SOFTWARE\Policies\Microsoft\Windows Defender\DisableAntiSpyware", storageKey);
    }

    [Fact]
    public void EditPlan_RealTimeProtection子鍵儲存於子鍵路徑()
    {
        var plan = ScriptBuilder.PolicyRtpPlan(isDisable: true);

        var storageKey = plan.GetStorageKey("Real-Time Protection", "DisableRealtimeMonitoring");
        Assert.Equal(
            @"SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection\DisableRealtimeMonitoring",
            storageKey);
    }
}

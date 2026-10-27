using WinDefendOFF.Core;
using Xunit;

namespace WinDefendOFF.Tests;

/// <summary>
/// 驗證狀態模型與服務定義的判斷邏輯。
/// </summary>
public class DefenderStateTests
{
    [Fact]
    public void 服務啟動型態_Disabled被正確辨識()
    {
        var svc = new ServiceState { Name = "WinDefend", Status = "Running", StartType = "Disabled" };
        Assert.True(svc.IsDisabled);
        Assert.True(svc.IsRunning);
    }

    [Fact]
    public void 服務啟動型態_Automatic不算Disabled()
    {
        var svc = new ServiceState { Name = "WinDefend", Status = "Running", StartType = "Automatic" };
        Assert.False(svc.IsDisabled);
    }

    [Fact]
    public void 服務狀態_大小寫不敏感()
    {
        var svc = new ServiceState { Name = "WinDefend", Status = "running", StartType = "disabled" };
        Assert.True(svc.IsRunning);
        Assert.True(svc.IsDisabled);
    }

    [Fact]
    public void Find_以不區分大小寫的方式尋找服務()
    {
        var state = new DefenderState
        {
            Services = new[]
            {
                new ServiceState { Name = "WinDefend", Status = "Running", StartType = "Automatic" },
                new ServiceState { Name = "WdFilter", Status = "Running", StartType = "Boot" },
            },
        };

        Assert.NotNull(state.Find("windefend"));
        Assert.Equal("WdFilter", state.Find("WDFILTER")?.Name);
        Assert.Null(state.Find("NotExist"));
    }

    [Fact]
    public void 引擎關鍵服務_包含WinDefend與WdNisSvc()
    {
        Assert.Contains("WinDefend", DefenderServices.EngineCritical);
        Assert.Contains("WdNisSvc", DefenderServices.EngineCritical);
        // WdFilter 是核心驅動，不是引擎行程
        Assert.DoesNotContain("WdFilter", DefenderServices.EngineCritical);
    }

    [Fact]
    public void 所有受控服務_皆有預設Start值()
    {
        foreach (var svc in DefenderServices.All)
        {
            Assert.True(DefenderServices.DefaultStartValues.ContainsKey(svc),
                $"{svc} 缺少預設 Start 值");
        }
    }

    [Fact]
    public void 預設Start值_符合Windows原生設定()
    {
        // WinDefend=Automatic, WdNisSvc=Manual, WdFilter=Boot, SecurityHealthService=Manual
        Assert.Equal(2, DefenderServices.DefaultStartValues["WinDefend"]);
        Assert.Equal(3, DefenderServices.DefaultStartValues["WdNisSvc"]);
        Assert.Equal(0, DefenderServices.DefaultStartValues["WdFilter"]);
        Assert.Equal(3, DefenderServices.DefaultStartValues["SecurityHealthService"]);
    }

    [Fact]
    public void IFEO目標_涵蓋引擎與相關元件()
    {
        Assert.Contains("MsMpEng.exe", DefenderServices.IfeoTargets);
        Assert.Contains("SecurityHealthSystray.exe", DefenderServices.IfeoTargets);
    }
}

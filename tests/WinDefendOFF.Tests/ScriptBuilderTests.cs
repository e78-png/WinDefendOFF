using WinDefendOFF.Core;
using Xunit;

namespace WinDefendOFF.Tests;

/// <summary>
/// 驗證產生的 PowerShell 腳本具備正確的可觀察行為。
///
/// 這些是實際踩過的坑，每一條都曾造成功能失效：
///   - 陣列字面量被展開 → reg add 全部報 Invalid syntax
///   - 無 BOM → 中文破壞 → ParserError
///   - reg load 缺 HKLM\ 前綴 → Invalid key name
///   - reg.exe 不支援含空格的鍵路徑 → 'Real-Time Protection' 寫不進去
/// </summary>
public class ScriptBuilderTests
{
    [Fact]
    public void ServiceStartTypeScript_停用時所有服務Start設為4()
    {
        var script = ScriptBuilder.BuildServiceStartTypeScript(isDisable: true);

        foreach (var svc in DefenderServices.All)
        {
            Assert.Contains($"@('{svc}','Start','4')", script);
        }
        // 停用時需一併關閉篡改保護
        Assert.Contains("@('WinDefend','TamperProtection','0')", script);
    }

    [Fact]
    public void ServiceStartTypeScript_啟用時回復各服務預設Start值()
    {
        var script = ScriptBuilder.BuildServiceStartTypeScript(isDisable: false);

        foreach (var svc in DefenderServices.All)
        {
            var expected = DefenderServices.DefaultStartValues[svc];
            Assert.Contains($"@('{svc}','Start','{expected}')", script);
        }
        // 啟用時不應再寫 TamperProtection=0
        Assert.DoesNotContain("@('WinDefend','TamperProtection','0')", script);
    }

    [Fact]
    public void Script_陣列元素間必須有逗號否則會被展開()
    {
        var script = ScriptBuilder.BuildServiceStartTypeScript(isDisable: true);

        // 取出 $sets 區塊，確認每個內層陣列之間都有逗號
        var lines = script.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.TrimStart().StartsWith("@('"))
            .ToList();

        Assert.NotEmpty(lines);
        for (var i = 0; i < lines.Count - 1; i++)
        {
            Assert.EndsWith("),", lines[i]);
        }
        // 最後一項不能有逗號（PowerShell 陣列尾端逗號是語法錯誤）
        Assert.False(lines[^1].TrimEnd().EndsWith(","));
    }

    [Fact]
    public void IfeoScript_封鎖時寫入systray()
    {
        var script = ScriptBuilder.BuildIfeoScript(addBlock: true);

        foreach (var target in DefenderServices.IfeoTargets)
        {
            Assert.Contains($"@('{target}','Debugger','systray.exe','SZ')", script);
        }
    }

    [Fact]
    public void IfeoScript_解除時刪除Debugger值()
    {
        var script = ScriptBuilder.BuildIfeoScript(addBlock: false);

        foreach (var target in DefenderServices.IfeoTargets)
        {
            Assert.Contains($"@('{target}','Debugger')", script);
        }
    }

    [Fact]
    public void IfeoScript_使用Hive手法而非直接寫入()
    {
        // MsMpEng.exe 的 IFEO 值受保護，直接 Remove-ItemProperty 會回報成功但值未刪除。
        // 整樹還原（restore）可繞過逐值保護，因此必須走 hive 手法。
        var script = ScriptBuilder.BuildIfeoScript(addBlock: false);

        Assert.Contains("Edit-Subtree", script);
        Assert.Contains("reg.exe", script);
    }

    [Fact]
    public void EditSubtree_使用HKLM前綴載入hive()
    {
        var script = ScriptBuilder.BuildEditSubtreeFunction();
        // reg load 必須帶 HKLM\ 前綴，否則回報 Invalid key name
        Assert.Contains("$tempKey = 'HKLM\\' + $unique", script);
        Assert.Contains("Reg @('load', $tempKey, $hive)", script);
    }

    [Fact]
    public void EditSubtree_支援根層子鍵標記()
    {
        var script = ScriptBuilder.BuildEditSubtreeFunction();
        Assert.Contains("'<ROOT>'", script);
        Assert.Contains("$tempKey } else { $tempKey + '\\' + $s[0] }", script);
    }

    [Fact]
    public void EditSubtree_使用唯一檔名避免殘留工作鎖定()
    {
        var script = ScriptBuilder.BuildEditSubtreeFunction();
        // 共用檔名會讓 File.Delete 拋 IOException
        Assert.Contains("[Guid]::NewGuid().ToString('N')", script);
    }

    [Fact]
    public void StopEngineScript_不載入任何第三方核心驅動()
    {
        var script = ScriptBuilder.BuildStopEngineScript();

        // 本專案不使用第三方核心驅動，只用 Windows 官方機制
        Assert.DoesNotContain("sc.exe create", script);
        Assert.DoesNotContain("CreateFileW", script);
        Assert.DoesNotContain("DeviceIoControl", script);
    }

    [Fact]
    public void StopEngineScript_嘗試結束引擎與停止服務()
    {
        var script = ScriptBuilder.BuildStopEngineScript();

        Assert.Contains("Stop-Process", script);
        Assert.Contains("MsMpEng", script);
        foreach (var svc in DefenderServices.All)
            Assert.Contains("sc.exe stop " + svc, script);
    }

    [Fact]
    public void StopEngineScript_輸出可判定的引擎狀態()
    {
        var script = ScriptBuilder.BuildStopEngineScript();

        // 控制器依此判定步驟成功與否
        Assert.Contains("ENGINE_STOPPED", script);
        Assert.Contains("ENGINE_STILL_RUNNING", script);
        Assert.Contains("STOP_BLOCKED", script);
    }

    [Fact]
    public void StopEngineScript_輸出防護狀態供驗證()
    {
        var script = ScriptBuilder.BuildStopEngineScript();

        Assert.Contains("AntispywareEnabled=", script);
        Assert.Contains("RealTimeProtectionEnabled=", script);
    }

    [Fact]
    public void RealtimeProtectionScript_方向正確()
    {
        var disable = ScriptBuilder.BuildRealtimeProtectionScript(enable: false);
        Assert.Contains("DisableRealtimeMonitoring $true", disable);
        Assert.Contains("RTP_DISABLED", disable);

        var enable = ScriptBuilder.BuildRealtimeProtectionScript(enable: true);
        Assert.Contains("DisableRealtimeMonitoring $false", enable);
        Assert.Contains("RTP_ENABLED", enable);
    }

    [Fact]
    public void StartServicesScript_先啟動WinDefend再啟動其他服務()
    {
        var script = ScriptBuilder.BuildStartServicesScript();

        var winDefendIdx = script.IndexOf("sc.exe start WinDefend", StringComparison.Ordinal);
        var nisIdx = script.IndexOf("sc.exe start WdNisSvc", StringComparison.Ordinal);

        Assert.True(winDefendIdx >= 0);
        Assert.True(nisIdx > winDefendIdx);
    }

    [Fact]
    public void 依賴regExe的腳本_對含空格的鍵路徑加上引號()
    {
        // reg.exe 需要引號包住含空格的鍵路徑，否則 'Real-Time Protection'
        // 會被拆成兩個參數而寫入錯誤的子鍵（實測產生過名為 'D'、'R' 的垃圾子鍵）。
        var script = ScriptBuilder.BuildServiceStartTypeScript(isDisable: true);

        Assert.Contains("if ($s.Contains(' ')", script);
        Assert.Contains("$s = '\"' + $s + '\"'", script);
    }
}

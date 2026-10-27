using WinDefendOFF.Core;
using Xunit;

namespace WinDefendOFF.Tests;

/// <summary>測試替身：以可控的方式模擬 SYSTEM 執行器與登錄檔。</summary>
internal sealed class FakeExecutor : ISystemExecutor
{
    public List<string> ExecutedScripts { get; } = new();
    public Dictionary<string, string> Responses { get; } = new();
    public int ExitCode { get; set; }

    /// <summary>套用編輯計畫的目標登錄檔（模擬腳本實際造成的變更）。</summary>
    public FakeRegistry? TargetRegistry { get; set; }

    /// <summary>指定哪些腳本執行時「套用計畫」；null 代表全部套用。</summary>
    public HashSet<string>? ApplyPlanFor { get; set; }

    /// <summary>模擬腳本執行失敗（不套用計畫，且回報失敗訊息）。</summary>
    public HashSet<string> FailScripts { get; } = new();

    public TaskRunResult RunScript(
        string scriptPath,
        TimeSpan timeout,
        IReadOnlyList<RegistryEditPlan>? plans = null)
    {
        var name = Path.GetFileName(scriptPath);
        ExecutedScripts.Add(name);

        var content = File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : string.Empty;

        // 腳本失敗 → 不套用編輯計畫（登錄檔維持原狀）
        if (FailScripts.Contains(name))
        {
            return new TaskRunResult(1, "SAVE_FAIL: " + name + "\nRESTORE_FAIL\n", string.Empty);
        }

        var shouldApply = plans is not null
            && TargetRegistry is not null
            && (ApplyPlanFor is null || ApplyPlanFor.Contains(name));

        // 模擬 reg save/load/restore 對登錄檔造成的實際變更
        if (shouldApply && TargetRegistry is not null)
        {
            foreach (var plan in plans!)
            {
                foreach (var e in plan.Sets)
                {
                    TargetRegistry.Dwords[plan.GetStorageKey(e.SubKey, e.ValueName)] = e.DwordValue;
                }
                foreach (var d in plan.Deletes)
                {
                    TargetRegistry.Dwords.Remove(plan.GetStorageKey(d.SubKey, d.ValueName));
                }
            }
        }

        var response = Responses.TryGetValue(name, out var r) ? r : DefaultResponseFor(name, content);
        return new TaskRunResult(ExitCode, response, string.Empty);
    }

    private static string DefaultResponseFor(string name, string content)
    {
        // 依腳本內容推斷合理的回應，讓流程能走完
        if (content.Contains("ENGINE_STOPPED")) return "ENGINE_STOPPED\n";
        if (content.Contains("sc.exe start WinDefend")) return "SC_START WinDefend: ok\n";
        if (content.Contains("DisableRealtimeMonitoring $false")) return "RTP_ENABLED\n";
        if (content.Contains("DisableRealtimeMonitoring")) return "RTP_DISABLED\n";
        return "EDIT_OK\n";
    }
}

internal sealed class FakeRegistry : IRegistryEditor
{
    public Dictionary<string, int?> Dwords { get; } = new();
    public Dictionary<string, string?> Strings { get; } = new();

    public int? ReadDword(string subKey, string valueName) =>
        Dwords.TryGetValue($"{subKey}\\{valueName}", out var v) ? v : null;

    public string? ReadString(string subKey, string valueName) =>
        Strings.TryGetValue($"{subKey}\\{valueName}", out var v) ? v : null;

    /// <summary>模擬寫入被拒（WRP 保護或權限不足）。</summary>
    public bool FailWrites { get; set; }

    public bool WriteDword(string subKey, string valueName, int value)
    {
        if (FailWrites) return false;
        Dwords[$"{subKey}\\{valueName}"] = value;
        return true;
    }

    public bool DeleteValue(string subKey, string valueName)
    {
        Dwords.Remove($"{subKey}\\{valueName}");
        Strings.Remove($"{subKey}\\{valueName}");
        return true;
    }

    public bool WriteString(string subKey, string valueName, string value)
    {
        if (FailWrites) return false;
        Strings[$"{subKey}\\{valueName}"] = value;
        return true;
    }

    public HiveEditResult EditSubtree(
        string liveKeyPath,
        IReadOnlyList<HiveEditor.ValueEdit> edits,
        IReadOnlyList<HiveEditor.ValueDelete> deletes,
        Action<string>? log = null)
    {
        // 鍵格式需與控制器讀取時一致（使用 RegistryPaths.ToSubKey 正規化）
        foreach (var e in edits)
        {
            var key = e.SubKey == "@ROOT"
                ? RegistryPaths.ToSubKey(liveKeyPath)
                : $"{RegistryPaths.ToSubKey(liveKeyPath)}\\{e.SubKey}";
            Dwords[$"{key}\\{e.ValueName}"] = e.DwordValue;
        }
        foreach (var d in deletes)
        {
            var key = d.SubKey == "@ROOT"
                ? RegistryPaths.ToSubKey(liveKeyPath)
                : $"{RegistryPaths.ToSubKey(liveKeyPath)}\\{d.SubKey}";
            Dwords.Remove($"{key}\\{d.ValueName}");
        }
        return new HiveEditResult(true, new[] { "EDIT_OK" }, null);
    }
}

internal sealed class FakeStateSource : IDefenderStateSource
{
    public DefenderState State { get; set; } = new();
    public DefenderState Read() => State;
}

/// <summary>
/// 驗證停用／啟用流程的可觀察行為：
///   - 步驟執行順序
///   - 各步驟的成功判定
///   - 最終驗證邏輯
///   - 失敗時的降級行為
/// </summary>
public class DefenderControllerTests : IDisposable
{
    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "wdotest_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_workDir, true); } catch { /* 忽略 */ }
    }

    private (DefenderController Controller, FakeExecutor Executor, FakeRegistry Registry, FakeStateSource State)
        CreateController(DefenderState initial)
    {
        Directory.CreateDirectory(_workDir);
        var executor = new FakeExecutor();
        var registry = new FakeRegistry();
        executor.TargetRegistry = registry;
        var state = new FakeStateSource { State = initial };
        var controller = new DefenderController(
            _workDir, _ => { }, executor, registry, state);
        return (controller, executor, registry, state);
    }

    private static DefenderState RunningState() => new()
    {
        EngineRunning = true,
        IsDisabled = false,
        IfeoBlockPresent = false,
        RealtimeProtectionEnabled = true,
    };

    [Fact]
    public void 停用流程_依序執行五個步驟()
    {
        var (controller, executor, registry, state) = CreateController(RunningState());

        // 模擬腳本執行後的狀態變化
        state.State = new DefenderState
        {
            EngineRunning = false,
            IsDisabled = true,
            IfeoBlockPresent = true,
            RealtimeProtectionEnabled = false,
        };

        var result = controller.Disable(RunningState());

        // 4 個腳本步驟（群組原則改為直接寫入，不需腳本）+ 1 個群組原則步驟 + 1 個驗證步驟
        Assert.Equal(6, result.Steps.Count);
        Assert.Equal(
            new[] { "rtp-off.ps1", "ifeo_add.ps1", "svcs.ps1", "stop-engine.ps1" },
            executor.ExecutedScripts);
    }

    [Fact]
    public void 啟用流程_依序執行五個步驟()
    {
        var (controller, executor, registry, state) = CreateController(new DefenderState
        {
            EngineRunning = false,
            IsDisabled = true,
            IfeoBlockPresent = true,
        });

        state.State = new DefenderState
        {
            EngineRunning = true,
            IsDisabled = false,
            IfeoBlockPresent = false,
            RealtimeProtectionEnabled = true,
        };

        var result = controller.Enable(state.State);

        // 4 個腳本步驟（群組原則改為直接寫入，不需腳本）+ 1 個群組原則步驟 + 1 個驗證步驟
        Assert.Equal(6, result.Steps.Count);
        Assert.Equal(
            new[] { "rtp-on.ps1", "ifeo_del.ps1", "svcs.ps1", "start.ps1" },
            executor.ExecutedScripts);
    }

    [Fact]
    public void 停用成功_所有步驟皆成功且結果為成功()
    {
        var (controller, executor, registry, state) = CreateController(RunningState());

        state.State = new DefenderState
        {
            EngineRunning = false,
            IsDisabled = true,
            IfeoBlockPresent = true,
        };

        var result = controller.Disable(RunningState());

        Assert.True(result.Success);
        Assert.All(result.Steps, s => Assert.True(s.Success, $"{s.Title} 應成功：{s.Error}"));
    }

    [Fact]
    public void 停用時引擎因PPL殘留但IFEO已封鎖且掃描已關閉_仍視為成功()
    {
        var (controller, executor, registry, state) = CreateController(RunningState());

        // 引擎因 PPL 保護無法結束，但 IFEO 已封鎖且即時掃描已關閉 → 防護確實已失效
        state.State = new DefenderState
        {
            EngineRunning = true,
            IsDisabled = false,
            IfeoBlockPresent = true,
            RealtimeProtectionEnabled = false,
        };

        var result = controller.Disable(RunningState());

        Assert.True(result.Success);
        var verify = result.Steps.Last();
        Assert.Equal("驗證最終狀態", verify.Title);
        Assert.True(verify.Success);
    }

    [Fact]
    public void 停用時引擎殘留且掃描仍開啟_則驗證步驟失敗()
    {
        var (controller, executor, registry, state) = CreateController(RunningState());

        // 模擬群組原則寫入失敗 → RTP 政策值不會被設為 1
        registry.FailWrites = true;

        // 引擎殘留、IFEO 已封鎖，但即時掃描仍是開啟的 → 尚未完全停用
        state.State = new DefenderState
        {
            EngineRunning = true,
            IsDisabled = false,
            IfeoBlockPresent = true,
            RealtimeProtectionEnabled = true,
        };

        var result = controller.Disable(RunningState());

        Assert.False(result.Success);
        var verify = result.Steps.Last();
        Assert.Equal("驗證最終狀態", verify.Title);
        Assert.False(verify.Success);
    }

    [Fact]
    public void 停用時IFEO未建立_則驗證步驟失敗()
    {
        var (controller, executor, registry, state) = CreateController(RunningState());

        state.State = new DefenderState
        {
            EngineRunning = false,
            IsDisabled = true,
            IfeoBlockPresent = false, // IFEO 沒寫成功
        };

        var result = controller.Disable(RunningState());

        Assert.False(result.Success);
    }

    [Fact]
    public void 啟用成功_所有步驟皆成功()
    {
        var (controller, executor, registry, state) = CreateController(new DefenderState
        {
            EngineRunning = false,
            IsDisabled = true,
            IfeoBlockPresent = true,
        });

        state.State = new DefenderState
        {
            EngineRunning = true,
            IsDisabled = false,
            IfeoBlockPresent = false,
        };

        var result = controller.Enable(state.State);

        Assert.True(result.Success);
        Assert.All(result.Steps, s => Assert.True(s.Success, $"{s.Title} 應成功：{s.Error}"));
    }

    [Fact]
    public void 服務Start值設定失敗_該步驟失敗()
    {
        var (controller, executor, registry, state) = CreateController(RunningState());

        // 腳本執行失敗 → 登錄檔未被修改 → Start 值不符
        executor.FailScripts.Add("svcs.ps1");
        state.State = new DefenderState
        {
            EngineRunning = false,
            IsDisabled = true,
            IfeoBlockPresent = true,
        };

        var result = controller.Disable(RunningState());

        var step = result.Steps.First(s => s.Title.Contains("開機啟動型態"));
        Assert.False(step.Success);
        Assert.Contains("Start 值未正確設定", step.Error);
    }

    [Fact]
    public void 群組原則設定失敗_該步驟失敗()
    {
        var (controller, executor, registry, state) = CreateController(RunningState());

        // 模擬 .NET API 寫入被拒（WRP 保護或權限不足）
        registry.FailWrites = true;

        state.State = new DefenderState
        {
            EngineRunning = false,
            IsDisabled = true,
            IfeoBlockPresent = true,
            RealtimeProtectionEnabled = false,
        };

        var result = controller.Disable(RunningState());

        var step = result.Steps.First(s => s.Title.Contains("群組原則"));
        Assert.False(step.Success);
    }

    [Fact]
    public void 腳本以UTF8BOM寫入_確保PowerShell正確解碼中文()
    {
        var (controller, executor, registry, state) = CreateController(RunningState());
        state.State = new DefenderState { EngineRunning = false, IsDisabled = true, IfeoBlockPresent = true };

        controller.Disable(RunningState());

        var path = Path.Combine(_workDir, "svcs.ps1");
        var bytes = File.ReadAllBytes(path);

        // UTF-8 BOM = EF BB BF
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }

    [Fact]
    public void 腳本內容在磁碟上可被讀回且與產生內容一致()
    {
        var (controller, executor, registry, state) = CreateController(RunningState());
        state.State = new DefenderState { EngineRunning = false, IsDisabled = true, IfeoBlockPresent = true };

        controller.Disable(RunningState());

        var expected = ScriptBuilder.BuildServiceStartTypeScript(isDisable: true);
        var actual = File.ReadAllText(Path.Combine(_workDir, "svcs.ps1"));
        Assert.Equal(expected, actual);
    }
}

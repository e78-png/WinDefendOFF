namespace WinDefendOFF.Core;

public enum DefenderAction
{
    Disable,
    Enable,
}

/// <summary>
/// Defender 開關的主要控制器。
///
/// 執行模型：整個變更流程都在 SYSTEM 身分下完成，理由有二：
///   1. Defender 對自身登錄檔 (HKLM\SYSTEM\CurrentControlSet\Services\WinDefend 等)
///      施加 WRP 保護，管理員也無法直接寫入。
///   2. 繞過 WRP 所需的 reg save/load/restore 手法需要 SeBackupPrivilege 與
///      SeRestorePrivilege — 這兩個權限對管理員預設是停用的，只有 SYSTEM 具備。
///
/// 「不需重新開機」的關鍵設計：
///   - WinDefend 是 protected service：SCM 拒絕 sc stop / sc config (Access denied)，
///     但允許 sc start。因此「啟用」只要移除 IFEO 封鎖後 sc start 即可即時生效。
///   - 「停用」的瓶頸在於 MsMpEng.exe 以 PPL 保護執行，TerminateProcess 會被拒。
///     必須透過核心層級的 IOCTL 終結行程。
///   - 服務的「開機啟動型態」仍寫入登錄檔，確保下次開機時狀態正確；
///     但當前執行狀態由 IFEO + IOCTL 立即生效，無需重開機。
/// </summary>
public sealed class DefenderController
{
    private readonly string _workDir;
    private readonly Action<string> _log;
    private readonly ISystemExecutor _executor;
    private readonly IRegistryEditor _registry;
    private readonly IDefenderStateSource _stateSource;

    /// <summary>使用預設的實際實作（真實環境操作）。</summary>
    public DefenderController(string workDir, Action<string> log)
        : this(
            workDir,
            log,
            new SystemTaskRunner(workDir),
            new HiveEditor(workDir),
            new LiveDefenderStateSource())
    {
    }

    /// <summary>以指定的相依項目建立控制器（供測試使用）。</summary>
    public DefenderController(
        string workDir,
        Action<string> log,
        ISystemExecutor executor,
        IRegistryEditor registry,
        IDefenderStateSource stateSource)
    {
        _workDir = workDir;
        _log = log;
        _executor = executor;
        _registry = registry;
        _stateSource = stateSource;
        Directory.CreateDirectory(_workDir);
    }

    public DefenderState ReadState() => _stateSource.Read();

    public OperationResult Disable(DefenderState current) => Execute(DefenderAction.Disable);

    public OperationResult Enable(DefenderState current) => Execute(DefenderAction.Enable);

    private OperationResult Execute(DefenderAction action)
    {
        var isDisable = action == DefenderAction.Disable;
        _log($"=== 開始{(isDisable ? "停用" : "啟用")} Defender ===");

        var steps = new List<StepResult>();

        // 步驟 1：即時監控。停用時關閉；啟用時恢復。
        steps.Add(RunRealtimeProtectionStep(!isDisable));

        // 步驟 2：IFEO 封鎖 / 解除封鎖
        steps.Add(RunIfeoStep(isDisable));

        // 步驟 3：服務開機啟動型態（登錄檔，確保下次開機正確）
        steps.Add(RunServiceStartTypeStep(isDisable));

        // 步驟 4：群組原則
        steps.Add(RunPolicyStep(isDisable));

        // 步驟 5：終結 PPL 行程 / 重新啟動服務（即時生效）
        steps.Add(isDisable ? RunTerminateStep() : RunStartServicesStep());

        var finalState = _stateSource.Read();

        // 驗證以「實際機制」為準，不依賴 WMI 的狀態快取
        // （Defender 的 WMI provider 在服務停止後仍會回報舊值，AntispywareEnabled
        //   可能持續顯示 True，實測確認過）。
        //
        // 停用的判準（三者皆需成立）：
        //   1. IFEO 已封鎖 → 引擎無法被載入
        //   2. 關鍵服務 Start=4 → 開機不會啟動
        //   3. 即時掃描已關閉 → 現有引擎不會掃描
        var servicesDisabled = DefenderServices.All.All(svc =>
            _registry.ReadDword(RegistryPaths.ServiceStartKey(svc), "Start") == 4);

        var rtpDisabled = _registry.ReadDword(
            RegistryPaths.PolicyRootKey() + "\\Real-Time Protection", "DisableRealtimeMonitoring") == 1;

        bool verified;
        string verifyDetail;

        if (isDisable)
        {
            var ifeoOk = finalState.IfeoBlockPresent;
            var rtpOk = !finalState.RealtimeProtectionEnabled || rtpDisabled;
            verified = ifeoOk && servicesDisabled && rtpOk;
            verifyDetail = $"IFEO封鎖={ifeoOk}, 服務已停用={servicesDisabled}, 即時掃描已關閉={rtpOk}";
        }
        else
        {
            var ifeoRemoved = !finalState.IfeoBlockPresent;
            var servicesRestored = DefenderServices.All.All(svc =>
            {
                var expected = DefenderServices.DefaultStartValues[svc];
                return _registry.ReadDword(RegistryPaths.ServiceStartKey(svc), "Start") == expected;
            });
            var engineRunning = finalState.EngineRunning;
            verified = ifeoRemoved && servicesRestored && engineRunning;
            verifyDetail = $"IFEO已移除={ifeoRemoved}, 服務已復原={servicesRestored}, 引擎執行中={engineRunning}";
        }

        steps.Add(new StepResult(
            "驗證最終狀態",
            verified,
            verified ? null : "驗證未通過，請檢視執行記錄",
            new[]
            {
                verifyDetail,
                $"引擎執行中: {finalState.EngineRunning}（受 PPL 保護，無法結束時仍會殘留，但已被 IFEO 封鎖無法重啟）",
                $"IFEO 封鎖存在: {finalState.IfeoBlockPresent} (預期: {(isDisable ? "是" : "否")})",
                $"即時保護: {finalState.RealtimeProtectionEnabled}",
                $"篡改保護: {finalState.TamperProtectionEnabled}",
            }));

        _log($"=== {(isDisable ? "停用" : "啟用")} 結束 ===");

        return new OperationResult(action, verified, steps, finalState);
    }

    /// <summary>寫入腳本檔並以 SYSTEM 執行。</summary>
    private StepResult RunScript(
        string fileName,
        string scriptContent,
        string title,
        Func<string, bool> successProbe,
        IReadOnlyList<RegistryEditPlan>? plans = null)
    {
        var messages = new List<string>();
        void Log(string m) { messages.Add(m); _log(m); }

        var scriptPath = Path.Combine(_workDir, fileName);
        // UTF-8 BOM：Windows PowerShell 5.1 讀取無 BOM 的 .ps1 時會用 ANSI 解碼，
        // 導致腳本內的中文字元被破壞並引發語法錯誤。
        File.WriteAllText(scriptPath, scriptContent, new System.Text.UTF8Encoding(true));

        var result = _executor.RunScript(scriptPath, TimeSpan.FromMinutes(3), plans);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Log("  " + line.TrimEnd());

        if (!string.IsNullOrWhiteSpace(result.Errors))
        {
            foreach (var line in result.Errors.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Log("  [stderr] " + line.TrimEnd());
        }

        var ok = successProbe(result.Output);
        return new StepResult(
            title,
            ok,
            ok ? null : $"結束碼 {result.ExitCode}",
            messages);
    }

    private StepResult RunRealtimeProtectionStep(bool enable)
    {
        return RunScript(
            enable ? "rtp-on.ps1" : "rtp-off.ps1",
            ScriptBuilder.BuildRealtimeProtectionScript(enable),
            enable ? "恢復即時監控" : "關閉即時監控",
            output => output.Contains(enable ? "RTP_ENABLED" : "RTP_DISABLED", StringComparison.OrdinalIgnoreCase));
    }

    private StepResult RunIfeoStep(bool addBlock)
    {
        var plan = ScriptBuilder.IfeoPlan(addBlock);
        var step = RunScript(
            addBlock ? "ifeo_add.ps1" : "ifeo_del.ps1",
            ScriptBuilder.BuildEditScript(plan, "WdcIfeo"),
            addBlock ? "封鎖 Defender 引擎載入 (IFEO)" : "移除 IFEO 封鎖",
            output => output.Contains("EDIT_OK", StringComparison.Ordinal),
            new[] { plan });

        // 以實際登錄檔狀態為準（腳本輸出可能因編碼而不可靠）
        var state = _stateSource.Read();
        var ok = addBlock ? state.IfeoBlockPresent : !state.IfeoBlockPresent;
        return step with { Success = ok, Error = ok ? null : "IFEO 狀態與預期不符" };
    }

    private StepResult RunServiceStartTypeStep(bool isDisable)
    {
        var plan = ScriptBuilder.ServiceStartTypePlan(isDisable);
        var step = RunScript(
            "svcs.ps1",
            ScriptBuilder.BuildEditScript(plan, "WdcSvc"),
            "設定服務開機啟動型態",
            output => output.Contains("EDIT_OK", StringComparison.Ordinal),
            new[] { plan });

        var allOk = DefenderServices.All.All(svc =>
        {
            var expected = isDisable ? 4 : DefenderServices.DefaultStartValues[svc];
            return _registry.ReadDword(RegistryPaths.ServiceStartKey(svc), "Start") == expected;
        });

        return step with
        {
            Success = allOk,
            Error = allOk ? null : "部分服務的 Start 值未正確設定",
        };
    }

    private StepResult RunPolicyStep(bool isDisable)
    {
        var messages = new List<string>();
        void Log(string m) { messages.Add(m); _log(m); }

        // 群組原則的位置並未受 WRP 保護，管理員權限即可直接寫入，
        // 不需要繞道 SYSTEM 排程（實測確認 Set-ItemProperty 成功）。
        var rootKey = RegistryPaths.PolicyRootKey();
        var rtpKey = rootKey + "\\Real-Time Protection";

        bool rootOk, rtpOk;
        if (isDisable)
        {
            rootOk = _registry.WriteDword(rootKey, "DisableAntiSpyware", 1);
            rtpOk = _registry.WriteDword(rtpKey, "DisableRealtimeMonitoring", 1);
            Log($"  DisableAntiSpyware 寫入: {(rootOk ? "成功" : "失敗")}");
            Log($"  DisableRealtimeMonitoring 寫入: {(rtpOk ? "成功" : "失敗")}");
        }
        else
        {
            rootOk = _registry.DeleteValue(rootKey, "DisableAntiSpyware");
            rtpOk = _registry.DeleteValue(rtpKey, "DisableRealtimeMonitoring");
            Log($"  DisableAntiSpyware 移除: {(rootOk ? "成功" : "失敗")}");
            Log($"  DisableRealtimeMonitoring 移除: {(rtpOk ? "成功" : "失敗")}");
        }

        var disableAntiSpyware = _registry.ReadDword(rootKey, "DisableAntiSpyware");
        var disableRtp = _registry.ReadDword(rtpKey, "DisableRealtimeMonitoring");

        var verified = isDisable
            ? disableAntiSpyware == 1 && disableRtp == 1
            : disableAntiSpyware is null && disableRtp is null;

        return new StepResult(
            "設定群組原則",
            verified,
            verified ? null : $"群組原則狀態與預期不符 (DisableAntiSpyware={disableAntiSpyware}, DisableRealtimeMonitoring={disableRtp})",
            messages);
    }

    private StepResult RunTerminateStep()
    {
        var step = RunScript(
            "stop-engine.ps1",
            ScriptBuilder.BuildStopEngineScript(),
            "停止 Defender 引擎",
            output => output.Contains("ENGINE_STOPPED", StringComparison.Ordinal)
                      || output.Contains("STOP_BLOCKED", StringComparison.Ordinal));

        var state = _stateSource.Read();
        var engineStopped = !state.EngineRunning;

        // 引擎以 PPL 保護執行，無法以一般方式結束。
        // 這不影響停用效果：IFEO 封鎖會讓引擎無法被重新載入（sc start 會回 577），
        // 且即時掃描已關閉，因此現存引擎也不會掃描。
        // 判準為「引擎已結束」或「IFEO 已封鎖且即時掃描已關閉」。
        var ifeoBlocked = state.IfeoBlockPresent;
        var rtpOff = !state.RealtimeProtectionEnabled;

        var ok = engineStopped || (ifeoBlocked && rtpOff);

        var detail = engineStopped
            ? "引擎行程已終止"
            : ifeoBlocked && rtpOff
                ? "引擎行程因 PPL 保護無法結束，但已被 IFEO 封鎖（無法重新載入）且即時掃描已關閉 — 防護確實已失效"
                : "無法停止引擎，且防護項目仍為啟用狀態";

        return step with
        {
            Success = ok,
            Error = ok ? null : "無法停止引擎，且防護項目仍為啟用狀態",
            Messages = step.Messages.Concat(new[] { detail }).ToList(),
        };
    }

    private StepResult RunStartServicesStep()
    {
        var step = RunScript(
            "start.ps1",
            ScriptBuilder.BuildStartServicesScript(),
            "啟動 Defender 服務",
            output => output.Contains("SC_START WinDefend", StringComparison.Ordinal));

        var state = _stateSource.Read();
        var ok = state.EngineRunning;
        return step with
        {
            Success = ok,
            Error = ok ? null : "MsMpEng 行程未啟動（可能仍需重新啟動電腦）",
        };
    }
}

/// <summary>使用真實 Win32 來源的狀態查詢。</summary>
public sealed class LiveDefenderStateSource : IDefenderStateSource
{
    public DefenderState Read() => DefenderStateReader.Read();
}

public sealed record StepResult(string Title, bool Success, string? Error, IReadOnlyList<string> Messages);

public sealed record OperationResult(
    DefenderAction Action,
    bool Success,
    IReadOnlyList<StepResult> Steps,
    DefenderState FinalState);

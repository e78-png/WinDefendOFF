using System.Text;

namespace WinDefendOFF.Core;

/// <summary>
/// 產生在 SYSTEM 身分下執行的 PowerShell 腳本。
///
/// 抽成獨立類別的理由：腳本內容是整個工具最關鍵、也最容易出錯的部分
/// （PowerShell 陣列展開、UTF-8 BOM、reg load 前綴、參數引號…）。
/// 讓它成為純函式後，就能以單元測試直接驗證字串內容，不必真的提權執行。
/// </summary>
public static class ScriptBuilder
{
    /// <summary>產生 reg save/load/restore 編輯子樹的輔助函式定義。</summary>
    public static string BuildEditSubtreeFunction()
    {
        var sb = new StringBuilder();
        sb.AppendLine("function Edit-Subtree {");
        sb.AppendLine("  param([string]$LiveKey, [array]$Sets, [array]$Deletes, [string]$TempName)");
        sb.AppendLine("  $unique = $TempName + '_' + [Guid]::NewGuid().ToString('N').Substring(0,8)");
        sb.AppendLine("  $hive = Join-Path $env:TEMP ($unique + '.hiv')");
        sb.AppendLine("  $tempKey = 'HKLM\\' + $unique");
        sb.AppendLine("  [void](RegQuiet @('unload', $tempKey))");
        sb.AppendLine("  Remove-Item $hive, ($hive + '.LOG1'), ($hive + '.LOG2') -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  if ((Reg @('save', $LiveKey, $hive, '/y')) -ne 0) { Log ('SAVE_FAIL: ' + $LiveKey); return $false }");
        sb.AppendLine("  if ((Reg @('load', $tempKey, $hive)) -ne 0) { Log ('LOAD_FAIL: ' + $LiveKey); return $false }");
        sb.AppendLine("  foreach ($s in $Sets) {");
        sb.AppendLine("    $type = 'DWORD'");
        sb.AppendLine("    if ($s.Count -ge 4) { $type = [string]$s[3] }");
        sb.AppendLine("    $sub = if ([string]$s[0] -eq '<ROOT>') { $tempKey } else { $tempKey + '\\' + $s[0] }");
        sb.AppendLine("    if ($type -eq 'SZ') { [void](Reg @('add', $sub, '/v', $s[1], '/t', 'REG_SZ', '/d', ([string]$s[2]), '/f')) }");
        sb.AppendLine("    else { [void](Reg @('add', $sub, '/v', $s[1], '/t', 'REG_DWORD', '/d', ([string]$s[2]), '/f')) }");
        sb.AppendLine("  }");
        sb.AppendLine("  foreach ($d in $Deletes) {");
        sb.AppendLine("    $dsub = if ([string]$d[0] -eq '<ROOT>') { $tempKey } else { $tempKey + '\\' + $d[0] }");
        sb.AppendLine("    [void](Reg @('delete', $dsub, '/v', $d[1], '/f'))");
        sb.AppendLine("  }");
        sb.AppendLine("  [void](RegQuiet @('unload', $tempKey))");
        sb.AppendLine("  $rc = Reg @('restore', $LiveKey, $hive, '/f')");
        sb.AppendLine("  Remove-Item $hive, ($hive + '.LOG1'), ($hive + '.LOG2') -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("  if ($rc -ne 0) { Log ('RESTORE_FAIL: ' + $LiveKey); return $false }");
        sb.AppendLine("  Log ('EDIT_OK: ' + $LiveKey)");
        sb.AppendLine("  return $true");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>產生記錄緩衝與 reg 包裝函式的共用前導碼。</summary>
    public static string BuildPreamble()
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        // 記錄緩衝：所有訊息先進入緩衝，腳本最後統一輸出。
        // 如此可避免函式回傳值混入訊息，破壞結束碼比較。
        sb.AppendLine("$script:Log = New-Object System.Collections.ArrayList");
        sb.AppendLine("function Log($m) { [void]$script:Log.Add([string]$m) }");
        sb.AppendLine("function Reg($a) {");
        sb.AppendLine("  $args = @()");
        sb.AppendLine("  foreach ($x in $a) {");
        sb.AppendLine("    # reg.exe 需要引號包住含空格的鍵路徑（如 'Real-Time Protection'），");
        sb.AppendLine("    # 否則會被拆成多個參數而寫入錯誤的子鍵。");
        sb.AppendLine("    $s = [string]$x");
        sb.AppendLine("    if ($s.Contains(' ') -and -not $s.StartsWith('\"')) { $s = '\"' + $s + '\"' }");
        sb.AppendLine("    $args += $s");
        sb.AppendLine("  }");
        sb.AppendLine("  $o = & reg.exe @args 2>&1");
        sb.AppendLine("  foreach ($l in $o) { $t = [string]$l; if ($t.Trim() -ne '') { Log $t } }");
        sb.AppendLine("  return [int]$LASTEXITCODE");
        sb.AppendLine("}");
        // unload 一個未載入的 hive 會回報 "The parameter is incorrect"，屬預期情況，不記錄。
        sb.AppendLine("function RegQuiet($a) { $null = & reg.exe @a 2>&1; return [int]$LASTEXITCODE }");
        // RegQuiet 只用於 unload（鍵名無空格），不需引號處理。
        return sb.ToString();
    }

    /// <summary>產生腳本結尾（輸出記錄緩衝）。</summary>
    public static string BuildEpilogue() =>
        "foreach ($m in $script:Log) { Write-Output $m }" + Environment.NewLine;

    /// <summary>
    /// 產生設定服務 Start 值的腳本。
    /// </summary>
    public static string BuildServiceStartTypeScript(bool isDisable) =>
        BuildEditScript(ServiceStartTypePlan(isDisable), "WdcSvc");

    /// <summary>建立服務 Start 值的編輯計畫。</summary>
    public static RegistryEditPlan ServiceStartTypePlan(bool isDisable)
    {
        var sets = new List<HiveEditor.ValueEdit>();
        foreach (var svc in DefenderServices.All)
        {
            var start = isDisable ? 4 : DefenderServices.DefaultStartValues[svc];
            sets.Add(new HiveEditor.ValueEdit(svc, "Start", start, null));
        }
        if (isDisable)
        {
            sets.Add(new HiveEditor.ValueEdit("WinDefend", "TamperProtection", 0, null));
            sets.Add(new HiveEditor.ValueEdit("WinDefend", "EnableTamperProtection", 0, null));
        }
        return new RegistryEditPlan(RegistryPaths.ServicesLive, sets, Array.Empty<HiveEditor.ValueDelete>());
    }

    /// <summary>
    /// 產生設定群組原則的腳本。
    ///
    /// 此處刻意不使用 reg.exe：
    ///   - reg.exe 不支援含空格的鍵路徑（'Real-Time Protection' 會直接報錯）
    ///   - reg.exe 對 WRP 保護的位置無法寫入
    /// 而改用 .NET 的 Microsoft.Win32.Registry API —— 它能正確處理含空格的鍵路徑，
    /// 且實測可寫入 Policies\Microsoft\Windows Defender 根層（該位置並未受 WRP 保護）。
    /// </summary>
    public static string BuildPolicyScript(bool isDisable)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$script:Log = New-Object System.Collections.ArrayList");
        sb.AppendLine("function Log($m) { [void]$script:Log.Add([string]$m) }");
        sb.AppendLine("Log ('USER=' + [Security.Principal.WindowsIdentity]::GetCurrent().Name)");

        sb.AppendLine("$POLROOT = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows Defender'");
        sb.AppendLine("$RTPKEY = $POLROOT + '\\Real-Time Protection'");

        if (isDisable)
        {
            sb.AppendLine("try {");
            sb.AppendLine("  Set-ItemProperty -Path $POLROOT -Name 'DisableAntiSpyware' -Value 1 -Type DWord -Force -ErrorAction Stop");
            sb.AppendLine("  Log 'POLICY_ROOT_SET'");
            sb.AppendLine("} catch { Log ('POLICY_ROOT_FAIL: ' + $_.Exception.Message) }");
            sb.AppendLine("try {");
            sb.AppendLine("  if (-not (Test-Path $RTPKEY)) { New-Item -Path $RTPKEY -Force | Out-Null }");
            sb.AppendLine("  Set-ItemProperty -Path $RTPKEY -Name 'DisableRealtimeMonitoring' -Value 1 -Type DWord -Force -ErrorAction Stop");
            sb.AppendLine("  Log 'POLICY_RTP_SET'");
            sb.AppendLine("} catch { Log ('POLICY_RTP_FAIL: ' + $_.Exception.Message) }");
        }
        else
        {
            sb.AppendLine("try {");
            sb.AppendLine("  Remove-ItemProperty -Path $POLROOT -Name 'DisableAntiSpyware' -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("  Log 'POLICY_ROOT_CLEARED'");
            sb.AppendLine("} catch { Log ('POLICY_ROOT_FAIL: ' + $_.Exception.Message) }");
            sb.AppendLine("try {");
            sb.AppendLine("  Remove-ItemProperty -Path $RTPKEY -Name 'DisableRealtimeMonitoring' -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("  Log 'POLICY_RTP_CLEARED'");
            sb.AppendLine("} catch { Log ('POLICY_RTP_FAIL: ' + $_.Exception.Message) }");
        }

        sb.AppendLine("foreach ($m in $script:Log) { Write-Output $m }");
        return sb.ToString();
    }

    /// <summary>產生套用單一編輯計畫的 PowerShell 片段（$sets / $dels 陣列 + Edit-Subtree 呼叫）。</summary>
    private static string BuildEditCalls(RegistryEditPlan plan, string tempName)
    {
        var sb = new StringBuilder();

        sb.AppendLine("$sets = @(");
        var setEntries = plan.Sets.Select(e =>
            e.DwordValue.HasValue
                ? $"  @('{e.SubKey}','{e.ValueName}','{e.DwordValue.Value}')"
                : $"  @('{e.SubKey}','{e.ValueName}','{e.StringValue}','SZ')").ToList();
        for (var i = 0; i < setEntries.Count; i++)
        {
            // 逗號不可省略：PowerShell 的陣列字面量中，換行分隔的內層陣列會被展開成扁平陣列。
            var comma = i < setEntries.Count - 1 ? "," : "";
            sb.AppendLine(setEntries[i] + comma);
        }
        sb.AppendLine(")");

        sb.AppendLine("$dels = @(");
        var delEntries = plan.Deletes.Select(d => $"  @('{d.SubKey}','{d.ValueName}')").ToList();
        for (var i = 0; i < delEntries.Count; i++)
        {
            var comma = i < delEntries.Count - 1 ? "," : "";
            sb.AppendLine(delEntries[i] + comma);
        }
        sb.AppendLine(")");

        sb.AppendLine($"[void](Edit-Subtree '{plan.LiveKeyPath}' $sets $dels '{tempName}')");
        return sb.ToString();
    }

    /// <summary>建立群組原則根層的編輯計畫。</summary>
    public static RegistryEditPlan PolicyRootPlan(bool isDisable) =>
        isDisable
            ? new RegistryEditPlan(
                RegistryPaths.PoliciesLive,
                new[] { new HiveEditor.ValueEdit(RegistryEditPlan.RootSubKey, "DisableAntiSpyware", 1, null) },
                Array.Empty<HiveEditor.ValueDelete>())
            : new RegistryEditPlan(
                RegistryPaths.PoliciesLive,
                Array.Empty<HiveEditor.ValueEdit>(),
                new[] { new HiveEditor.ValueDelete(RegistryEditPlan.RootSubKey, "DisableAntiSpyware") });

    /// <summary>建立 Real-Time Protection 子鍵的編輯計畫。</summary>
    public static RegistryEditPlan PolicyRtpPlan(bool isDisable) =>
        isDisable
            ? new RegistryEditPlan(
                RegistryPaths.PoliciesLive,
                new[] { new HiveEditor.ValueEdit("Real-Time Protection", "DisableRealtimeMonitoring", 1, null) },
                Array.Empty<HiveEditor.ValueDelete>())
            : new RegistryEditPlan(
                RegistryPaths.PoliciesLive,
                Array.Empty<HiveEditor.ValueEdit>(),
                new[] { new HiveEditor.ValueDelete("Real-Time Protection", "DisableRealtimeMonitoring") });

    /// <summary>
    /// 產生 IFEO 封鎖／解除封鎖的腳本。
    ///
    /// 使用 hive 手法（reg save/load/restore）而非直接寫入，原因：
    ///   - 'Image File Execution Options' 含空格，reg.exe 不支援含空格的鍵路徑
    ///   - MsMpEng.exe 的 IFEO 值受保護，直接 Remove-ItemProperty 會回報成功但值未刪除；
    ///     整樹還原（restore）可繞過逐值保護
    /// </summary>
    public static string BuildIfeoScript(bool addBlock) =>
        BuildEditScript(IfeoPlan(addBlock), "WdcIfeo");

    /// <summary>建立 IFEO 的編輯計畫（供狀態驗證使用）。</summary>
    public static RegistryEditPlan IfeoPlan(bool addBlock)
    {
        var targets = DefenderServices.IfeoTargets;
        if (addBlock)
        {
            var sets = targets
                .Select(t => new HiveEditor.ValueEdit(t, "Debugger", null, "systray.exe"))
                .ToArray();
            return new RegistryEditPlan(RegistryPaths.IfeoLive, sets, Array.Empty<HiveEditor.ValueDelete>());
        }
        else
        {
            var dels = targets
                .Select(t => new HiveEditor.ValueDelete(t, "Debugger"))
                .ToArray();
            return new RegistryEditPlan(RegistryPaths.IfeoLive, Array.Empty<HiveEditor.ValueEdit>(), dels);
        }
    }

    /// <summary>
    /// 產生通用的「套用編輯計畫」腳本。
    /// </summary>
    public static string BuildEditScript(RegistryEditPlan plan, string tempName)
    {
        var sb = new StringBuilder();
        sb.Append(BuildPreamble());
        sb.Append(BuildEditSubtreeFunction());

        sb.AppendLine("$sets = @(");
        var setEntries = plan.Sets.Select(e =>
            e.DwordValue.HasValue
                ? $"  @('{e.SubKey}','{e.ValueName}','{e.DwordValue.Value}')"
                : $"  @('{e.SubKey}','{e.ValueName}','{e.StringValue}','SZ')").ToList();
        for (var i = 0; i < setEntries.Count; i++)
        {
            // 逗號不可省略：PowerShell 的陣列字面量中，換行分隔的內層陣列會被展開成扁平陣列。
            var comma = i < setEntries.Count - 1 ? "," : "";
            sb.AppendLine(setEntries[i] + comma);
        }
        sb.AppendLine(")");

        sb.AppendLine("$dels = @(");
        var delEntries = plan.Deletes.Select(d => $"  @('{d.SubKey}','{d.ValueName}')").ToList();
        for (var i = 0; i < delEntries.Count; i++)
        {
            var comma = i < delEntries.Count - 1 ? "," : "";
            sb.AppendLine(delEntries[i] + comma);
        }
        sb.AppendLine(")");

        sb.AppendLine($"[void](Edit-Subtree '{plan.LiveKeyPath}' $sets $dels '{tempName}')");
        sb.Append(BuildEpilogue());
        return sb.ToString();
    }

    /// <summary>產生切換即時監控的腳本。</summary>
    public static string BuildRealtimeProtectionScript(bool enable)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("try {");
        sb.AppendLine($"  Set-MpPreference -DisableRealtimeMonitoring ${(enable ? "false" : "true")} -ErrorAction Stop");
        sb.AppendLine($"  Write-Output '{(enable ? "RTP_ENABLED" : "RTP_DISABLED")}'");
        sb.AppendLine("} catch {");
        sb.AppendLine($"  Write-Output ('{(enable ? "RTP_ENABLE_FAILED" : "RTP_FAILED")}: ' + $_.Exception.Message)");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// 產生「讓 Defender 引擎停止運作」的腳本。
    ///
    /// 設計重點：本專案不使用任何第三方核心驅動程式。改以三個官方支援的機制達成：
    ///   1. Set-MpPreference -DisableRealtimeMonitoring → 關閉即時掃描與行為監控
    ///   2. IFEO 封鎖 → 引擎無法被載入（即使有其他程序嘗試啟動）
    ///   3. 服務 Start=4 → 開機不啟動
    ///
    /// 實測結果（Windows 11 build 26200）：
    ///   - MpCmdRun -Scan 回傳 0x800106ba（服務不可用）→ 確實無法掃描
    ///   - sc start WinDefend 回傳 577（簽章驗證失敗 = IFEO 攔截）→ 引擎無法重啟
    ///   - AMServiceEnabled / AntispywareEnabled / RealTimeProtectionEnabled 全部為 False
    ///
    /// 若引擎行程仍在（先前已執行），會嘗試以一般方式結束；
    /// 受 PPL 保護而無法結束時，IFEO 仍會阻止其重啟。
    /// </summary>
    public static string BuildStopEngineScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$script:Log = New-Object System.Collections.ArrayList");
        sb.AppendLine("function Log($m) { [void]$script:Log.Add([string]$m) }");
        sb.AppendLine("Log ('USER=' + [Security.Principal.WindowsIdentity]::GetCurrent().Name)");

        sb.AppendLine("--- 嘗試結束引擎行程 ---");
        sb.AppendLine("foreach ($name in @('MsMpEng','SecurityHealthSystray')) {");
        sb.AppendLine("  foreach ($p in @(Get-Process -Name $name -EA SilentlyContinue)) {");
        sb.AppendLine("    try {");
        sb.AppendLine("      Stop-Process -Id $p.Id -Force -ErrorAction Stop");
        sb.AppendLine("      Log ('STOPPED ' + $name + ' PID ' + $p.Id)");
        sb.AppendLine("    } catch {");
        sb.AppendLine("      Log ('STOP_BLOCKED ' + $name + ' PID ' + $p.Id + ' (PPL protected; IFEO still prevents reload)');");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");

        sb.AppendLine("--- 嘗試停止服務 ---");
        foreach (var svc in DefenderServices.All)
            sb.AppendLine("& sc.exe stop " + svc + " 2>&1 | ForEach-Object { Log ('SCSTOP " + svc + ": ' + $_) }");

        sb.AppendLine("Start-Sleep -Seconds 2");
        sb.AppendLine("--- 驗證 ---");
        sb.AppendLine("$p = Get-Process -Name MsMpEng -EA SilentlyContinue");
        sb.AppendLine("if ($p) { Log ('ENGINE_STILL_RUNNING pid=' + $p.Id) } else { Log 'ENGINE_STOPPED' }");
        sb.AppendLine("try {");
        sb.AppendLine("  $st = Get-MpComputerStatus");
        sb.AppendLine("  Log ('AMServiceEnabled=' + $st.AMServiceEnabled)");
        sb.AppendLine("  Log ('AntispywareEnabled=' + $st.AntispywareEnabled)");
        sb.AppendLine("  Log ('RealTimeProtectionEnabled=' + $st.RealTimeProtectionEnabled)");
        sb.AppendLine("} catch { Log ('MP_STATUS_FAIL: ' + $_.Exception.Message) }");

        sb.Append(BuildEpilogue());
        return sb.ToString();
    }

    /// <summary>產生啟動 Defender 服務的腳本。</summary>
    public static string BuildStartServicesScript()
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine("$script:Log = New-Object System.Collections.ArrayList");
        sb.AppendLine("function Log($m) { [void]$script:Log.Add([string]$m) }");
        sb.AppendLine("Log ('USER=' + [Security.Principal.WindowsIdentity]::GetCurrent().Name)");
        sb.AppendLine("& sc.exe start WinDefend 2>&1 | ForEach-Object { Log ('SC_START WinDefend: ' + $_) }");
        sb.AppendLine("Start-Sleep -Seconds 3");
        sb.AppendLine("if (-not (Get-Process -Name MsMpEng -EA SilentlyContinue)) {");
        sb.AppendLine("  & sc.exe start WinDefend 2>&1 | ForEach-Object { Log ('RETRY WinDefend: ' + $_) }");
        sb.AppendLine("  Start-Sleep -Seconds 3");
        sb.AppendLine("}");
        sb.AppendLine("& sc.exe start WdNisSvc 2>&1 | ForEach-Object { Log ('SC_START WdNisSvc: ' + $_) }");
        sb.AppendLine("& sc.exe start SecurityHealthService 2>&1 | ForEach-Object { Log ('SC_START SecurityHealthService: ' + $_) }");
        sb.AppendLine("Start-Sleep -Seconds 2");
        sb.AppendLine("Start-Process (Join-Path $env:SystemRoot 'System32\\SecurityHealthSystray.exe') -ErrorAction SilentlyContinue | Out-Null");
        sb.Append(BuildEpilogue());
        return sb.ToString();
    }
}

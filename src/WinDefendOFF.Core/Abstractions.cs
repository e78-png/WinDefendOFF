namespace WinDefendOFF.Core;

/// <summary>
/// 以 SYSTEM 身分執行腳本的抽象。
/// 讓上層流程邏輯能在沒有管理員權限的環境下以測試替身驗證。
/// </summary>
public interface ISystemExecutor
{
    /// <summary>
    /// 以 SYSTEM 身分執行 PowerShell 腳本，回傳輸出與結束碼。
    /// </summary>
    /// <param name="scriptPath">腳本檔路徑。</param>
    /// <param name="timeout">逾時時間。</param>
    /// <param name="plans">
    /// 此腳本預期套用的登錄檔編輯計畫。真實執行時腳本本身會完成編輯，
    /// 此參數僅供測試替身使用，讓替身能忠實模擬執行後的登錄檔狀態。
    /// </param>
    TaskRunResult RunScript(
        string scriptPath,
        TimeSpan timeout,
        IReadOnlyList<RegistryEditPlan>? plans = null);
}

/// <summary>登錄檔子樹讀寫的抽象。</summary>
public interface IRegistryEditor
{
    /// <summary>讀取 DWORD 值，不存在時回傳 null。</summary>
    int? ReadDword(string subKey, string valueName);

    /// <summary>讀取字串值，不存在時回傳 null。</summary>
    string? ReadString(string subKey, string valueName);

    /// <summary>
    /// 直接寫入 DWORD 值。
    ///
    /// 用於父鍵根層的值（如 Policies\Microsoft\Windows Defender\DisableAntiSpyware）——
    /// 該位置無法用 hive 手法表達（子鍵名稱會被 PowerShell 特殊處理）。
    /// WRP 保護的位置仍會被拒絕，此時回傳 false。
    /// </summary>
    bool WriteDword(string subKey, string valueName, int value);

    /// <summary>刪除 DWORD 值。回傳是否成功（值不存在視為成功）。</summary>
    bool DeleteValue(string subKey, string valueName);

    /// <summary>直接寫入字串值（REG_SZ）。</summary>
    bool WriteString(string subKey, string valueName, string value);

    /// <summary>離線編輯登錄檔子樹（繞過 WRP 保護的關鍵操作）。</summary>
    HiveEditResult EditSubtree(
        string liveKeyPath,
        IReadOnlyList<HiveEditor.ValueEdit> edits,
        IReadOnlyList<HiveEditor.ValueDelete> deletes,
        Action<string>? log = null);
}

/// <summary>Defender 狀態查詢的抽象。</summary>
public interface IDefenderStateSource
{
    DefenderState Read();
}

/// <summary>PPL 行程終結的抽象。</summary>
public interface IProcessTerminator
{
    /// <summary>終結指定的行程名稱清單，回傳成功終結的項目。</summary>
    IReadOnlyList<string> KillProcesses(
        IReadOnlyList<string> processNames, Action<string>? log = null);
}

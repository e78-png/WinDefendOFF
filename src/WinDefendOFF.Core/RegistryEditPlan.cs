namespace WinDefendOFF.Core;

/// <summary>
/// 一組登錄檔編輯操作。
///
/// 同時存在兩個用途：
///   1. 產生 SYSTEM 腳本（reg save/load/restore 的參數來源）
///   2. 讓測試替身能忠實模擬「腳本執行後登錄檔會變成什麼」
///
/// 兩者共用同一份資料可避免「腳本內容」與「預期結果」漂移。
/// </summary>
public sealed record RegistryEditPlan(
    string LiveKeyPath,
    IReadOnlyList<HiveEditor.ValueEdit> Sets,
    IReadOnlyList<HiveEditor.ValueDelete> Deletes)
{
    /// <summary>
    /// 代表「寫在父鍵本身」的子鍵標記。
    ///
    /// 刻意不用 "@ROOT" 這類以 @ 開頭的字串：PowerShell 會把 @ 開頭的 token
    /// 視為 splatting / 陣列展開語意，導致子鍵名被截斷成單一字元
    /// （實測曾產生名為 "@" 與 "D" 的垃圾子鍵）。
    /// </summary>
    public const string RootSubKey = "<ROOT>";

    /// <summary>以 .NET Registry API 的子鍵格式取得某個值的儲存鍵。</summary>
    public string GetStorageKey(string subKey, string valueName)
    {
        var baseKey = RegistryPaths.ToSubKey(LiveKeyPath);
        var key = subKey == RootSubKey ? baseKey : $"{baseKey}\\{subKey}";
        return $"{key}\\{valueName}";
    }
}

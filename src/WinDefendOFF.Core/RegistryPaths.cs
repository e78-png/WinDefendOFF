namespace WinDefendOFF.Core;

/// <summary>
/// 登錄檔路徑常數與正規化。
///
/// 兩套 API 對路徑的期待不同，混用是最容易出錯的地方：
///   - reg.exe 需要完整前綴：<c>HKLM\SYSTEM\...</c>
///   - Microsoft.Win32.Registry.LocalMachine 不接受 <c>HKLM\</c> 前綴：
///     <c>OpenSubKey("HKLM\\SYSTEM\\...")</c> 會直接失敗
///
/// 因此所有路徑以「live key」（帶 HKLM\ 前綴）為唯一來源，
/// 透過 <see cref="ToSubKey"/> 轉成 .NET API 需要的格式。
/// </summary>
public static class RegistryPaths
{
    /// <summary>服務設定（reg.exe 格式）。</summary>
    public const string ServicesLive = @"HKLM\SYSTEM\CurrentControlSet\Services";

    /// <summary>群組原則（reg.exe 格式）。</summary>
    public const string PoliciesLive = @"HKLM\SOFTWARE\Policies\Microsoft\Windows Defender";

    /// <summary>Image File Execution Options（reg.exe 格式）。</summary>
    public const string IfeoLive = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    /// <summary>Defender 功能設定（reg.exe 格式）。</summary>
    public const string DefenderFeaturesLive = @"HKLM\SOFTWARE\Microsoft\Windows Defender";

    /// <summary>
    /// 將 live key 轉為 .NET Registry API 需要的子鍵路徑（移除 HKLM\ 前綴）。
    /// </summary>
    public static string ToSubKey(string liveKey) =>
        liveKey.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
            ? liveKey[5..]
            : liveKey;

    /// <summary>服務 Start 值的子鍵路徑（.NET 格式）。</summary>
    public static string ServiceStartKey(string serviceName) =>
        ToSubKey(ServicesLive) + "\\" + serviceName;

    /// <summary>群組原則根層的子鍵路徑（.NET 格式）。</summary>
    public static string PolicyRootKey() => ToSubKey(PoliciesLive);

    /// <summary>IFEO 目標的子鍵路徑（.NET 格式）。</summary>
    public static string IfeoTargetKey(string imageName) =>
        ToSubKey(IfeoLive) + "\\" + imageName;
}

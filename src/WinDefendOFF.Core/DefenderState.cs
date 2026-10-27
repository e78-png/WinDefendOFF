using System.Runtime.InteropServices;

namespace WinDefendOFF.Core;

/// <summary>單一 Defender 相關服務/驅動程式的狀態。</summary>
public sealed class ServiceState
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StartType { get; init; } = string.Empty;

    public bool IsRunning => Status.Equals("Running", StringComparison.OrdinalIgnoreCase);
    public bool IsDisabled => StartType.Equals("Disabled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>App 觀測到的 Defender 整體狀態。</summary>
public sealed class DefenderState
{
    public IReadOnlyList<ServiceState> Services { get; init; } = Array.Empty<ServiceState>();

    /// <summary>Defender 引擎是否實際運作中 (MsMpEng 行程存在且服務未停用)。</summary>
    public bool EngineRunning { get; init; }

    /// <summary>是否已完成停用 (所有關鍵服務 Disabled 且引擎未執行)。</summary>
    public bool IsDisabled { get; init; }

    /// <summary>即時保護是否啟用。</summary>
    public bool RealtimeProtectionEnabled { get; init; }

    /// <summary>
    /// Defender 防護引擎整體是否啟用 (AntispywareEnabled)。
    /// 這是判斷「防護確實已關閉」最可靠的指標 ——
    /// 當它為 false 時，即使引擎行程仍在，也無法進行任何掃描。
    /// </summary>
    public bool AntispywareEnabled { get; init; }

    /// <summary>Defender 服務層級是否啟用 (AMServiceEnabled)。</summary>
    public bool AmServiceEnabled { get; init; }

    /// <summary>篡改保護是否啟用。</summary>
    public bool TamperProtectionEnabled { get; init; }

    /// <summary>是否有殘留的 IFEO 封鎖 (開啟 Defender 後必須移除，否則服務無法啟動)。</summary>
    public bool IfeoBlockPresent { get; init; }

    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public ServiceState? Find(string name) =>
        Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Defender 涉及到的服務名稱與其「啟用時」應有的開機啟動型態。</summary>
public static class DefenderServices
{
    public const string WinDefend = "WinDefend";
    public const string WdNisSvc = "WdNisSvc";
    public const string WdFilter = "WdFilter";
    public const string SecurityHealthService = "SecurityHealthService";

    /// <summary>停用時全部設為 Disabled (Start=4)。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        WinDefend, WdNisSvc, WdFilter, SecurityHealthService
    };

    /// <summary>引擎相關 (停用時必須關閉)。</summary>
    public static readonly IReadOnlyList<string> EngineCritical = new[]
    {
        WinDefend, WdNisSvc
    };

    /// <summary>
    /// 重新啟用時各服務應有的 Start 值。
    /// WinDefend=2(Auto), WdNisSvc=3(Manual), WdFilter=0(Boot), SecurityHealthService=3(Manual)
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> DefaultStartValues =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [WinDefend] = 2,
            [WdNisSvc] = 3,
            [WdFilter] = 0,
            [SecurityHealthService] = 3,
        };

    /// <summary>以 IFEO Debugger 封鎖的執行檔 (阻止 Defender 引擎被載入)。</summary>
    public static readonly IReadOnlyList<string> IfeoTargets = new[]
    {
        "MsMpEng.exe",
        "SecurityHealthSystray.exe",
        "SecurityHealthService.exe",
    };
}

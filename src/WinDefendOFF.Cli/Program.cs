using System.Text;
using WinDefendOFF.Core;

// CLI 測試宿主：驗證核心流程，不需 GUI。
// 用法: WinDefendOFF.Cli <status|disable|enable> [輸出檔路徑]

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
var outputPath = args.Length > 1 ? args[1] : null;

var sb = new StringBuilder();
void Say(string line = "") => sb.AppendLine(line);

void Flush()
{
    if (outputPath is null)
    {
        Console.Write(sb.ToString());
        return;
    }
    try
    {
        File.WriteAllText(outputPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine("OUTPUT_FILE=" + outputPath);
    }
    catch (Exception ex)
    {
        Console.WriteLine("寫入輸出檔失敗: " + ex.Message);
        Console.Write(sb.ToString());
    }
}

// 建立 SYSTEM 排程工作需要管理員權限；未提升時自動以 runas 重新啟動。
if (!Elevation.IsElevated())
{
    if (Elevation.RelaunchElevated(args))
    {
        Say("已以系統管理員身分重新啟動。");
        Flush();
        return 0;
    }
    Say("需要系統管理員權限，且使用者拒絕了 UAC 提權。");
    Flush();
    return 3;
}

try
{

var workDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WinDefendOFF");
Directory.CreateDirectory(workDir);

var controller = new DefenderController(workDir, m => Say("  " + m));

void PrintState(DefenderState s)
{
    Say();
    Say("=== Defender 狀態 @" + s.CapturedAt.ToString("HH:mm:ss") + " ===");
    Say("  整體: " + (s.IsDisabled ? "已停用" : "運作中"));
    Say("  引擎執行中: " + s.EngineRunning);
    Say("  即時保護: " + s.RealtimeProtectionEnabled);
    Say("  篡改保護: " + s.TamperProtectionEnabled);
    Say("  IFEO 封鎖: " + s.IfeoBlockPresent);
    foreach (var svc in s.Services)
    {
        Say($"  {svc.Name,-24} {svc.Status,-12} {svc.StartType}");
    }
    Say();
}

switch (command)
{
    case "status":
    {
        PrintState(controller.ReadState());
        Flush();
        return 0;
    }

    case "check":
    {
        Say("=== 環境相容性檢查 ===");
        var compat = new CompatibilityChecker().Check();
        foreach (var item in compat.Items)
        {
            var mark = item.Passed ? "[ OK ]" : (item.Critical ? "[!!  ]" : "[警告]");
            Say($"{mark} {item.Name}");
            Say($"        {item.Detail}");
            if (!item.Passed && item.Remedy is not null)
                Say($"        → {item.Remedy}");
        }
        Say();
        Say(compat.CanDisableWithoutReboot
            ? "結論：可即時停用 / 啟用，無需重新啟動電腦。"
            : "結論：存在阻斷性問題，無法保證即時切換。");
        Flush();
        return compat.CanDisableWithoutReboot ? 0 : 1;
    }

    case "disable":
    case "enable":
    {
        PrintState(controller.ReadState());

        var action = command == "disable" ? DefenderAction.Disable : DefenderAction.Enable;
        var result = action == DefenderAction.Disable
            ? controller.Disable(controller.ReadState())
            : controller.Enable(controller.ReadState());

        Say();
        Say("=== 步驟結果 ===");
        foreach (var step in result.Steps)
        {
            Say($"  [{(step.Success ? "OK  " : "FAIL")}] {step.Title}");
            if (step.Error is not null) Say("         錯誤: " + step.Error);
            foreach (var msg in step.Messages)
            {
                var t = msg.TrimEnd();
                if (t.Length > 0) Say("         " + t);
            }
        }

        Say();
        Say("=== 最終結果: " + (result.Success ? "成功" : "部分失敗") + " ===");
        PrintState(result.FinalState);
        Flush();
        return result.Success ? 0 : 1;
    }

    default:
    {
        Say("用法: WinDefendOFF.Cli <status|check|disable|enable> [輸出檔路徑]");
        Flush();
        return 2;
    }
}
}
catch (Exception ex)
{
    Say("未處理的例外: " + ex);
    Flush();
    return 99;
}

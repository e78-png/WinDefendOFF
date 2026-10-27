# 技術細節

本文件記錄 WinDefendOFF 在 Windows 11 (build 26200) 上實測的機制與限制。

---

## 1. Defender 的自我保護機制

### 1.1 WRP — 登錄檔保護

以下位置對一般使用者與管理員（含 SYSTEM）皆拒絕直接寫入：

| 位置 | 用途 |
|---|---|
| `HKLM\SYSTEM\CurrentControlSet\Services\WinDefend` | 服務設定 |
| `HKLM\SYSTEM\CurrentControlSet\Services\WdNisSvc` | 網路檢查服務 |
| `HKLM\SYSTEM\CurrentControlSet\Services\WdFilter` | 核心篩選驅動 |

錯誤訊息為 `Attempted to perform an unauthorized operation`。

**繞過手法**（與 DefenderControl 相同）：

```
reg save  <父鍵>  temp.hiv /y      # 需要 SeBackupPrivilege
reg load  HKLM\TempHive temp.hiv
reg add   HKLM\TempHive\<子鍵> ...  # 此時可寫入原本受保護的子鍵
reg unload HKLM\TempHive
reg restore <父鍵> temp.hiv /f      # 需要 SeRestorePrivilege
```

關鍵在於**還原「父」樹**：父鍵（`Services`）本身可寫，而 `reg restore` 會以匯出時的
內容覆寫整個子樹，包含先前被 DACL 保護的子鍵。

若改為還原「子」樹（`Services\WinDefend`），則會失敗：

```
RESTORE_FAIL: HKLM\SYSTEM\CurrentControlSet\Services\WinDefend
```

### 1.2 Protected Service — 服務控制層級保護

`WinDefend` 是 Windows 的 protected service。實測（以 SYSTEM 身分）：

| 命令 | 結果 |
|---|---|
| `sc query WinDefend` | ✅ 成功 |
| `sc start WinDefend` | ✅ 成功（服務已在執行時回 1056） |
| `sc stop WinDefend` | ❌ `OpenService FAILED 5: Access is denied` |
| `sc config WinDefend start= demand` | ❌ `OpenService FAILED 5: Access is denied` |
| WMI `Win32_Service.StopService()` | ❌ ReturnValue = 2 (Access denied) |

曾嘗試移除 `HKLM\...\Services\WinDefend\Security` 子鍵（含 protected descriptor
的 REG_BINARY）後再試 — **無效**。SCM 對 protected service 的限制由核心強制。

**這是不對稱的關鍵**：

- 服務的**啟動**可以立即執行 → 「啟用」不需要重開機
- 服務的**停止**無法執行 → 「停用」只能靠封鎖引擎載入

### 1.3 PPL — 行程保護

`MsMpEng.exe` 以 Protected Process Light 執行。實測：

```
Stop-Process -Id <MsMpEng> -Force   (SYSTEM 身分)
→ Cannot stop process "MsMpEng" because of the following error: Access is denied
```

---

## 2. 採用的手法

本專案**不使用任何第三方核心驅動程式**，僅使用 Windows 官方機制。

早期版本曾採用開源專案 `wesmar/WinDefCtl` 的作法：內嵌簽章但存在弱點的核心驅動
（`kvckiller.sys`），透過 IOCTL `0x22201C` 從核心終結受 PPL 保護的行程。

該做法有三個問題：

1. **授權不清** — `wesmar/WinDefCtl` 的 GitHub API 回傳 `"license": null`，
   內嵌其二進位檔有散播風險。
2. **Defender 誤判** — 內嵌的驅動程式會被 Windows Defender 判定為惡意並隔離，
   導致散播時「開啟沒反應」。
3. **不必要** — 實測證明不需終結行程也能達成完整停用（見下）。

### 2.1 替代方案：三個官方機制的組合

| 機制 | 作用 | 需 SYSTEM？ |
|---|---|---|
| `Set-MpPreference -DisableRealtimeMonitoring $true` | 關閉即時掃描與行為監控 | 否（管理員即可） |
| IFEO `Debugger=systray.exe` | 引擎無法被載入 | 是（該鍵路徑含空格，reg.exe 不支援） |
| 服務 `Start=4` | 開機不啟動 | 是（Services 子樹受 WRP 保護） |

實測驗證（三項皆成立時）：

```
MpCmdRun -Scan -ScanType 2
→ [Failed][0x800106ba] 服務不可用          ← 確實無法掃描

sc start WinDefend
→ StartService FAILED 577                   ← IFEO 攔截引擎載入

AMServiceEnabled = False
AntispywareEnabled = False
RealTimeProtectionEnabled = False
```

**結論**：不需要核心層級的行程終結，防護確實已完全失效。

### 2.2 IFEO 封鎖

```
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\MsMpEng.exe
    Debugger = REG_SZ "systray.exe"
```

效果：即使有其他程序嘗試啟動 `MsMpEng.exe`，也只會執行 `systray.exe`
（系統匣圖示程式，無害），真正的引擎映像不會被載入。

這同時解決了兩件事：

1. 引擎不會在停用後被服務控制管理器或其他機制重新拉起
2. 引擎不會在開機時被載入（不依賴 Start 值）

**注意**：啟用時必須**先**移除 IFEO 再 `sc start WinDefend`，
否則服務啟動會因載入被攔截的映像而失敗（錯誤碼 577）。

### 2.3 只在必要時使用 SYSTEM

實測確認以下位置**管理員權限即可寫入**，不需要 SYSTEM：

- `HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\DisableAntiSpyware`
- `HKLM\SOFTWARE\Policies\Microsoft\Windows Defender\Real-Time Protection\DisableRealtimeMonitoring`

只有 `HKLM\SYSTEM\CurrentControlSet\Services\*` 受 WRP 保護，需要 SYSTEM 排程工作。

---

## 3. 實作上踩到的坑

### 3.1 PowerShell 陣列字面量會被展平

```powershell
$sets = @(
  @('a','b','c')
  @('d','e','f')
)
$sets.Count   # → 6，不是 2
```

內層陣列被**展開**成扁平陣列，導致後續 `$s[0]`、`$s[1]` 取到的是字元而非欄位。
必須用逗號分隔：

```powershell
$sets = @(
  @('a','b','c'),
  @('d','e','f')
)
$sets.Count   # → 2
```

此問題造成 `reg add` 大量出現 `Invalid syntax` 錯誤。

### 3.2 UTF-8 BOM 是必要的

Windows PowerShell 5.1 讀取無 BOM 的 `.ps1` 時使用 ANSI（繁中 CP950）解碼，
腳本內的中文字元會被破壞並引發語法錯誤：

```
ParserError: The string is missing the terminator: '
```

所有產生的 `.ps1` 必須以 **UTF-8 with BOM** 寫入。

### 3.3 `reg load` 需要 `HKLM\` 前綴

```
reg load WdcSvc temp.hiv      → ERROR: Invalid key name.
reg load HKLM\WdcSvc temp.hiv → 成功
```

### 3.4 `reg.exe` 不支援含空格的鍵路徑

```
reg add "HKLM\...\Real-Time Protection" /v X /t REG_DWORD /d 1 /f
→ ERROR: The parameter is incorrect.
```

即使手動加引號也一樣（`reg.exe` 會把引號當成路徑的一部分）。

**影響的鍵路徑**：
- `Image File Execution Options`（IFEO）
- `Real-Time Protection`（群組原則子鍵）

**解法**：改用 .NET 的 `Microsoft.Win32.Registry` API，它能正確處理含空格的鍵路徑。

### 3.5 直接刪除 IFEO 值會「假成功」

```
Remove-ItemProperty -Path '...\Image File Execution Options\MsMpEng.exe' -Name 'Debugger' -Force
→ 無錯誤，但值仍在
```

`MsMpEng.exe` 的 IFEO 值受保護。必須用 hive 整樹還原（`reg restore`）才能真正刪除。

### 3.6 根層值不能放在 hive 陣列裡

`Policies\Microsoft\Windows Defender` 的根層值（`DisableAntiSpyware`）
若以空字串表示子鍵路徑，會產生無效路徑。本專案改為直接以 .NET API 寫入。

### 3.7 WMI 狀態會是陳舊的

Defender 停止後，`Get-MpComputerStatus` 仍可能回報 `AntispywareEnabled = True`。
**驗證必須以登錄檔的實際值為準**，不能依賴 WMI。

### 3.8 殘留行程鎖住檔案

先前崩潰的行程會鎖住 DLL，導致後續建置失敗：

```
warning MSB3026: ... because it is being used by another process.
  檔案鎖定者: "WinDefendOFF.Cli.exe (22676)"
```

除錯時需先清除殘留行程。

---

## 4. 驗證方式

`src/WinDefendOFF.Cli` 提供可腳本化的介面，便於自動化驗證：

```powershell
$cli = ".\publish-sc\cli\WinDefendOFF.Cli.exe"
& $cli status
& $cli check
& $cli disable
& $cli enable
```

狀態讀取分三個來源，可靠性不同：

| 資料 | 來源 | 備註 |
|---|---|---|
| 服務狀態 | `sc query` | SCM 可能快取舊的啟動型態 |
| 服務啟動型態 | `Registry\...\Services\<svc>\Start` | **權威來源** |
| 即時保護 / 篡改保護 | WMI `MSFT_MpComputerStatus` | 服務停止後會陳舊 |
| 引擎是否執行 | `Process.GetProcessesByName` | 直接且可靠 |

驗證判準以**登錄檔的實際值 + 引擎行程是否存在**為準。

---

## 5. 已知限制

- **WdFilter（核心篩選驅動）** 是開機啟動的核心驅動，無法在不重開機的情況下卸載。
  停用後它仍留在記憶體中，但因為即時掃描已關閉，不會進行任何掃描。
  下次開機時其 `Start` 值為 `4`，不會載入。
- **SecurityHealthService** 為 protected service，`sc stop` 被拒。
  停用時透過 IFEO 封鎖其啟動；它本身會在開機後依 `Start` 值判斷。
- 群組原則 `DisableAntiSpyware` 在部分 Windows 版本已不再被 Defender 遵循
  （Microsoft 於 2020 年後改以其他機制）。本專案仍寫入該值以保持狀態一致，
  但實際效果主要來自 IFEO 封鎖與即時掃描關閉。

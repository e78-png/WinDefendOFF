#  contributing to WinDefendOFF

感謝你願意參與。這個文件說明如何開始。

---

## 開發環境

| 需求 | 版本 |
|---|---|
| .NET SDK | 8.0 以上 |
| 作業系統 | Windows 10 1809 (build 17763) 以上 / Windows 11，x64 |
| 執行測試 | 不需要管理員權限 |

```powershell
git clone <repo-url>
cd WinDefendOFF
dotnet build WinDefendOFF.sln -c Release
dotnet test tests/WinDefendOFF.Tests -c Release
```

---

## 專案結構

```
src/
├── WinDefendOFF.Core/     核心邏輯（無 UI 相依，可獨立測試）
│   ├── Abstractions.cs        可注入的介面（ISystemExecutor、IRegistryEditor…）
│   ├── ScriptBuilder.cs       產生 PowerShell 腳本（純函式，最容易出錯的 部分）
│   ├── DefenderController.cs  流程编排
│   ├── RegistryPaths.cs       登錄檔路徑常數與正規化
│   ├── SystemTaskRunner.cs    SYSTEM 身分排程執行
│   └── CompatibilityChecker.cs 環境相容性檢查
├── WinDefendOFF.App/      WinForms GUI
└── WinDefendOFF.Cli/      CLI
tests/
└── WinDefendOFF.Tests/    xUnit 測試
```

---

## 設計原則

### 1. 核心邏輯不直接呼叫 Win32 API

`WinDefendOFF.Core` 透過 `ISystemExecutor`、`IRegistryEditor`、
`IDefenderStateSource` 這三個介面與外部世界互動。測試用假實作注入。

新增 Win32 互動時，先在 `Abstractions.cs` 定義介面，再實作。

### 2. PowerShell 腳本是純函式輸出

`ScriptBuilder` 的每個方法都只回傳字串，不做 I/O。這讓腳本內容可以直接
用單元測試驗證 —— 這個專案踩過的坑（陣列展開、UTF-8 BOM、引號、路徑前綴）
全都是在這一層被測試捕捉到的。

**修改腳本後務必更新對應的測試。**

### 3. 驗證以「實際機制」為準

不要依賴 Defender 的 WMI 狀態 —— 服務停止後 WMI 仍會回報舊值。
驗證應依據：

- 登錄檔的實際值（服務 Start 值、IFEO Debugger、群組原則）
- 行程是否真的存在
- `Get-MpComputerStatus` 僅作參考

---

## 提交訊息

使用中文或英文皆可，格式：

```
<類型>: <簡述>

<詳細說明>
```

類型：`feat` / `fix` / `refactor` / `test` / `docs` / `chore`

---

## 測試要求

- 新增功能必須附測試
- 修 bug 必須先寫出能重現該 bug 的測試
- 測試需驗證**可觀察行為**，而非實作細節

反例（驗證實作細節）：

```csharp
// ✗ 只驗證有呼叫某個方法
Assert.True(mockRegistry.WasEditCalled);
```

正例（驗證可觀察結果）：

```csharp
// ✓ 驗證產生的腳本內容正確
Assert.Contains("@('WinDefend','Start','4')", script);
```

---

## 安全考量

本專案會停用裝置的防毒軟體。修改時請特別注意：

- 不要引入任何第三方核心驅動程式（會造成授權與散播問題）
- 不要將操作寫入不需要管理員權限的位置
- 涉及登錄檔的操作必須考慮 WRP 保護
- 任何失敗路徑都必須能降級並回報，不得靜默失敗

---

## 提交 PR 前檢查清單

- [ ] `dotnet build WinDefendOFF.sln -c Release` 無錯誤無警告
- [ ] `dotnet test tests/WinDefendOFF.Tests -c Release` 全部通過
- [ ] 在真實機器上驗證 `disable` → `enable` → `disable` 往返
- [ ] 確認沒有殘留的排程工作、驅動服務或暫存 hive
- [ ] 若有行為變更，更新 `CHANGELOG.md` 與 `docs/TECHNICAL.md`

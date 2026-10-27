# 發布與散播

本文件說明如何把 WinDefendOFF 打包成可分發的發行檔。

---

## 一鍵打包

```powershell
# 一般發布（需目標電腦有 .NET 8 Desktop Runtime，約 0.7 MB）
.\build\publish.ps1

# 攜式發布（內含 runtime，單一 exe，約 66 MB）
.\build\publish.ps1 -SelfContained

# 打包成 zip 便於散播
.\build\publish.ps1 -SelfContained -Zip
```

輸出於 `publish/`（或 `publish-sc/`）。

---

## 發行格式比較

| | framework-dependent | self-contained |
|---|---|---|
| 指令 | `.\build\publish.ps1` | `.\build\publish.ps1 -SelfContained` |
| 大小 | ~0.7 MB | ~66 MB |
| 目標機需求 | .NET 8 Desktop Runtime | 免安裝任何相依套件 |
| 適用情境 | 自己的電腦、多台機台統一安裝 runtime | 散播給他人、臨時測試機 |

self-contained 模式使用 `PublishSingleFile`，整個程式打包成單一 `WinDefendOFF.exe`，
可直接複製到任何 Windows 10/11 x64 機器執行。

---

## 發行內容

```
publish/
├── WinDefendOFF.exe          GUI 主程式
├── WinDefendOFF.Core.dll     核心邏輯
├── WinDefendOFF.dll          GUI 前端
├── WinDefendOFF.deps.json
├── WinDefendOFF.runtimeconfig.json
├── WinDefendOFF.ico          應用程式圖示
├── README.md
├── TECHNICAL.md
└── cli/
    ├── WinDefendOFF.Cli.exe  命令列介面
    └── ...
```

---

## 快捷啟動

發佈目錄中可加入批次檔，方便非技術使用者：

```bat
@echo off
:: WinDefendOFF 啟動器
:: 會自動以系統管理員身分執行
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~dp0WinDefendOFF.exe' -Verb RunAs"
) else (
    "%~dp0WinDefendOFF.exe"
)
```

存為 `WinDefendOFF.bat`。

---

## 執行需求

| 項目 | 需求 |
|---|---|
| 作業系統 | Windows 10 1809 以上 / Windows 11（x64） |
| 權限 | **系統管理員**（程式會自動觸發 UAC 提權） |
| 執行階段 | .NET 8 Desktop Runtime（僅 framework-dependent 模式需要） |
| 磁碟 | 約 100 MB（self-contained） |

---

## 版本與發布

版本號由 `-Version` 參數控制，會同時套用至組件版本資訊：

```powershell
.\build\publish.ps1 -Version 1.2.0 -SelfContained -Zip
# → WinDefendOFF-1.2.0.zip
```

版本資訊可在檔案內容的 `Product version` 檢視。

---

## 重新產生圖示

圖示由 `build/IconGen` 產生（非手繪像素檔）：

```powershell
dotnet run --project build/IconGen -c Release -- assets
```

會輸出 `assets/WinDefendOFF.ico`（多尺寸）與 `assets/WinDefendOFF.png`（256px 預覽）。

---

## 安裝到其他電腦

### 方式 A：直接複製（攜式）

1. 以 `-SelfContained` 發布
2. 將 `publish-sc/` 整個資料夾複製到目標電腦
3. 雙擊 `WinDefendOFF.exe`

### 方式 B：建立安裝套件

若需要安裝程式（開始選單、桌面捷徑、解除安裝），可用 Inno Setup 或 WiX 封裝：

```iss
[Setup]
AppName=WinDefendOFF
AppVersion=1.0.0
DefaultDirName={autopf}\WinDefendOFF
DefaultGroupName=WinDefendOFF
OutputBaseFilename=WinDefendOFF-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes

[Files]
Source: "publish-sc\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\WinDefendOFF"; Filename: "{app}\WinDefendOFF.exe"
Name: "{autodesktop}\WinDefendOFF"; Filename: "{app}\WinDefendOFF.exe"

[Run]
Filename: "{app}\WinDefendOFF.exe"; Description: "立即啟動"; Flags: nowait postinstall skipifsilent
```

注意：安裝程式本身也應要求管理員權限：

```iss
[Setup]
PrivilegesRequired=admin
```

---

## 簽章（選用）

若要散播給他人，強烈建議對執行檔做程式碼簽章，避免被防毒軟體誤判：

```powershell
signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256 `
           /f "publish-sc\WinDefendOFF.exe" /p <password>
```

---

## 已知散播限制

本專案**零第三方二進位**，僅使用 Windows 官方機制，因此 Defender 誤判的機率極低。

仍可能被攔截的情況：

- 工具本身會修改 Defender 的受保護設定，部分環境的啟發保護（ASR）規則可能攔截
- 企業環境的應用程式控制清單（AppLocker / WDAC）可能禁止執行

處理方式（擇一）：

1. 簽章執行檔（見上）—— 建議做法，可大幅降低誤判
2. 在目標機先將路徑加入 Defender 排除項目
3. 散播前先關閉即時保護

工具本身不會自行修改排除項目 —— 那是使用者端的政策決定。

若執行時出現「被防毒軟體隔離」，症狀是「雙擊沒反應」或「檔案消失」。
可先用 CLI 確認環境：

```powershell
.\publish-sc\cli\WinDefendOFF.Cli.exe check
```

---

## 驗證發布產物

打包後建議執行一次往返測試：

```powershell
$cli = ".\publish-sc\cli\WinDefendOFF.Cli.exe"
& $cli check     # 先確認環境相容性
& $cli status
& $cli disable
& $cli enable
& $cli status
```

三次操作都應回報「最終結果: 成功」，且不需要重新啟動電腦。

---

## 在目標機上的首次部署檢查

散播到其他電腦後，建議照下列順序確認：

```powershell
# 1. 環境檢查（不變更任何設定）
.\WinDefendOFF.Cli.exe check
```

若輸出「結論：可即時停用 / 啟用」，即可直接使用。

若出現 `[!!  ]` 開頭的項目，該項為阻斷性問題，請依報告中的建議處理。
若出現 `[警告]`，代表該功能可能降級（例如驅動無法載入，此時需重開機讓變更完全生效）。

### 排除 Defender 誤判

若 Defender 將程式隔離，症狀是「雙擊沒反應」或「檔案消失」。
處理方式：

```powershell
# 先還原被隔離的檔案（從 Defender 隔離區還原）
# 再加入排除項目
Add-MpPreference -ExclusionPath "C:\Path\To\WinDefendOFF"
Add-MpPreference -ExclusionProcess "WinDefendOFF.exe"
Add-MpPreference -ExclusionProcess "WinDefendOFF.Cli.exe"
```

或在 Windows 安全性 → 病毒與威脅防護 → 排除項目 → 新增資料夾排除。

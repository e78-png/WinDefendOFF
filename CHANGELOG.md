# 變更記錄

本專案遵循 [語意化版本](https://semver.org/lang/zh-TW/)。

## [1.0.0] — 2026-10-27

首個正式發行版。

### 新增

- **WinForms GUI**（`WinDefendOFF.exe`）
  - 六張狀態卡：整體狀態、引擎行程、即時保護、篡改保護、IFEO 引擎封鎖、相容性檢查
  - 服務狀態表（WinDefend / WdNisSvc / WdFilter / SecurityHealthService）
  - 單一「停用 / 啟用」切換按鈕，操作前有確認對話框
  - 內建執行記錄面板，並同步寫入 `%LOCALAPPDATA%\WinDefendOFF\windefendoff.log`
  - 應用程式圖示（`assets/WinDefendOFF.ico`，由 `build/IconGen` 程式化產生）

- **CLI 介面**（`WinDefendOFF.Cli.exe`）
  - `status` — 查詢 Defender 現況
  - `check` — 環境相容性檢查
  - `disable` — 停用 Defender
  - `enable` — 啟用 Defender

- **不需重新啟動電腦的即時切換**
  - 停用：關閉即時掃描 → IFEO 封鎖引擎載入 → 服務設為 Disabled → 停止服務
  - 啟用：移除 IFEO 封鎖 → 服務恢復預設啟動型態 → 移除群組原則 → 啟動服務

- **環境相容性檢查**
  - 系統架構、Windows 版本、管理員權限、Defender 服務存在性
  - 官方停用機制可用性（Set-MpPreference / IFEO / 服務 Start 值）
  - protected service 的 SERVICE_START 存取權確認

- **零第三方二進位**
  - 全部功能僅使用 Windows 官方機制，不內嵌任何第三方核心驅動程式
  - 可自由散播，無授權爭議

- **測試套件**
  - 45 個 xUnit 測試，涵蓋腳本生成、流程順序、步驟成敗判定、路徑正規化

- **發布流程**
  - `build/publish.ps1` — 支援 framework-dependent 與 self-contained 單檔發布
  - `-Zip` 參數產生可散播的壓縮檔
  - `build/WinDefendOFF.bat` — 自動 UAC 提權的啟動器

### 技術細節

- 受 WRP 保護的登錄檔位置以 `reg save` / `reg load` / `reg restore` 離線編輯
  （需 SeBackupPrivilege + SeRestorePrivilege，故透過一次性 SYSTEM 排程工作執行）
- 群組原則與 IFEO 改用 .NET Registry API：
  `reg.exe` 不支援含空格的鍵路徑（如 `Real-Time Protection`），且直接刪除
  `MsMpEng.exe` 的 IFEO 值會回報成功但實際未刪除

[1.0.0]: https://github.com/wesmar/WinDefCtl/releases/tag/v2.0.0

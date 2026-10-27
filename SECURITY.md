# 安全政策

## 回報漏洞

若發現安全問題，請透過 GitHub 的 Security Advisory 私下回報，
**不要**公開建立 issue。

預期 7 天內回覆。

---

## 支援範圍

| 項目 | 支援狀態 |
|---|---|
| Windows 11 x64 | ✅ 完整測試 |
| Windows 10 1809+ x64 | ✅ 支援 |
| Windows 10 32 位元 | ❌ 不支援（僅提供 x64） |
| Windows 7 / 8.1 | ❌ 不支援 |
| macOS / Linux | ❌ 不支援（Windows 專用） |

---

## 安全性設計

本工具會停用裝置的防毒軟體，因此本身的安全性特別重要。

### 零第三方二進位

本專案**不內嵌任何第三方核心驅動程式**，僅使用 Windows 官方機制：

- `Set-MpPreference -DisableRealtimeMonitoring`
- Image File Execution Options（IFEO）
- 服務 `Start` 登錄檔值

早期版本曾採用內嵌弱點核心驅動程式（BYOVD）以終結受 PPL 保護的行程，
但該方式有授權與散播問題，且實測顯示並非必要 —— 已於 1.0.0 移除。

### 最小權限

- 僅在偵測到受保護服務的 `SERVICE_START` 存取權時進行即時切換
- 群組原則與 IFEO 的寫入以管理員權限即可完成，不需額外提權
- 僅 WRP 保護的服務設定需要 SYSTEM 排程工作，且工作在完成後立即刪除

### 操作痕跡

- 所有變更寫入 `%LOCALAPPDATA%\WinDefendOFF\windefendoff.log`
- 每次操作都會在 GUI 顯示逐步結果
- 排程工作、暫存 hive、驅動檔案在操作後皆被清理

### 使用者告知

- GUI 在停用前顯示確認對話框，明確列出將被變更的項目
- README 與 PUBLISH 文件說明「停用後裝置完全沒有防護」

---

## 使用者責任

- 僅用於自有或已獲授權的裝置
- 停用期間請勿執行不可信程式
- 停用後應盡快重新啟用

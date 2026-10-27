# 第三方聲明

本專案實作下列開源專案所記載的技術。**未複製其原始碼**，技術為獨立實作。
此處提供出處與授權資訊以示歸屬。

---

## DefenderControl

- 專案：https://github.com/SkyN9ne/DefenderControl
- 授權：MIT
- 版權：Copyright (c) 2021-2022 qtKite

記載了本專案用來寫入 **Windows Resource Protection (WRP) 保護登錄檔** 的
`reg save` / `reg load` / `reg restore` 離線編輯手法。

---

## Microsoft Defender 官方文件

- https://learn.microsoft.com/microsoft.com/defender-endpoint/

記載了 Defender 的服務模型、篡改保護（Tamper Protection）行為，
以及 protected service 的存取限制。

---

## 附註：本專案不使用任何第三方二進位

本專案**未內嵌任何第三方核心驅動程式**，僅使用 Windows 官方機制：

- `Set-MpPreference -DisableRealtimeMonitoring`
- Image File Execution Options（IFEO）
- 服務 `Start` 登錄檔值

早期開發版本曾評估採用內嵌簽章弱點核心驅動程式（BYOVD）的作法，
但因授權不清（上游 repo 無 LICENSE）與散播時易被防毒誤判，
且實測證明並非必要，已於 1.0.0 移除。

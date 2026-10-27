@echo off
rem WinDefendOFF 啟動器
rem 若未以系統管理員身分執行，會自動透過 UAC 提權後啟動。

setlocal
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo 正在要求系統管理員權限...
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
        "Start-Process -FilePath '%~dp0WinDefendOFF.exe' -Verb RunAs"
) else (
    "%~dp0WinDefendOFF.exe"
)
endlocal

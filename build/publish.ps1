<#
.SYNOPSIS
    建置並打包 WinDefendOFF。

.DESCRIPTION
    產生兩種發行格式：

    1. framework-dependent（預設）
       需要目標電腦安裝 .NET 8 Desktop Runtime，檔案小、下載快。

    2. self-contained（-SelfContained）
       內含 .NET 執行階段，可攜式執行，不需另外安裝 runtime，
       適合直接散播給其他機器。

.PARAMETER SelfContained
    產生 self-contained 單檔執行檔（檔案較大，但無需安裝 runtime）。

.PARAMETER Version
    版本號，會套用至組���版本資訊。

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -SelfContained -Zip
#>
param(
  [string]$Configuration = 'Release',
  [string]$OutputDir = "$PSScriptRoot\..\publish",
  [switch]$SelfContained,
  [switch]$Zip,
  [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

Write-Step "建置 ($Configuration, Version $Version)"
dotnet build "$root\WinDefendOFF.sln" -c $Configuration -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "建置失敗" }

if (Test-Path $OutputDir) {
  # 先嘗試直接刪除；若被執行中的程式鎖住，改為發布到新目錄再覆蓋。
  try {
    Remove-Item $OutputDir -Recurse -Force -ErrorAction Stop
  } catch {
    Write-Warning "輸出目錄被鎖定，改為增量發布（舊檔案可能殘留，請關閉正在執行的 WinDefendOFF 後重新發布）。"
  }
}

$pubArgs = @(
  "-c", $Configuration, "-r", "win-x64",
  "-p:Version=$Version", "--nologo"
)
if ($SelfContained) {
  $pubArgs += @("--self-contained", "true")
  $pubArgs += @("-p:PublishSingleFile=true")
  $pubArgs += @("-p:IncludeNativeLibrariesForSelfExtract=true")
  $pubArgs += @("-p:EnableCompressionInSingleFile=true")
} else {
  $pubArgs += @("--self-contained", "false")
}

Write-Step "發布 GUI"
dotnet publish "$root\src\WinDefendOFF.App\WinDefendOFF.App.csproj" @pubArgs -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "GUI 發布失敗" }

Write-Step "發布 CLI"
dotnet publish "$root\src\WinDefendOFF.Cli\WinDefendOFF.Cli.csproj" @pubArgs -o "$OutputDir\cli"
if ($LASTEXITCODE -ne 0) { throw "CLI 發布失敗" }

# 附上說明文件與啟動批次檔
Copy-Item "$root\README.md" "$OutputDir\README.md" -Force
Copy-Item "$root\docs\TECHNICAL.md" "$OutputDir\TECHNICAL.md" -Force
Copy-Item "$root\docs\PUBLISH.md" "$OutputDir\PUBLISH.md" -Force
Copy-Item "$root\build\WinDefendOFF.bat" "$OutputDir\WinDefendOFF.bat" -Force

if ($Zip) {
  Write-Step "壓縮"
  $zipPath = Join-Path (Split-Path $OutputDir -Parent) "WinDefendOFF-$Version.zip"
  if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
  Compress-Archive -Path "$OutputDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
  Write-Host "  ZIP: $zipPath" -ForegroundColor Green
}

$size = [math]::Round((Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)

Write-Host ""
Write-Host "完成。" -ForegroundColor Green
Write-Host "  GUI      : $OutputDir\WinDefendOFF.exe"
Write-Host "  CLI      : $OutputDir\cli\WinDefendOFF.Cli.exe"
Write-Host "  發布類型 : $(if ($SelfContained) { 'self-contained（無需安裝 runtime）' } else { 'framework-dependent（需 .NET 8 Desktop Runtime）' })"
Write-Host "  總大小   : $size MB"
Write-Host ""
Write-Host "執行時需要系統管理員權限（會自動觸發 UAC 提權）。"

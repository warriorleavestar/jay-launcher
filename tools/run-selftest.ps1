<#
运行 Jay 的界面自检（开发用）：真实窗口里走一遍安装、启动、联机等流程，结果写进报告目录的 report.txt。
用法：powershell -NoProfile -ExecutionPolicy Bypass -File tools\run-selftest.ps1 -Report <报告目录> [-Configuration Release]
#>
param(
    [Parameter(Mandatory = $true)][string]$Report,
    [string]$Configuration = "Release",
    [int]$TimeoutSeconds = 1500
)

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo "src\Amber.App\bin\$Configuration\net10.0\Jay.exe"
if (-not (Test-Path $exe)) { Write-Host "找不到 $exe，先 dotnet build -c $Configuration"; exit 1 }
New-Item -ItemType Directory -Force $Report | Out-Null
$reportPath = (Resolve-Path $Report).Path
# The self-check changes settings, installs and launches: it gets its own settings folder and game folder inside the
# report folder, never this computer's own Jay settings or .minecraft. A caller's AMBER_DATA_DIR is kept.
$ownData = -not $env:AMBER_DATA_DIR
if ($ownData) {
    $data = Join-Path $reportPath "data"
    $game = Join-Path $reportPath "game"
    New-Item -ItemType Directory -Force $data, $game | Out-Null
    $settings = Join-Path $data "settings.json"
    # The first-run scope needs a brand-new settings folder; it picks its own test game folder.
    if (-not (Test-Path $settings) -and $env:AMBER_SELFTEST_SCOPE -ne "firstrun") {
        [System.IO.File]::WriteAllText($settings, (@{ GameDirectory = $game } | ConvertTo-Json))
    }
    $env:AMBER_DATA_DIR = $data
}
$env:AMBER_SELFTEST = $reportPath
$p = Start-Process -FilePath $exe -PassThru
$env:AMBER_SELFTEST = $null
if ($ownData) { $env:AMBER_DATA_DIR = $null }
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
    Write-Host "自检超时，结束 Jay"
    Stop-Process -Id $p.Id -Force
}
$reportFile = Join-Path $Report "report.txt"
if (Test-Path $reportFile) { Get-Content $reportFile -Encoding UTF8 } else { Write-Host "没有生成 report.txt" }

# install.ps1 — 安装 / 更新 Colugo 到 Grasshopper 库目录，并校验真的装上了
#
#   .\install.ps1           安全模式：发现 Rhino 在运行就停下来说明原因
#   .\install.ps1 -Force    结束 Rhino 进程后再装（会丢未保存内容，慎用）
#
# 为什么需要这个脚本：GH 会把已加载的 .gha 锁住，cp 会失败但不报错得很隐蔽，
# 结果就是"代码改了、重启了、没变化"。这个脚本把"检查 → 复制 → 校验 hash"合成一步。

param([switch]$Force)

$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot 'Colugo\bin\Release\net7.0-windows\Colugo.gha'
$dstDir = Join-Path $env:APPDATA 'Grasshopper\Libraries'
$dst = Join-Path $dstDir 'Colugo.gha'

if (-not (Test-Path $src)) {
    Write-Host "找不到 $src" -ForegroundColor Red
    Write-Host "先编译：cd gh\Colugo; dotnet build -c Release" -ForegroundColor Yellow
    exit 1
}

$rhino = Get-Process -Name Rhino -ErrorAction SilentlyContinue
if ($rhino) {
    $ids = ($rhino | Select-Object -ExpandProperty Id) -join ', '
    if (-not $Force) {
        Write-Host "Rhino 正在运行（PID: $ids），它锁着 Colugo.gha，安装一定失败。" -ForegroundColor Yellow
        Write-Host "关掉所有 Rhino 窗口后重跑本脚本。" -ForegroundColor Yellow
        Write-Host "确实要强装就加 -Force（会结束 Rhino，未保存内容会丢）。" -ForegroundColor DarkYellow
        exit 2
    }
    Write-Host "结束 Rhino: $ids" -ForegroundColor Yellow
    $rhino | Stop-Process -Force
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force -Path $dstDir | Out-Null
Copy-Item $src $dst -Force

$h1 = (Get-FileHash $src -Algorithm MD5).Hash
$h2 = (Get-FileHash $dst -Algorithm MD5).Hash
$len = (Get-Item $dst).Length

if ($h1 -ne $h2) {
    Write-Host "校验失败：复制后的文件与源不一致" -ForegroundColor Red
    exit 3
}

Write-Host "已安装 Colugo.gha  $len bytes  md5 $($h1.Substring(0,12))" -ForegroundColor Green
Write-Host "位置：$dst"
Write-Host "下一步：打开 Rhino → 输入 Grasshopper → 看 Colugo 标签页"

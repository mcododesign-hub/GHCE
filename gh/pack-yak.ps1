# pack-yak.ps1 — 把 Colugo 打成 Yak 包（Rhino PackageManager 可分发）
#
#   .\pack-yak.ps1            只打包：产物在 dist\colugo-<版本>-rh8_*_*.yak
#   .\pack-yak.ps1 -Push      打包并推送到 yak.rhino3d.com（需要先 yak login）
#
# 为什么单独一个脚本：手动拼目录最容易错两件事 —— 版本号和文件名对不上、
# 把 %APPDATA%\Grasshopper\Libraries 里那份**正在被 Rhino 锁住**的 .gha 拷进去。
# 这里版本号只从 csproj 读一处，文件只从 bin\Release 取。

param([switch]$Push)

$ErrorActionPreference = 'Stop'

$gh = $PSScriptRoot
$root = Split-Path $gh -Parent
# Rhino 不一定装在 C:（这台在 D:），从注册表读安装路径，别猜盘符
$rhino = (Get-ItemProperty 'HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install' -ErrorAction SilentlyContinue).InstallPath
if (-not $rhino) { $rhino = Join-Path $env:ProgramFiles 'Rhino 8\' }
$yak = Join-Path $rhino 'System\Yak.exe'
if (-not (Test-Path $yak)) { throw "找不到 Yak.exe：$yak" }

$csproj = Join-Path $gh 'Colugo\Colugo.csproj'
$ver = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $ver) { throw 'csproj 里读不到 <Version>' }

$src = Join-Path $gh 'Colugo\bin\Release\net7.0-windows\Colugo.gha'
if (-not (Test-Path $src)) {
    throw "找不到 $src。先编译：cd gh\Colugo; dotnet build -c Release"
}

$stage = Join-Path $root 'dist\Colugo'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item $src (Join-Path $stage 'Colugo.gha')

# 试用案例随包分发：仓库根目录有 examples\*.gh 就一起打进去。
# 它们会被装到 <packages>\Colugo\<版本>\samples\，GH 不会自动加载，需要用户手动打开。
$samples = Join-Path $root 'examples'
if (Test-Path $samples) {
    $ghFiles = Get-ChildItem $samples -Filter *.gh -File
    if ($ghFiles.Count -gt 0) {
        $dest = Join-Path $stage 'samples'
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        $ghFiles | Copy-Item -Destination $dest
        Write-Host "随包附带 $($ghFiles.Count) 个样例：$($ghFiles.Name -join ', ')" -ForegroundColor Cyan
    }
}

$manifest = @"
---
name: Colugo
version: $ver
authors:
- mcodo design
description: >
  Grasshopper Creative Coding Engine. Author geometry, style, time and runtime
  expressions in Grasshopper, execute them in a browser runtime: 2D canvas,
  entities with state, tracks and morph frames, event-driven variables, and a
  three.js scene. The preview opens in an embedded window inside Rhino, and the
  artwork exports to PNG, SVG, image sequence, video or a standalone HTML file.
url: https://github.com/mcododesign-hub/Colugo
keywords:
- grasshopper
- creative coding
- generative art
- animation
- three.js
"@

# 不带 BOM：YAML 开头的 --- 前面多三个字节会让解析器认不出文档起点
[System.IO.File]::WriteAllText(
    (Join-Path $stage 'manifest.yml'),
    $manifest,
    (New-Object System.Text.UTF8Encoding($false)))

Push-Location $stage
try {
    # yak 会把 [warn] 写进 stderr，而脚本开头设了 ErrorActionPreference='Stop'，
    # 于是"正常但带警告"的构建会被当成异常抛掉 —— 表现是"yak build 没有产出 .yak"，
    # 但手动跑同一条命令却是成功的。这里临时放开。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & $yak build 2>&1
    $ErrorActionPreference = $prevEap
} finally {
    Pop-Location
}
$out | ForEach-Object { Write-Host $_ }

$yakFile = Get-ChildItem $stage -Filter *.yak | Select-Object -First 1
if (-not $yakFile) { throw 'yak build 没有产出 .yak' }

$target = Join-Path (Join-Path $root 'dist') $yakFile.Name
Copy-Item $yakFile.FullName $target -Force
Remove-Item $yakFile.FullName -Force

Write-Host ""
Write-Host "包已生成：$target" -ForegroundColor Green
Write-Host "大小：$([math]::Round((Get-Item $target).Length / 1MB, 2)) MB"

if ($Push) {
    Write-Host "推送到 yak.rhino3d.com ..." -ForegroundColor Yellow
    & $yak push $target
    if ($LASTEXITCODE -ne 0) { throw 'yak push 失败（先跑 yak login）' }
    Write-Host "已推送。PackageManager 里几分钟内可见。" -ForegroundColor Green
} else {
    Write-Host "未推送。要发布：.\pack-yak.ps1 -Push（先 yak login）" -ForegroundColor Yellow
}

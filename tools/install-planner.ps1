#Requires -Version 7
<#
.SYNOPSIS
  构建并安装 sdemo.planner 到 DesktopAssembly 的 apps 目录。
.DESCRIPTION
  payload 里放 Release 发布产物；app.json / widget.json 复制到应用根目录，
  并按实际 exe 重算 sha256（宿主启动前会校验，占位 0 会被判 APP_INVALID）。
#>
param(
    [string]$AppsRoot = (Join-Path $env:LOCALAPPDATA 'SDemo\DesktopAssembly\apps'),
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$target = Join-Path $AppsRoot 'sdemo.planner'
$payload = Join-Path $target 'payload'

New-Item -ItemType Directory -Force -Path $payload | Out-Null

if (-not $SkipBuild) {
    Write-Host '▸ dotnet publish SDemo.Planner (Release)'
    dotnet publish (Join-Path $repo 'src\SDemo.App.Planner.Host\SDemo.App.Planner.Host.csproj') `
        -c Release -o $payload --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（exit $LASTEXITCODE）" }
}

$exe = Join-Path $payload 'SDemo.Planner.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe；先不加 -SkipBuild 跑一次。" }

Copy-Item (Join-Path $repo 'deploy\app.json') $target -Force
Copy-Item (Join-Path $repo 'deploy\widget.json') $target -Force

$appPath = Join-Path $target 'app.json'
$app = Get-Content $appPath -Raw -Encoding UTF8 | ConvertFrom-Json
$app.sha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
$app | ConvertTo-Json -Depth 8 | Set-Content $appPath -Encoding UTF8

Write-Host "✓ 已安装到 $target"
Write-Host "  exe    : $exe"
Write-Host "  sha256 : $($app.sha256)"
Write-Host ''
Write-Host '校验清单：'
Write-Host "  pwsh $(Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\smoke-mcp.ps1')"

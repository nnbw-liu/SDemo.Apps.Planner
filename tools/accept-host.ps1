#Requires -Version 7
<#
.SYNOPSIS
  宿主联调验收：启动 DesktopAssembly → 发现已安装的 sdemo.planner → ensure → 主窗口显示 → MCP 可调。
.DESCRIPTION
  只读检查（tools/list、todo_list），不改真实待办数据；结束后优雅退出 planner，并清理本次拉起的进程。
  依赖同仓的 DesktopAssembly 已构建：DesktopAssembly/src/SDemo.Shell.Host/bin/.../SDemo.Shell.exe。
#>
param(
    [string]$ShellExe = '',
    [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path $PSScriptRoot -Parent
$mainRepo = Split-Path (Split-Path $repo -Parent) -Parent
if (-not $ShellExe) {
    $ShellExe = Join-Path $mainRepo 'DesktopAssembly\src\SDemo.Shell.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Shell.exe'
}
if (-not (Test-Path $ShellExe)) { throw "找不到宿主 $ShellExe；先在 DesktopAssembly 里 dotnet build -c Release。" }

$appsRoot = Join-Path $env:APPDATA 'SDemo\DesktopAssembly\apps'
$installed = Join-Path $appsRoot 'sdemo.planner\app.json'
if (-not (Test-Path $installed)) { throw "sdemo.planner 未安装到 $appsRoot；先跑 tools\install-planner.ps1。" }

$runtimeDir = Join-Path $env:LOCALAPPDATA 'SDemo\DesktopAssembly\runtime'
$hostJsonPath = Join-Path $runtimeDir 'host.json'
$clientTokenPath = Join-Path $runtimeDir 'client-token'
$plannerRuntimePath = Join-Path $runtimeDir 'sdemo.planner.json'

if (Test-Path $hostJsonPath) {
    $old = Get-Content $hostJsonPath -Raw | ConvertFrom-Json
    if ($old.pid -and (Get-Process -Id $old.pid -ErrorAction SilentlyContinue)) {
        throw "宿主已在运行（pid=$($old.pid)），先退出托盘程序。"
    }
}

if (Test-Path $plannerRuntimePath) {
    $old = Get-Content $plannerRuntimePath -Raw | ConvertFrom-Json
    if ($old.pid -and (Get-Process -Id $old.pid -ErrorAction SilentlyContinue)) {
        throw "sdemo.planner 已在运行（pid=$($old.pid)），先退出。"
    }
}

function Wait-For([scriptblock]$Condition, [string]$What, [int]$Seconds = $TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 200
    }
    throw "等待超时：$What"
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "断言失败：$Message" }
    Write-Host "  ✓ $Message"
}

$beforePids = @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
$hostProcess = $null

try {
    Write-Host '▸ 启动宿主'
    $hostProcess = Start-Process -FilePath $ShellExe -PassThru
    Wait-For { Test-Path $hostJsonPath } 'host.json'

    $hostState = Get-Content $hostJsonPath -Raw | ConvertFrom-Json
    $clientToken = (Get-Content $clientTokenPath -Raw).Trim()
    $hostBase = "http://127.0.0.1:$($hostState.port)"
    $hostHeaders = @{ Authorization = "Bearer $clientToken" }

    Write-Host "  宿主 pid=$($hostState.pid) port=$($hostState.port)"
    Assert-True ((Invoke-RestMethod "$hostBase/v1/host" -Headers $hostHeaders).hostVersion -ne $null) 'GET /v1/host 可用'

    Write-Host '▸ 发现应用'
    $apps = Invoke-RestMethod "$hostBase/v1/apps" -Headers $hostHeaders
    $planner = $apps.apps | Where-Object { $_.appId -eq 'sdemo.planner' }
    if (-not $planner) { throw '宿主没有发现 sdemo.planner（检查 apps 目录与 app.json）。' }

    Write-Host "  state=$($planner.state) enabled=$($planner.enabled) hashValid=$($planner.hashValid)"
    Assert-True ([bool]$planner.enabled) 'app.json 被发现且 enabled=true'
    Assert-True ([bool]$planner.hashValid) 'sha256 校验通过'

    Write-Host '▸ ensure（宿主拉起应用）'
    $ensure = Invoke-RestMethod "$hostBase/v1/apps/sdemo.planner/ensure" -Method Post -Headers $hostHeaders `
        -ContentType 'application/json' -Body (@{ timeoutMs = 10000 } | ConvertTo-Json -Compress)
    Assert-True ($ensure.state -eq 'ready') 'ensure 返回 ready'
    Assert-True ($ensure.runtime.pid -gt 0) 'ensure 返回 pid'
    Assert-True ([bool](Get-Process -Id $ensure.runtime.pid -ErrorAction SilentlyContinue)) '应用进程存活'

    Wait-For { Test-Path $plannerRuntimePath } 'planner runtime.json'
    $appRuntime = Get-Content $plannerRuntimePath -Raw | ConvertFrom-Json
    Assert-True ($appRuntime.pid -eq $ensure.runtime.pid) 'runtime.json 的 pid 与 ensure 一致'

    Write-Host '▸ 应用面 / MCP'
    $appHeaders = @{ Authorization = "Bearer $($appRuntime.token)" }
    $appBase = "http://127.0.0.1:$($appRuntime.port)"
    $health = Invoke-RestMethod "$appBase/v1/health"
    Assert-True ($health.ok -eq $true) 'GET /v1/health 返回 ok'

    $tools = Invoke-RestMethod "$appBase/mcp" -Method Post -Headers $appHeaders -ContentType 'application/json' `
        -Body '{"jsonrpc":"2.0","id":"1","method":"tools/list"}'
    Assert-True ($tools.result.tools.Count -eq 13) 'MCP tools/list 返回 13 个工具'

    $list = Invoke-RestMethod "$appBase/mcp" -Method Post -Headers $appHeaders -ContentType 'application/json' `
        -Body '{"jsonrpc":"2.0","id":"2","method":"tools/call","params":{"name":"todo_list","arguments":{"limit":5}}}'
    Assert-True ($list.result.structuredContent.ok -eq $true) 'todo_list 调用成功'

    Write-Host '▸ 主窗口'
    Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SdemoPlannerFindWindow
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lparam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    public static IntPtr FindVisibleByTitle(string title)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            if (sb.ToString() == title) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
    Wait-For { [SdemoPlannerFindWindow]::FindVisibleByTitle('事序 · 项目与日常') -ne [IntPtr]::Zero } `
        '主窗口「事序 · 项目与日常」' 15
    $mainHwnd = [SdemoPlannerFindWindow]::FindVisibleByTitle('事序 · 项目与日常')
    Assert-True ($mainHwnd -ne [IntPtr]::Zero) '宿主拉起后主窗口可见'

    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SdemoPlannerWindowMessage
{
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
"@
    [SdemoPlannerWindowMessage]::PostMessage([IntPtr]$mainHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 500
    Wait-For { [SdemoPlannerFindWindow]::FindVisibleByTitle('事序 · 项目与日常') -eq [IntPtr]::Zero } '主窗口收进托盘'
    Assert-True ((Invoke-RestMethod "$appBase/v1/health").ok -eq $true) '关闭主窗口后应用仍存活'

    Write-Host ''
    Write-Host '✓ 宿主联调通过：发现 → ensure → 主窗口 → MCP' -ForegroundColor Green
}
finally {
    if (Test-Path $plannerRuntimePath) {
        try {
            $appRuntime = Get-Content $plannerRuntimePath -Raw | ConvertFrom-Json
            if ($appRuntime.port -and $appRuntime.token) {
                Invoke-RestMethod "http://127.0.0.1:$($appRuntime.port)/v1/shutdown" -Method Post `
                    -Headers @{ Authorization = "Bearer $($appRuntime.token)" } `
                    -ContentType 'application/json' -Body '{"graceMs":2000}' -TimeoutSec 5 | Out-Null
            }
        }
        catch {
            # 已退出或宿主先收走，忽略。
        }
    }

    Start-Sleep -Milliseconds 400
    if ($hostProcess -and -not $hostProcess.HasExited) {
        Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue
    }

    # 清掉本次宿主拉起的应用进程（保留测试前就在运行的）。
    Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
        if ($beforePids -notcontains $_.Id) {
            try {
                if ($_.Path -and $_.Path.StartsWith($appsRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
                # 受保护进程拿不到 Path。
            }
        }
    }

    Remove-Item $hostJsonPath, $clientTokenPath -Force -ErrorAction SilentlyContinue
    Write-Host '已清理：宿主 / 本次应用进程 / host runtime。'
}

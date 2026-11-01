#Requires -Version 7
<#
.SYNOPSIS
  日历拖拽改期冒烟（临时库）：建一条未排期待办 → 切到月历 → 把卡片拖到「2 号」→ 断言 dueAt 变成该月 2 号 09:00。
.DESCRIPTION
  用 Win32 鼠标事件模拟真实指针拖拽（不是直接调服务），因此能覆盖 WPF DragDrop/命中测试。
  不碰真实数据；结束优雅退出并清理临时库。
#>
param(
    [string]$ExePath = '',
    [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $ExePath) {
    $ExePath = Join-Path $repo 'src\SDemo.App.Planner.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Planner.exe'
}
if (-not (Test-Path $ExePath)) { throw "找不到 $ExePath；先 dotnet build -c Release" }

$runtimePath = Join-Path $env:LOCALAPPDATA 'SDemo\DesktopAssembly\runtime\sdemo.planner.json'
if (Test-Path $runtimePath) {
    $old = Get-Content $runtimePath -Raw | ConvertFrom-Json
    if ($old.pid -and (Get-Process -Id $old.pid -ErrorAction SilentlyContinue)) {
        throw "sdemo.planner 已在运行（pid=$($old.pid)），先退出。"
    }
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "断言失败：$Message" }
    Write-Host "  ✓ $Message"
}

$dir = Join-Path $env:TEMP ('planner-drag-' + [guid]::NewGuid().ToString('N'))
$db = Join-Path $dir 'planner.db'
New-Item -ItemType Directory -Force $dir | Out-Null
$process = $null

try {
    $instance = 'drag-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $process = Start-Process $ExePath -ArgumentList @("--db=$db", "--instance=$instance") -PassThru

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $runtime = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $runtimePath) {
            try {
                $candidate = Get-Content $runtimePath -Raw | ConvertFrom-Json
                if ($candidate.pid -eq $process.Id) { $runtime = $candidate; break }
            }
            catch {}
        }
        if ($process.HasExited) { throw "进程提前退出（exit $($process.ExitCode)）" }
        Start-Sleep -Milliseconds 150
    }
    if (-not $runtime) { throw '等待 runtime.json 超时' }

    $base = "http://127.0.0.1:$($runtime.port)"
    $headers = @{ Authorization = "Bearer $($runtime.token)" }

    $create = Invoke-RestMethod "$base/mcp" -Method Post -Headers $headers -ContentType 'application/json' `
        -Body (@{ jsonrpc = '2.0'; id = '1'; method = 'tools/call'; params = @{
            name = 'todo_create'; arguments = @{ items = @(@{ title = '拖拽验证待办' }) } } } |
            ConvertTo-Json -Depth 8 -Compress)
    $id = $create.result.structuredContent.data.items[0].id
    Start-Sleep -Milliseconds 500

    Write-Host '▸ 切到月历并定位拖拽源/目标'
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '待办与日历 · sdemo.planner')
    $window = $null
    for ($i = 0; $i -lt 50; $i++) {
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
        if ($null -ne $window) { break }
        Start-Sleep -Milliseconds 200
    }
    Assert-True ($null -ne $window) '主窗口存在'

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $calendarNav = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition) |
        Where-Object { $_.Current.Name -match '日历' } | Select-Object -First 1
    Assert-True ($null -ne $calendarNav) '日历导航按钮存在'
    $calendarNav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800

    $listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $sourceItem = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition) |
        Where-Object { $_.Current.Name -match '拖拽验证待办' } | Select-Object -First 1
    Assert-True ($null -ne $sourceItem) '未排期列表里有待办卡片'
    $sourceRect = $sourceItem.Current.BoundingRectangle

    $textCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $dayCell = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition) |
        Where-Object {
            $_.Current.Name -eq '2' -and
            $_.Current.BoundingRectangle.X -gt $window.Current.BoundingRectangle.X + 300 -and
            $_.Current.BoundingRectangle.Y -gt $window.Current.BoundingRectangle.Y + 120 -and
            $_.Current.BoundingRectangle.Y -lt $window.Current.BoundingRectangle.Y + $window.Current.BoundingRectangle.Height - 20
        } | Select-Object -First 1
    Assert-True ($null -ne $dayCell) '找到目标日期格（2 号）'
    $targetRect = $dayCell.Current.BoundingRectangle

    Write-Host "  源=($([int]$sourceRect.X),$([int]$sourceRect.Y)) 目标=($([int]$targetRect.X),$([int]$targetRect.Y))"

    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class SdemoPlannerCalendarDrag
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    public static void Drag(IntPtr hwnd, int x1, int y1, int x2, int y2)
    {
        SetForegroundWindow(hwnd);
        Thread.Sleep(300);
        SetCursorPos(x1, y1);
        Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(150);
        for (int i = 1; i <= 25; i++)
        {
            SetCursorPos(x1 + (x2 - x1) * i / 25, y1 + (y2 - y1) * i / 25);
            Thread.Sleep(40);
        }
        Thread.Sleep(200);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(500);
    }
}
"@

    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    [SdemoPlannerCalendarDrag]::Drag($hwnd,
        [int]($sourceRect.X + $sourceRect.Width / 2), [int]($sourceRect.Y + $sourceRect.Height / 2),
        [int]($targetRect.X + $targetRect.Width / 2), [int]($targetRect.Y + $targetRect.Height / 2))

    Write-Host '▸ 校验 dueAt'
    $expected = Get-Date -Year (Get-Date).Year -Month (Get-Date).Month -Day 2 -Hour 9 -Minute 0 -Second 0
    $get = Invoke-RestMethod "$base/mcp" -Method Post -Headers $headers -ContentType 'application/json' `
        -Body (@{ jsonrpc = '2.0'; id = '2'; method = 'tools/call'; params = @{
            name = 'todo_get'; arguments = @{ id = $id } } } | ConvertTo-Json -Depth 8 -Compress)
    $dueAt = $get.result.structuredContent.data.item.dueAt
    Assert-True ($null -ne $dueAt) '拖拽后 dueAt 已写入'
    $local = ([datetimeoffset]$dueAt).LocalDateTime
    Assert-True ($local.Date -eq $expected.Date -and $local.Hour -eq 9) "dueAt=$($local.ToString('yyyy-MM-dd HH:mm'))（预期 $($expected.ToString('yyyy-MM-dd')) 09:00）"

    Invoke-RestMethod "$base/v1/shutdown" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}' | Out-Null
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw 'shutdown 后进程未退出' }
    Assert-True ($process.ExitCode -eq 0) "进程退出码 $($process.ExitCode)"

    Write-Host ''
    Write-Host '✓ 日历拖拽冒烟通过' -ForegroundColor Green
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    if (Test-Path $runtimePath) {
        try {
            $tail = Get-Content $runtimePath -Raw | ConvertFrom-Json
            if ($tail.pid -and -not (Get-Process -Id $tail.pid -ErrorAction SilentlyContinue)) {
                Remove-Item $runtimePath -Force -ErrorAction SilentlyContinue
            }
        }
        catch { Remove-Item $runtimePath -Force -ErrorAction SilentlyContinue }
    }
}

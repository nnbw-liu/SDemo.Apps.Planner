#Requires -Version 7
<#
.SYNOPSIS
  组件 UI 冒烟（临时库）：UIA 在快速新增框输入 → 点「添加」→ 点 ○ 完成 → 用 MCP 断言结果。
.DESCRIPTION
  不碰真实数据；启动 --widget 模式（只组件，无主窗口），结束后优雅退出。
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

$dir = Join-Path $env:TEMP ('planner-widget-' + [guid]::NewGuid().ToString('N'))
$db = Join-Path $dir 'planner.db'
New-Item -ItemType Directory -Force $dir | Out-Null
$process = $null

try {
    $instance = 'widget-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $process = Start-Process $ExePath -ArgumentList @('--widget', "--instance=$instance", "--db=$db") -PassThru

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

    Write-Host '▸ 组件窗口'
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '待办组件')
    $widget = $null
    for ($i = 0; $i -lt 50; $i++) {
        $widget = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
        if ($widget) { break }
        Start-Sleep -Milliseconds 200
    }
    Assert-True ($null -ne $widget) '组件窗口存在'

    Write-Host '▸ UIA 输入并添加'
    $editCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edit = $widget.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    Assert-True ($null -ne $edit) '快速新增输入框存在'
    $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('组件快速新增验证')

    $buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $addButton = $widget.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition) |
        Where-Object { $_.Current.Name -eq '添加' } | Select-Object -First 1
    Assert-True ($null -ne $addButton) '「添加」按钮存在'
    $addButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800

    $listed = Invoke-RestMethod "$base/mcp" -Method Post -Headers $headers -ContentType 'application/json' `
        -Body '{"jsonrpc":"2.0","id":"1","method":"tools/call","params":{"name":"todo_list","arguments":{"limit":10}}}'
    $titles = @($listed.result.structuredContent.data.items | ForEach-Object { $_.title })
    Assert-True ($titles -contains '组件快速新增验证') '组件添加的待办落库并可被 MCP 读取'

    Write-Host '▸ 点 ○ 完成'
    $toggle = $null
    for ($i = 0; $i -lt 10; $i++) {
        $toggle = $widget.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition) |
            Where-Object { $_.Current.Name -eq '○' } | Select-Object -First 1
        if ($toggle) { break }
        Start-Sleep -Milliseconds 200
    }
    Assert-True ($null -ne $toggle) '待办行上有 ○ 按钮'
    $toggle.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $item = $listed.result.structuredContent.data.items | Where-Object { $_.title -eq '组件快速新增验证' } | Select-Object -First 1
    $get = $null
    for ($i = 0; $i -lt 15; $i++) {
        $get = Invoke-RestMethod "$base/mcp" -Method Post -Headers $headers -ContentType 'application/json' `
            -Body (@{ jsonrpc = '2.0'; id = '2'; method = 'tools/call'; params = @{ name = 'todo_get'; arguments = @{ id = $item.id } } } | ConvertTo-Json -Depth 6 -Compress)
        if ($get.result.structuredContent.data.item.status -eq 'done') { break }
        Start-Sleep -Milliseconds 300
    }
    Assert-True ($get.result.structuredContent.data.item.status -eq 'done') '点 ○ 后状态变为 done'

    Invoke-RestMethod "$base/v1/shutdown" -Method Post -Headers $headers -ContentType 'application/json' -Body '{}' | Out-Null
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw 'shutdown 后进程未退出' }
    Assert-True ($process.ExitCode -eq 0) "进程退出码 $($process.ExitCode)"

    Write-Host ''
    Write-Host '✓ 组件 UI 冒烟通过' -ForegroundColor Green
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

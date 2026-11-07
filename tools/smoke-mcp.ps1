#Requires -Version 7
<#
.SYNOPSIS
  sdemo.planner MCP 冒烟：headless 启动 → 读 runtime.json → 走真实 HTTP 调工具 → 优雅退出。
.DESCRIPTION
  覆盖验收要求的最小闭环：initialize、tools/list（13 个）、todo_create、todo_list、
  todo_complete、todo_search、todo_plan_from_text + todo_accept_draft、todo_stats，
  以及「没有 token 时 401」。
  使用临时库，不碰 %APPDATA% 里的真实数据。
#>
param(
    [string]$ExePath = '',
    [int]$TimeoutSeconds = 25,
    [switch]$KeepDatabase
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
if (-not $ExePath) {
    $ExePath = Join-Path $repo 'src\SDemo.App.Planner.Host\bin\Release\net10.0-windows10.0.19041.0\SDemo.Planner.exe'
}
if (-not (Test-Path $ExePath)) { throw "找不到可执行文件 $ExePath；先 dotnet build -c Release" }

$runtimePath = Join-Path $env:LOCALAPPDATA 'SDemo\DesktopAssembly\runtime\sdemo.planner.json'
if (Test-Path $runtimePath) {
    $existing = Get-Content $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($existing.pid -and (Get-Process -Id $existing.pid -ErrorAction SilentlyContinue)) {
        throw "sdemo.planner 已在运行（pid=$($existing.pid)），先退出或关掉主窗口。"
    }

    Remove-Item $runtimePath -Force
}

$dbDir = Join-Path $env:TEMP ('sdemo-planner-smoke-' + [guid]::NewGuid().ToString('N'))
$dbPath = Join-Path $dbDir 'planner.db'
$instance = 'smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8)

$process = $null
try {
    Write-Host "▸ 启动 headless（db=$dbPath）"
    $process = Start-Process -FilePath $ExePath `
        -ArgumentList @('--headless', "--instance=$instance", "--db=$dbPath") `
        -PassThru

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $runtime = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $runtimePath) {
            try {
                $candidate = Get-Content $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($candidate.pid -eq $process.Id) { $runtime = $candidate; break }
            }
            catch {
                # runtime.json 写入的瞬间可能还没写完，重试。
            }
        }

        if ($process.HasExited) { throw "进程提前退出（exit $($process.ExitCode)）" }
        Start-Sleep -Milliseconds 150
    }

    if (-not $runtime) { throw "等待 runtime.json 超时" }

    $base = "http://127.0.0.1:$($runtime.port)"
    $token = $runtime.token
    $headers = @{ Authorization = "Bearer $token" }

    function Invoke-Mcp {
        param([string]$Method, [object]$Params = $null, [string]$Id = '1')

        $payload = @{ jsonrpc = '2.0'; id = $Id; method = $Method }
        if ($null -ne $Params) { $payload.params = $Params }

        $response = Invoke-RestMethod -Uri "$base/mcp" -Method Post -Headers $headers `
            -ContentType 'application/json' -Body ($payload | ConvertTo-Json -Depth 12 -Compress)
        if ($response.error) { throw "JSON-RPC error：$($response.error | ConvertTo-Json -Compress)" }
        return $response.result
    }

    function Assert-True {
        param([bool]$Condition, [string]$Message)
        if (-not $Condition) { throw "断言失败：$Message" }
        Write-Host "  ✓ $Message"
    }

    Write-Host '▸ 健康检查与鉴权'
    $health = Invoke-RestMethod -Uri "$base/v1/health" -Method Get
    Assert-True ($health.ok -eq $true) 'GET /v1/health 返回 ok'

    $unauthorized = Invoke-WebRequest -Uri "$base/mcp" -Method Post -ContentType 'application/json' `
        -Body '{"jsonrpc":"2.0","id":"0","method":"tools/list"}' -SkipHttpErrorCheck
    Assert-True ($unauthorized.StatusCode -eq 401) '无 token 调 /mcp 返回 401'

    Write-Host '▸ MCP 协议'
    $init = Invoke-Mcp -Method 'initialize' -Params @{ protocolVersion = '2025-06-18' }
    Assert-True ($init.protocolVersion -eq '2025-06-18') 'initialize 协商协议版本'
    Assert-True ($init.serverInfo.name -eq 'sdemo.planner') 'serverInfo.name 正确'

    $tools = Invoke-Mcp -Method 'tools/list' -Id '2'
    Assert-True ($tools.tools.Count -eq 13) 'tools/list 返回 13 个工具'
    Assert-True ($tools.tools[0]._meta.'sdemo/risk' -ne $null) '工具带 sdemo/risk 元数据'

    Write-Host '▸ 待办写读'
    $create = Invoke-Mcp -Method 'tools/call' -Id '3' -Params @{
        name = 'todo_create'
        arguments = @{
            items = @(@{ title = '修复登录接口'; dueAt = '2026-11-02T18:00:00+08:00'; priority = 3 })
            clientRequestId = 'smoke_' + [guid]::NewGuid().ToString('N')
        }
        _meta = @{ 'sdemo/sourceSessionId' = 'smoke'; 'sdemo/sourceExcerpt' = '冒烟测试' }
    }
    Assert-True ($create.structuredContent.ok -eq $true) 'todo_create 成功'
    $item = $create.structuredContent.data.items[0]
    Assert-True ($item.source -eq 'ai') '来源由服务端注入为 ai'
    $id = $item.id

    $listed = Invoke-Mcp -Method 'tools/call' -Id '4' -Params @{ name = 'todo_list'; arguments = @{ status = 'open' } }
    Assert-True ($listed.structuredContent.data.items.Count -ge 1) 'todo_list 能看到新条目'

    $complete = Invoke-Mcp -Method 'tools/call' -Id '5' -Params @{
        name = 'todo_complete'
        arguments = @{ id = $id; expectedRevision = 1 }
    }
    Assert-True ($complete.structuredContent.data.status -eq 'done') 'todo_complete 置为 done'
    Assert-True ($complete.structuredContent.data.revision -eq 2) '完成使 revision +1'

    $conflict = Invoke-Mcp -Method 'tools/call' -Id '6' -Params @{
        name = 'todo_update'
        arguments = @{ id = $id; expectedRevision = 1; patch = @{ title = '改个名' } }
    }
    Assert-True ($conflict.structuredContent.ok -eq $false) '过期 revision 返回失败'
    Assert-True ($conflict.structuredContent.code -eq 'TODO_CONFLICT') '冲突错误码为 TODO_CONFLICT'

    $search = Invoke-Mcp -Method 'tools/call' -Id '7' -Params @{ name = 'todo_search'; arguments = @{ q = '登录' } }
    Assert-True ($search.structuredContent.data.hits.Count -ge 1) '中文两字搜索命中'

    Write-Host '▸ 草稿流程'
    $plan = Invoke-Mcp -Method 'tools/call' -Id '8' -Params @{
        name = 'todo_plan_from_text'
        arguments = @{ text = "1. 整理客户名单`n2. 准备报价单" }
        _meta = @{ 'sdemo/sourceExcerpt' = '冒烟：拆草稿' }
    }
    Assert-True ($plan.structuredContent.data.items.Count -eq 2) '草稿拆出 2 条'
    $draftId = $plan.structuredContent.data.draftId
    $before = Invoke-Mcp -Method 'tools/call' -Id '9' -Params @{ name = 'todo_list'; arguments = @{ status = 'open' } }
    Assert-True ($before.structuredContent.data.items.Count -eq 0) '草稿未采纳前不写正式表'

    $accept = Invoke-Mcp -Method 'tools/call' -Id '10' -Params @{ name = 'todo_accept_draft'; arguments = @{ draftId = $draftId } }
    Assert-True ($accept.structuredContent.data.items.Count -eq 2) 'todo_accept_draft 写入 2 条'

    $stats = Invoke-Mcp -Method 'tools/call' -Id '11' -Params @{ name = 'todo_stats'; arguments = @{} }
    Assert-True ($stats.structuredContent.data.openTotal -eq 2) 'todo_stats 只统计 todo'

    Write-Host '▸ 优雅退出'
    Invoke-RestMethod -Uri "$base/v1/shutdown" -Method Post -Headers $headers `
        -ContentType 'application/json' -Body '{"graceMs":2000}' | Out-Null

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        throw 'shutdown 后进程未在超时内退出'
    }

    Assert-True ($process.ExitCode -eq 0) "进程退出码 $($process.ExitCode)"

    Write-Host ''
    Write-Host '✓ MCP 冒烟全部通过' -ForegroundColor Green
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(3000) | Out-Null
    }

    if (-not $KeepDatabase) {
        Remove-Item -Recurse -Force $dbDir -ErrorAction SilentlyContinue
    }

    if (Test-Path $runtimePath) {
        try {
            $tail = Get-Content $runtimePath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($tail.pid -and -not (Get-Process -Id $tail.pid -ErrorAction SilentlyContinue)) {
                Remove-Item $runtimePath -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            Remove-Item $runtimePath -Force -ErrorAction SilentlyContinue
        }
    }
}

# 组合矩阵验收：**v1 形状的客户端 × C# v2 主机**（Phase 7-① 的一格）
#
# 为什么单做这一格：`versions/compat-matrix.md` 矩阵一第 2 行是**硬要求** ——
# 「v1 客户端一行不改也能跑完整链路」，而它历史上有一个真实风险：
# v1 的协商比的是**应用版本**主版本，v2 主机若照旧执行，`1.1.0` 对 `2.0.0` 会**全部 426**。
# 实现宣称已按路径把 `/api/v1/*` 固定成主版本 1（`spec/12-provider.md` §6.1）——本脚本就是那句宣称的判据。
#
# 「v1 形状」= 与 `packages/quizsync_core/lib/client/api_client.dart` 发的头**逐字一致**：
#   `Authorization: Bearer <token>` + `X-QS-Client-Version: 1.0.0`
# （Dart 客户端的默认 appVersion 就是 `1.0.0`）。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify_matrix.ps1
# 退出码：0 = 全过；1 = 有 FAIL 或前置失败。

param(
    [int]$Port = 8799,
    [string]$Root = 'D:\DeepseekHarness\TemporaryConversationFile\matrix-v1-v2',
    # 宿主形态：`V2` = C# 主机（独立仓）；`Dart` = v1 参考宿主（quizsync_core 的 shelf 服务端）。
    # 客户端形状固定是 **v1**（与 Dart 客户端默认头逐字一致）—— 这一格要看的就是兼容层。
    [ValidateSet('V2', 'Dart')][string]$HostKind = 'V2'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:QS_DOTNET) { $env:QS_DOTNET } else { 'dotnet' }
$serverRepo = $repo
$serverDll = Join-Path $serverRepo 'src\QuizSync.Server.Cli\bin\Release\publish\QuizSync.Server.Cli.dll'
$v1 = '1.0.0'   # v1 客户端的应用版本（与 Dart 客户端默认值逐字一致）

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Red }
}

# 原生命令输出：**不要用 `2>$null`**（stderr 每行会变成 ErrorRecord，Stop 策略下第一行就终止脚本）。
function Native([string]$file, [string[]]$arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    # ⚠️ **给 curl 兜底加超时**：绝大部分调用点没写 `-m`，某一步挂住整个脚本就永远等下去
    #    （上一轮就是这样卡住的：服务端明明起来并在监听，脚本却不再前进）。
    #    在这里统一加，调用点一处都不用改。
    if ($file -eq 'curl.exe' -and -not ($arguments -contains '-m')) {
        $arguments = @('-m', '20') + $arguments
    }
    try { return ((& $file @arguments 2>&1 | Where-Object { $_ -is [string] }) | Out-String).Trim() }
    finally { $ErrorActionPreference = $prev }
}

# 带 v1 头的 GET / POST（`-D -` 用来读响应头，426 这类判据在头上）
function V1([string]$url, [string]$token = '', [string[]]$extra = @()) {
    $a = @('-s', '-D', '-', $url)
    if ($token) { $a += @('-H', "Authorization: Bearer $token") }
    $a += @('-H', "X-QS-Client-Version: $v1")
    $a += $extra
    return Native 'curl.exe' $a
}

function StatusOf([string]$raw) {
    $m = [regex]::Match($raw, 'HTTP/1\.1 (\d{3})')
    # ⚠️ 本机是 PowerShell 5.1：**不支持表达式形式的 `return if (…) {…} else {…}`**
    #    （与"没有三元运算符"同一条限制）。实测报 `if : The term 'if' is not recognized`。
    if ($m.Success) { return [int]$m.Groups[1].Value }
    return 0
}

function BodyOf([string]$raw) {
    # 头与体之间空行；体可能在多段（-D - 与重定向交织），取最后一个空行之后的全部
    $idx = $raw.LastIndexOf("`r`n`r`n")
    if ($idx -lt 0) { return '' }
    return $raw.Substring($idx + 4).Trim()
}

if (-not (Test-Path $serverDll)) { throw "找不到主机产物：$serverDll（先 dotnet publish）" }

Get-Process 'QuizSync.Server.Cli' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Root | Out-Null
$serverDir = Join-Path $Root 'server'
New-Item -ItemType Directory -Force -Path $serverDir | Out-Null

$server = $null
try {
    if ($HostKind -eq 'Dart') {
        # ---- Dart 宿主（v1 参考实现）：`packages/quizsync_core/tool/matrix_host.dart` ----
        Write-Host "`n=== 起 Dart 宿主（端口 $Port，数据目录临时）===" -ForegroundColor Cyan
        $dart = 'D:\Windows\Apps\flutter\flutter\bin\dart.bat'
        $coreDir = Join-Path $repo 'packages\quizsync_core'
        $outLog = Join-Path $Root 'server.out.log'
        $server = Start-Process -FilePath $dart `
            -ArgumentList @('run', 'tool/matrix_host.dart', "$Port", $serverDir) `
            -WorkingDirectory $coreDir -PassThru -NoNewWindow `
            -RedirectStandardOutput $outLog `
            -RedirectStandardError (Join-Path $Root 'server.err.log')

        # 就绪判据是它自己打印的那行 `READY port=… code=…`（Dart 宿主没有本机控制面，配对码只能从这里拿）。
        # ⚠️ **首次 `dart run` 要跑 build hooks**（实测 stderr 里两行 `Running build hooks...`），
        #    远超 30 秒 —— 上一轮就死在"就绪等待只有 30 秒"上。这里给 5 分钟。
        $readyLine = $null
        foreach ($attempt in 1..1000) {
            Start-Sleep -Milliseconds 300
            if (Test-Path $outLog) {
                $readyLine = (Get-Content $outLog | Where-Object { $_ -match '^READY ' } | Select-Object -First 1)
            }
            if ($readyLine) { break }
        }
        Check 'Dart 宿主打印 READY（含端口与配对码）' ($null -ne $readyLine) "$readyLine"
        if (-not $readyLine) { throw "Dart 宿主没起来（看 $Root\server.err.log）" }

        $code = [regex]::Match($readyLine, 'code=(\d{6})').Groups[1].Value
        Check '从 READY 行拿到 6 位配对码' ($code.Length -eq 6) "code=$code"
    }
    else {
        Write-Host "`n=== 起 C# v2 主机（端口 $Port，数据目录临时）===" -ForegroundColor Cyan
        $server = Start-Process -FilePath $dotnet `
            -ArgumentList @($serverDll, 'run', '--port', $Port, '--data', $serverDir) `
            -PassThru -NoNewWindow `
            -RedirectStandardOutput (Join-Path $Root 'server.out.log') `
            -RedirectStandardError (Join-Path $Root 'server.err.log')

        $ready = $false
        foreach ($attempt in 1..60) {
            Start-Sleep -Milliseconds 300
            if ((Native 'curl.exe' @('-s', '-m', '2', "http://127.0.0.1:$Port/health")) -match 'ok') { $ready = $true; break }
        }
        Check '主机就绪（/health）' $ready "端口 $Port"
        if (-not $ready) { throw "主机没起来（看 $Root\server.err.log）" }

        $control = (Get-Content (Join-Path $serverDir 'control.token')).Trim()
        $code = ((Native 'curl.exe' @('-s', '-H', "X-QS-Control: $control",
            "http://127.0.0.1:$Port/api/v1/pair/code")) | ConvertFrom-Json).code
        Check '控制面取到 6 位配对码' ($code -is [string] -and $code.Length -eq 6) "code=$code"
    }

    # ---- ① 兼容层的核心判据：v1 头 + /api/v1/* 必须**不是 426** ----
    $bodyDir = Join-Path $Root 'body'
    New-Item -ItemType Directory -Force -Path $bodyDir | Out-Null
    $pairJson = Join-Path $bodyDir 'pair.json'
    @{ code = $code; device_id = 'matrix-v1-client'; device_name = '矩阵·v1';
       platform = 'android'; app_version = $v1 } |
        ConvertTo-Json -Compress | Set-Content -Encoding UTF8 $pairJson

    $raw = V1 "http://127.0.0.1:$Port/api/v1/pair" '' @('-X', 'POST', '-H',
        'Content-Type: application/json; charset=utf-8', '--data-binary', "@$pairJson")
    $status = StatusOf $raw
    Check 'v1 头打 /api/v1/pair：**不是 426**（兼容层的硬要求）' ($status -eq 200) "HTTP $status"
    $token = ''
    try { $token = (BodyOf $raw | ConvertFrom-Json).token } catch { }
    Check 'v1 客户端拿到 64 位令牌' ($token -is [string] -and $token.Length -eq 64) "长度 $($token.Length)"

    # ---- ② v1 面**不加** Provider 字段（冻结契约）----
    $infoRaw = V1 "http://127.0.0.1:$Port/api/v1/info"
    $infoText = BodyOf $infoRaw
    Check 'v1 的 /info 不含 provider_online（v1 面一个字段都不加）' (-not ($infoText -match 'provider_online')) ''
    Check 'v1 的 /info 不含 provider_model' (-not ($infoText -match 'provider_model')) ''
    Check 'v1 的 /info 不含 v1_compat' (-not ($infoText -match 'v1_compat')) ''
    $info = $infoText | ConvertFrom-Json
    Check 'v1 的 /info 自述 protocol_version = 1' ($info.protocol_version -eq 1) "protocol_version=$($info.protocol_version)"

    # ---- ③ 宿主形态的差异：v2 面存在与否（**这本身就是矩阵要记录的一格**）----
    if ($HostKind -eq 'V2') {
        $v2Raw = Native 'curl.exe' @('-s', "http://127.0.0.1:$Port/api/v2/info")
        $v2 = $v2Raw | ConvertFrom-Json
        Check 'v2 的 /info 有 provider_online 与 v1_compat' `
            (($null -ne $v2.provider_online) -and ($v2.v1_compat -eq $true)) `
            "provider_online=$($v2.provider_online) v1_compat=$($v2.v1_compat)"
        Check 'v2 的 /info 自述 protocol_version = 2' ($v2.protocol_version -eq 2) "protocol_version=$($v2.protocol_version)"
    }
    else {
        # v1 宿主上打 `/api/v2/*` 不是"404 一句话"就完了：协议里 v1 的检查顺序是
        # **先鉴权、后版本、再路由** —— 匿名打过去得到的是 **401**，只有带令牌才会走到 404。
        # 第一次断言只写了 404，被 401 判红（**探针错**：少了鉴权这一层；产品完全符合协议）。
        $v2Anon = StatusOf (Native 'curl.exe' @('-s', '-D', '-', "http://127.0.0.1:$Port/api/v2/info"))
        Check 'v1 宿主对 /api/v2/info：匿名 → 401（先鉴权）' ($v2Anon -eq 401) "HTTP $v2Anon"

        $v2Authed = StatusOf (Native 'curl.exe' @('-s', '-D', '-', "http://127.0.0.1:$Port/api/v2/info",
            '-H', "Authorization: Bearer $token"))
        Check 'v1 宿主对 /api/v2/info：带令牌 → 普通 404（v2 客户端据此判定老主机）' `
            ($v2Authed -eq 404) "HTTP $v2Authed"
    }

    # ---- ④ v1 客户端跑完整链路：推 op（建合集）→ 切合集 → 上传 → 建任务 → 轮询终态 ----
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $opsJson = Join-Path $bodyDir 'ops.json'
    @{ ops = @(@{ op_id = [guid]::NewGuid().ToString(); device_id = 'matrix-v1-client'; lamport = 1
                  entity = 'collection'; entity_id = 'c-matrix'; op_type = 'upsert'
                  fields_json = @{ name = '矩阵合集'; created_at = $now; updated_at = $now; updated_by = 'matrix-v1-client' } }) } |
        ConvertTo-Json -Depth 6 -Compress | Set-Content -Encoding UTF8 $opsJson
    $push = (Native 'curl.exe' @('-s', '-X', 'POST', "http://127.0.0.1:$Port/api/v1/sync/ops",
        '-H', "Authorization: Bearer $token", '-H', "X-QS-Client-Version: $v1",
        '-H', 'Content-Type: application/json; charset=utf-8', '--data-binary', "@$opsJson")) | ConvertFrom-Json
    Check 'v1 客户端推 op 成功（applied=1）' ($push.applied -eq 1) "applied=$($push.applied) rejected=$($push.rejected)"

    $select = (Native 'curl.exe' @('-s', '-X', 'POST', "http://127.0.0.1:$Port/api/v1/collections/c-matrix/select",
        '-H', "Authorization: Bearer $token", '-H', "X-QS-Client-Version: $v1")) | ConvertFrom-Json
    Check 'v1 客户端切合集成功' ($select.active_collection_id -eq 'c-matrix') "active=$($select.active_collection_id)"

    # 上传：真 JPEG（用 System.Drawing 现生成，别再手搓字节 —— 上一轮就是这么栽的）
    $jpg = Join-Path $bodyDir 'probe.jpg'
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap 120, 120
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::White)
    $bmp.Save($jpg, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $g.Dispose(); $bmp.Dispose()
    $upload = (Native 'curl.exe' @('-s', '-X', 'POST', "http://127.0.0.1:$Port/api/v1/images",
        '-H', "Authorization: Bearer $token", '-H', "X-QS-Client-Version: $v1",
        '-F', "file=@$jpg;type=image/jpeg")) | ConvertFrom-Json
    $hash = $upload.image_hash
    Check 'v1 客户端上传成功（multipart）' ($hash -is [string] -and $hash.Length -eq 64) "hash=$($hash.Substring(0,12))…"

    $taskJson = Join-Path $bodyDir 'task.json'
    @{ task_id = 't-matrix'; image_hash = $hash; source_device = 'matrix-v1-client' } |
        ConvertTo-Json -Compress | Set-Content -Encoding UTF8 $taskJson
    $createRaw = Native 'curl.exe' @('-s', '-D', '-', '-X', 'POST', "http://127.0.0.1:$Port/api/v1/tasks",
        '-H', "Authorization: Bearer $token", '-H', "X-QS-Client-Version: $v1",
        '-H', 'Content-Type: application/json; charset=utf-8', '--data-binary', "@$taskJson")
    Check 'v1 客户端建任务返回 202' ((StatusOf $createRaw) -eq 202) "HTTP $(StatusOf $createRaw)"

    # 没有 Provider 时必须快速失败（这是 v1 客户端能看到的终态；顺带证明它拿得到明确错误码）
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $terminal = $null
    while ($sw.ElapsedMilliseconds -lt 8000) {
        $t = (Native 'curl.exe' @('-s', "http://127.0.0.1:$Port/api/v1/tasks/t-matrix",
            '-H', "Authorization: Bearer $token", '-H', "X-QS-Client-Version: $v1")) | ConvertFrom-Json
        if ($t.status -in @('done', 'failed', 'cancelled')) { $terminal = $t; break }
        Start-Sleep -Milliseconds 60
    }
    $sw.Stop()
    Check 'v1 客户端能等到终态' ($null -ne $terminal) $(if ($terminal) { "status=$($terminal.status) 用时 $($sw.ElapsedMilliseconds) ms" } else { '8 秒内没有终态' })
    if ($terminal) {
        if ($HostKind -eq 'V2') {
            Check '没有 Provider 时给的是明确错误码（不是假成功）' `
                (($terminal.status -eq 'failed') -and ($terminal.error_code -eq 'provider_offline')) `
                "status=$($terminal.status) error_code=$($terminal.error_code)"
        }
        else {
            # v1 宿主**没有 Provider 概念**：识别由宿主自己执行（这里注入的是读 fixture 的假 provider），
            # 所以同一发请求在这里是 `done` —— 这条差异正是矩阵存在的意义，如实断言而不是"两边都写成成功"。
            Check 'v1 宿主自己执行识别 → 终态 done（v1 无 Provider 概念）' `
                ($terminal.status -eq 'done') "status=$($terminal.status)"
            # 第一次这里断言的是 `question_count` —— **读错了字段**：它不在任务响应里
            # （题目数与题目在 `task_result` 的会话载荷里）。改断言"会话 id 非空"：
            # done 必然落了会话，链路走通就有它。
            Check 'v1 宿主落下了会话（done 必然有 session_id）' `
                (-not [string]::IsNullOrWhiteSpace($terminal.session_id)) "session_id=$($terminal.session_id)"
        }
    }

    # ---- ⑤ 拉 op：v1 客户端要把自己推的 op 看见（水位语义）----
    # ⚠️ 这个端点**必须带 `from_device`**（实现里缺了就回 400 `invalid_request`、ops 为空）；
    #    游标参数 `cursor` 覆盖 `since_lamport`（v1 行为）。第一次跑漏了 `from_device` 于是断言红了 ——
    #    又是「先怀疑探针、再怀疑产品」：服务端其实如实报了 invalid_request。
    $pulled = (Native 'curl.exe' @('-s',
        "http://127.0.0.1:$Port/api/v1/sync/ops?from_device=matrix-v1-client&since_lamport=0",
        '-H', "Authorization: Bearer $token", '-H', "X-QS-Client-Version: $v1")) | ConvertFrom-Json
    $ids = @($pulled.ops | ForEach-Object { $_.entity_id })
    Check 'v1 客户端能拉回自己推的合集 op' ($ids -contains 'c-matrix') "实体=$($ids -join ',')"
}
finally {
    # ⚠️ **不要写 `$server.Kill($true)`**：PowerShell 5.1 跑在 .NET Framework 上，
    #    `Process.Kill(bool)`（连子进程一起杀）是 .NET 5+ 才有的重载 —— 调用会抛，
    #    而上一版把它包在 `catch { }` 里**静默吞掉**：服务进程活到最后，它继承的
    #    `-RedirectStandardOutput` 句柄让外层命令永远等不到 EOF。于是脚本看起来"卡住"，
    #    其实整条链路早就跑完了（取证：临时目录里 4 个 body 文件在启动后 1 秒内就写齐了）。
    #    改用 `taskkill /T`（连带子进程），并且**失败要出声**——不许再吞。
    if ($server) {
        $killOutput = Native 'taskkill.exe' @('/T', '/F', '/PID', "$($server.Id)")
        if ($killOutput -notmatch 'SUCCESS|成功') {
            Write-Host ("  ⚠️ 收尾没杀掉主机进程 {0}：{1}" -f $server.Id, $killOutput) -ForegroundColor Yellow
        }
    }

    Get-Process 'QuizSync.Server.Cli', 'dart', 'dartaotruntime' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
Write-Host ("矩阵这一格 = v1 形状的客户端 × {0} 宿主" -f $(if ($HostKind -eq 'Dart') { 'Dart（v1 参考实现）' } else { 'C# v2 主机（走 /api/v1/* 兼容层）' }))
exit $(if ($fail -eq 0) { 0 } else { 1 })

# 离线 AI 桩：本机假 provider，用「真 HTTP + 真 OpenAI 信封」把**识别链路**整条跑通，
# 但**不出网、不花钱、结果固定**（端到端验收用）。
#
# 为什么要它：用户报的「识别完当前合集看不到 / 悬浮窗没内容 / 安卓也不同步」全都发生在
# 「识别成功之后」那一段。用真 AI 验证要等十几秒、要花 Key、还可能因为模型抽风而红，
# 于是这条链路的回归只能靠肉眼。这个桩把「AI 那一跳」换成确定的答案，剩下的每一步
# （剪贴板监听 → 分析引擎 → 落库 → 合集归属 → 推 op → 悬浮窗/主界面渲染）都是**真代码**。
#
# 用法（通常由 tools\verify_recognition_collection.ps1 拉起，也可以手动）：
#   powershell -ExecutionPolicy Bypass -File tools\fake_ai_provider.ps1 `
#       -Port 8799 -ReadyFile <文件> -CallLog <文件>
#
# 端点：`POST /v1/chat/completions`（OpenAI 兼容；与 `AiProviders.BuildRequest` 拼出来的 URL 一致）。
# 响应：标准信封 `choices[0].message.content` = 固定的两题 JSON（内容见下）。
# 退出：被 `Stop-Process` 杀掉即可（这是个桩，不做优雅退出）。
param(
    [int]$Port = 8799,
    [string]$ReadyFile,
    [string]$CallLog,
    [int]$MaxCalls = 0
)

$ErrorActionPreference = 'Stop'

# ---- 固定答案：两题单选，选项与答案自洽（不会触发「选项与答案不匹配」那类告警）----
$questions = @{
    questions = @(
        @{
            question_no = '1'
            type        = 'single'
            stem        = '「端到端桩题」世界上面积最大的洋是？'
            options     = @(
                @{ label = 'A'; text = '太平洋' },
                @{ label = 'B'; text = '大西洋' }
            )
            answer      = @{ choice = @('A') }
            analysis    = '太平洋面积约 1.65 亿平方公里，是最大的洋。'
            confidence  = 0.99
        },
        @{
            question_no = '2'
            type        = 'single'
            stem        = '「端到端桩题」中华人民共和国的首都是？'
            options     = @(
                @{ label = 'A'; text = '北京' },
                @{ label = 'B'; text = '上海' }
            )
            answer      = @{ choice = @('A') }
            analysis    = '北京是中华人民共和国的首都。'
            confidence  = 0.98
        }
    )
}

$content = $questions | ConvertTo-Json -Depth 10 -Compress
$envelope = @{
    id      = 'fake-completion-1'
    object  = 'chat.completion'
    created = 1
    model   = 'fake-model'
    choices = @(@{
            index         = 0
            finish_reason = 'stop'
            message       = @{ role = 'assistant'; content = $content }
        })
} | ConvertTo-Json -Depth 10 -Compress

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()

if ($ReadyFile) {
    [System.IO.File]::WriteAllText($ReadyFile, "ready $Port", (New-Object System.Text.UTF8Encoding($false)))
}

$calls = 0
try {
    while ($true) {
        $client = $listener.AcceptTcpClient()
        try {
            $stream = $client.GetStream()

            # 读请求：先读头（到 \r\n\r\n），再按 Content-Length 读满 body。
            # ⚠️ 不能只读一次：请求体是 base64 图片，可能上兆。
            $buffer = New-Object byte[] 65536
            $acc = New-Object System.IO.MemoryStream
            $headerEnd = -1
            $contentLength = 0
            while ($true) {
                $read = $stream.Read($buffer, 0, $buffer.Length)
                if ($read -le 0) { break }
                $acc.Write($buffer, 0, $read)
                $bytes = $acc.ToArray()
                if ($headerEnd -lt 0) {
                    $head = [System.Text.Encoding]::ASCII.GetString($bytes)
                    $idx = $head.IndexOf("`r`n`r`n")
                    if ($idx -ge 0) {
                        $headerEnd = $idx + 4
                        $m = [regex]::Match($head.Substring(0, $idx), '(?im)^Content-Length:\s*(\d+)')
                        if ($m.Success) { $contentLength = [int]$m.Groups[1].Value }
                    }
                }
                if ($headerEnd -ge 0 -and $bytes.Length -ge ($headerEnd + $contentLength)) { break }
            }

            $all = $acc.ToArray()
            $headText = [System.Text.Encoding]::ASCII.GetString($all, 0, [Math]::Min($all.Length, [Math]::Max($headerEnd, 1)))
            $firstLine = ($headText -split "`r`n")[0]
            $calls++

            if ($CallLog) {
                Add-Content -Path $CallLog -Encoding UTF8 -Value (
                    "call $calls : $firstLine : headerBytes=$headerEnd bodyBytes=$contentLength")
            }

            $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($envelope)
            $header = "HTTP/1.1 200 OK`r`n" +
                      "Content-Type: application/json; charset=utf-8`r`n" +
                      "Content-Length: $($bodyBytes.Length)`r`n" +
                      "Connection: close`r`n`r`n"
            $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
            $stream.Write($headerBytes, 0, $headerBytes.Length)
            $stream.Write($bodyBytes, 0, $bodyBytes.Length)
            $stream.Flush()
        }
        catch {
            if ($CallLog) { Add-Content -Path $CallLog -Encoding UTF8 -Value "call $calls : ERROR $($_.Exception.Message)" }
        }
        finally {
            $client.Close()
        }

        if ($MaxCalls -gt 0 -and $calls -ge $MaxCalls) { break }
    }
}
finally {
    $listener.Stop()
}

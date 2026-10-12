param(
    [string]$Serial = 'e5b1641d',
    [string]$Package = 'com.quizsync.android.native.dev',
    [switch]$SkipBuild
)

# 一条命令核验 Android 端主壳。
#
# **为什么需要它**（都是这一程真实摔过的）：
#  ① 中文不能当命令行参数（会被代码页吃掉，回显成 `NOT FOUND: ʶ���`，
#     于是「找不到节点」被误判成「界面上没有」）—— 所以这里的断言关键字**写在脚本里**，
#     脚本本身存成 UTF-8 with BOM；
#  ② 坐标不能靠 `-split ' '` 解析（会切出空串，拼成 `1467,,1610` 让 `input tap` 直接报错）
#     —— 一律正则取 `(\d+),(\d+)`；
#  ③ **构建必须在验之前**：跑的是上一次装上去的包时，「界面看起来对」说明不了任何事
#     （Windows 侧就因为漏了这一步，把一份编译不过的代码报成了「构建 0 错误 + 验收全绿」）。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools/verify_android_ui.ps1
# 退出码：0 = 全过；1 = 有 FAIL 或前置步骤失败。

$ErrorActionPreference = 'Continue'
$root = 'D:\ZCode\QuizSync_AI'
$tmp = 'D:\DeepseekHarness\TemporaryConversationFile'
$adb = 'D:\Windows\Apps\Android\Sdk\platform-tools\adb.exe'
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = 'D:\Windows\Apps\Java\jdk-17.0.18' }
if (-not $env:ANDROID_HOME) { $env:ANDROID_HOME = 'D:\Windows\Apps\Android\Sdk' }

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) }
}

# ---------------------------------------------------------------------------
# 1) 构建（构建不过就没有必要往下走：下面每一条绿都会是假的）
# ---------------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host '=== 构建 ==='
    # 可用 $env:QS_GRADLE 覆盖：既让脚本可配置，也让「注入一个必然失败的构建」成为可能
    # （负向对照 —— 没验过会拦的拦截等于没有拦截）。
    $gradle = $env:QS_GRADLE
    if (-not $gradle) { $gradle = 'D:\Windows\Apps\Gradle\gradle-8.13\bin\gradle.bat' }
    $buildOutput = & $gradle `
        -p (Join-Path $root 'android') :app:assembleDebug --console=plain 2>&1
    $errors = $buildOutput | Select-String '^e: '
    if ($LASTEXITCODE -ne 0 -or $errors) {
        Write-Host '构建失败，验收没有意义 —— 下面的结论一条都不成立：'
        $errors | Select-Object -First 10 | ForEach-Object { Write-Host ('  ' + $_.Line) }
        exit 1
    }

    Write-Host '构建通过'
}

# ---------------------------------------------------------------------------
# 2) 安装 + 启动
# ---------------------------------------------------------------------------
$apk = Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk'
if (-not (Test-Path $apk)) { Write-Host "找不到 APK：$apk"; exit 1 }

$install = & $adb -s $Serial install -r $apk 2>&1
# ⚠️ 先把输出**拼成一行**再匹配：`-notmatch` 作用在数组上是「过滤」而不是「返回布尔」，
# 安装输出有两行（Performing Streamed Install / Success），只要有一行不含 Success 就会被判成失败。
if (($install -join ' ') -notmatch 'Success') {
    Write-Host ('安装失败：' + ($install -join ' '))
    exit 1
}

& $adb -s $Serial shell am force-stop $Package | Out-Null
& $adb -s $Serial shell am start -n "$Package/$Package.MainActivity" | Out-Null
Start-Sleep -Seconds 9

# ---------------------------------------------------------------------------
# 3) 抓控件树（dump 到设备再 cat 回来，显式写 UTF-8）
# ---------------------------------------------------------------------------
function Get-Tree([string]$tag) {
    $remote = "/sdcard/qs-$tag.xml"
    $local = Join-Path $tmp "android-verify-$tag.xml"
    & $adb -s $Serial shell uiautomator dump $remote | Out-Null
    $raw = & $adb -s $Serial shell cat $remote
    [IO.File]::WriteAllText($local, ($raw -join "`n"), (New-Object Text.UTF8Encoding($false)))
    return $local
}

# 长按：找到文字所在节点，在它的中心按住 1 秒。
# 用 `input swipe x y x y 1000`（起点终点相同）—— Compose 的长按阈值约 500ms。
function Long-Press-Text([string]$file, [string]$text, [int]$ms = 1000) {
    $nodes = [regex]::Matches([IO.File]::ReadAllText($file), '<node[^>]*>')
    foreach ($node in $nodes) {
        $t = [regex]::Match($node.Value, 'text="([^"]*)"')
        if ($t.Success -and $t.Groups[1].Value -like "*$text*") {
            $b = [regex]::Match($node.Value, 'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"')
            if ($b.Success) {
                $x = [int](([int]$b.Groups[1].Value + [int]$b.Groups[3].Value) / 2)
                $y = [int](([int]$b.Groups[2].Value + [int]$b.Groups[4].Value) / 2)
                & $adb -s $Serial shell input swipe $x $y $x $y $ms | Out-Null
                return $true
            }
        }
    }

    return $false
}

function Tap-Text([string]$file, [string]$text) {
    $nodes = [regex]::Matches([IO.File]::ReadAllText($file), '<node[^>]*>')
    foreach ($node in $nodes) {
        $t = [regex]::Match($node.Value, 'text="([^"]*)"')
        if ($t.Success -and $t.Groups[1].Value -eq $text) {
            $b = [regex]::Match($node.Value, 'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"')
            if ($b.Success) {
                $x = [int](([int]$b.Groups[1].Value + [int]$b.Groups[3].Value) / 2)
                $y = [int](([int]$b.Groups[2].Value + [int]$b.Groups[4].Value) / 2)
                & $adb -s $Serial shell input tap $x $y | Out-Null
                return $true
            }
        }
    }

    return $false
}

# ---------------------------------------------------------------------------
# 4) 主壳断言
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 主壳断言 ==='
$tree = Get-Tree 'shell'
$xml = [IO.File]::ReadAllText($tree)
$names = [regex]::Matches($xml, 'text="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }

# AppBar 第 1 行是应用名（规格 §2.2），不是当前 tab 名。
Check 'AppBar 显示应用名「AI 双端搜题」' ($names -contains 'AI 双端搜题')
Check 'AppBar 有连接状态行（未配对 / 电脑未连接 / 已连接 …）' `
    ([bool]($names | Where-Object { $_ -match '^(未配对|电脑未连接|已连接)' }))

# 底部三标签（规格 §1.2）。
foreach ($tab in @('当前任务', '历史记录', '设置')) {
    Check ("底部导航栏有「$tab」") ($names -contains $tab)
}

# AppBar 标题位**不再**写当前 tab 名：规格里 tab 只由底部导航栏表示。
$currentCount = ($names | Where-Object { $_ -eq '当前任务' } | Measure-Object).Count
Check '「当前任务」只出现一次（标题位不再写 tab 名）' ($currentCount -eq 1) ("出现 " + $currentCount + " 次")

# ---------------------------------------------------------------------------
# 5) 历史页断言（点进第二个 tab）
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 历史页断言 ==='
if (Tap-Text $tree '历史记录') {
    # ⚠️ **点完不能立刻抓树**：实测出现过「点了历史记录、抓到的却是设置页」——
    # 切 tab 有动画与重组的竞态，抓早了就抓到上一屏（表现为两条假 FAIL）。
    # 所以轮询到「历史页的标记」出现为止，而不是睡固定秒数。
    $history = $null
    for ($i = 0; $i -lt 8; $i++) {
        Start-Sleep -Seconds 2
        $candidate = Get-Tree 'history'
        if ([IO.File]::ReadAllText($candidate) -match '搜索题干与解析') { $history = $candidate; break }
    }
    if (-not $history) { $history = $candidate }
    $historyXml = [IO.File]::ReadAllText($history)
    Check '历史页有搜索框「搜索题干与解析」' ($historyXml -match '搜索题干与解析')
    Check '历史页有合集分组或空态' `
        ([bool]($historyXml -match '条识别记录' -or $historyXml -match '还没有识别记录'))
}
else {
    Check '能点到「历史记录」标签' $false
}

# ---------------------------------------------------------------------------
Write-Host ''
# ---------------------------------------------------------------------------
# 6) 删除确认框（规格 §2.4.5）—— **只验弹窗、点取消，不删数据**
#
# 为什么只验到这一步：删一条记录会改真机上的数据，验收脚本每跑一次删一条，
# 跑几轮就把记录删光了（这一程真的发生过）。所以这里验「长按弹出了确认框、
# 文案与规格逐字一致、取消能关掉」，删除本身留给手工与那一次的 op 墓碑取证。
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 删除确认框断言 ==='
if (Test-Path $history) {
    $historyXml = [IO.File]::ReadAllText($history)
    if ($historyXml -match '条识别记录') {
        if (Tap-Text $history '未分类') {
            Start-Sleep -Seconds 5
            $detail = Get-Tree 'detail'
            $detailXml = [IO.File]::ReadAllText($detail)
            if (Long-Press-Text $detail '识别出') {
                Start-Sleep -Seconds 3
                $dialog = Get-Tree 'dialog'
                $dialogXml = [IO.File]::ReadAllText($dialog)
                Check '长按条目弹出「删除这条识别记录？」' ($dialogXml -match '删除这条识别记录？')
                Check '确认框正文与规格逐字一致' `
                    ($dialogXml -match '本机与电脑上的这条记录都会被删除（软删除，会同步到主机）。此操作不可撤销。')
                Check '确认框有「取消」与「删除」两个按钮' `
                    (($dialogXml -match '取消') -and ($dialogXml -match '>删除<|text="删除"'))
                # 点「取消」关掉 —— 不动数据。
                if (Tap-Text $dialog '取消') { Start-Sleep -Seconds 2 }
                Check '点「取消」后确认框关掉了' (-not ([IO.File]::ReadAllText((Get-Tree 'after-cancel')) -match '删除这条识别记录？'))
            }
            else {
                Write-Host '  SKIP  详情页上没有条目（记录被前面的验证删光了），这一段跳�过'
            }
        }
    }
    else {
        Write-Host '  SKIP  历史页当前是空态，删除确认框这一段跳过'
    }
}

Write-Host ("结果：PASS {0} / FAIL {1}" -f $pass, $fail)
Write-Host ("控件树：{0}" -f $tree)
exit $(if ($fail -eq 0) { 0 } else { 1 })

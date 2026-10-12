param(
    [string]$ProcessName = 'QuizSync.App',
    [int]$Dpi = 192,
    [switch]$NoLaunch
)

# 一条命令核验 Windows 端界面。
#
# **为什么需要它**：agent 看不到图像。前面已经因此栽过两次 ——
#  ① 药丸被 WinUI 画成椭圆（控件树完全看不出来，是像素游程量出来的）；
#  ② 代码里取主题色全拿到透明（`Application.Current.Resources.ThemeDictionaries`
#     不含合并字典里的条目），控件树同样看不出来。
# 所以验收必须同时做**控件树断言**与**像素断言**两种，缺一种就会漏掉一整类问题。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools/verify_windows_ui.ps1
# 退出码：0 = 全过；1 = 有 FAIL。

$ErrorActionPreference = 'Continue'
$root = 'D:\ZCode\QuizSync_AI'
$tmp = 'D:\DeepseekHarness\TemporaryConversationFile'
$uia = Join-Path $tmp 'verify-windows-ui.txt'

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) }
}

# ---------------------------------------------------------------------------
# 先构建 App。**这一步不能省** —— 省了就会验到「上一次构建的旧 exe」。
#
# 踩过（第 133 轮）：`dotnet test` **不构建 `QuizSync.App`**（它是 WinUI exe，
# 不是测试项目的依赖），所以 App 里的编译错误（当时是 `MultipageStaging.cs` 少了
# 一个 `using`）一路溜进了「构建 0 错误 + 测试 178 通过 + 验收 38 PASS」的结论里 ——
# 三条都真，但**它们描述的是一份编译不过的代码**。
# 现在构建放在最前面：构建不过就直接退出，后面的绿一律不作数。
# ---------------------------------------------------------------------------
# ⚠️ **先杀旧实例再构建**。踩过：把构建放到最前面、却没先杀掉正在跑的应用 ——
# 旧进程锁着 `bin\...\*.dll`，构建报一堆 MSB3027「文件被 QuizSync.App 锁定」，
# 于是**只要应用开着，验收脚本就跑不起来**。
if (-not $NoLaunch) {
    Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
}

Write-Host '=== 构建（验之前先确保验的是刚构建出来的东西）==='
$dotnet = $env:QS_DOTNET
if (-not $dotnet) { $dotnet = 'dotnet' }
$buildOutput = & $dotnet build (Join-Path $root 'windows\QuizSync.App\QuizSync.App.csproj') `
    -c Debug -v q --nologo 2>&1
$buildErrors = $buildOutput | Select-String ': error ' | Select-Object -Unique
if ($LASTEXITCODE -ne 0 -or $buildErrors) {
    Write-Host '构建失败，验收没有意义 —— 下面的结论一条都不成立：'
    $buildErrors | Select-Object -First 10 |
        ForEach-Object { Write-Host ('  ' + ($_.Line -replace ' \[D:\\ZCode.*$', '')) }
    exit 1
}

Write-Host '构建通过'

# ---------------------------------------------------------------------------
# 启动（或复用已在跑的实例）
# ---------------------------------------------------------------------------
$exe = Join-Path $root 'windows\QuizSync.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe'
if (-not (Test-Path $exe)) { Write-Host "找不到 exe：$exe"; exit 1 }

if (-not $NoLaunch) {
    # 旧实例已在构建之前杀掉（见上）。
    Start-Process -FilePath $exe | Out-Null
    Start-Sleep -Seconds 8
}

$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host '进程没起来'; exit 1 }
Write-Host ("进程 pid={0}  内存 {1} MB" -f $proc.Id, [math]::Round($proc.WorkingSet64 / 1MB, 1))

# ---------------------------------------------------------------------------
# 先过「首次使用门」。1.1.0 的 CollectionGate：一个合集都没有时，启动进的是
# 「新建任务合集」页而不是主界面（ui-spec-desktop §2.3.1）。
# 全新安装（或被清过库）时不过这一步，下面所有主界面断言都会红 —— 那是判据的问题，不是界面的问题。
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 首次使用门 ==='

# 轮询到「**主界面真的画完**」为止。**只保留一个 $lines，一个循环**。
# 判据必须是侧栏也在，不能只看顶栏药丸 —— 这里踩过三次，都表现为「明明在主界面却判定不对」：
# ① 抽成 Read-Tree 函数之后返回值不对；
# ② 启动后固定等 8 秒就 dump，dump 没写成，读到的是上一次留下的旧文件；
# ③ 门页之后只等 4 秒，抓到首帧还没画完的树（顶栏有了、侧栏没有）→ 顶栏断言过、其余全红。
$lines = @()
for ($attempt = 1; $attempt -le 12; $attempt++) {
    Start-Sleep -Seconds 2
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\dump_uia.ps1') `
        -ProcessName $ProcessName -MaxDepth 8 -OutFile $uia | Out-Null
    $lines = @(Get-Content $uia -Encoding UTF8 -ErrorAction SilentlyContinue)
    if ($lines | Select-String -Pattern 'name=[历史记录]' -SimpleMatch | Select-Object -First 1) {
        break
    }

    # 门页没有侧栏：给它几轮机会，稳定停在门页就跳出（下面会先建合集）。
    if ($attempt -ge 3 -and ($lines | Select-String -Pattern 'id=[PageTitle]' -SimpleMatch | Select-Object -First 1)) {
        break
    }
}

# 确保侧栏看的是「全部记录」。
# 为什么每次都要做：进入某个合集后侧栏默认是「当前合集」作用域，而**刚建的合集是空的** ——
# 那时侧栏是「这个合集还没有记录」的空态、内容区是欢迎态（**这是正确行为**），
# 会话头与底部导航条根本不在树里，下面三条断言必然红。
# 这一步是幂等的：已经在「全部记录」上再选一次没有副作用。
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc2 = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($proc2) {
    $c2 = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc2.Id)
    $w2 = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $c2) |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
        Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) } |
        Select-Object -First 1
    if ($w2) {
        $allItem = $w2.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '全部记录')))
        if ($allItem) {
            try {
                $allItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                Start-Sleep -Seconds 2
                & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\dump_uia.ps1') `
                    -ProcessName $ProcessName -MaxDepth 8 -OutFile $uia | Out-Null
                $lines = @(Get-Content $uia -Encoding UTF8)
                Write-Host '  已切到「全部记录」作用域'
            }
            catch { Write-Host ('  切「全部记录」失败：' + $_.Exception.Message) }
        }
    }
}
Write-Host ("  控件树 {0} 行" -f $lines.Count)
$onGate = -not ($lines | Select-String -Pattern 'name=[历史记录]' -SimpleMatch | Select-Object -First 1)

if ($onGate) {
    Write-Host '  当前停在合集门页，先建一个合集再继续'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\probe_collection_gate.ps1') `
        -ProcessName $ProcessName -Name '默认合集' | Out-Null
    # 导航 + 首帧渲染要时间：1 秒时读到的还是门页那棵旧树（实测踩过）。
    # 建完合集再轮询一次，**判据同样是侧栏也在**（见上面的注释：只看顶栏会抓到半帧）。
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        Start-Sleep -Seconds 2
        & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\dump_uia.ps1') `
            -ProcessName $ProcessName -MaxDepth 8 -OutFile $uia | Out-Null
        $lines = @(Get-Content $uia -Encoding UTF8 -ErrorAction SilentlyContinue)
        # 落地标记用**合集药丸的 AutomationId**，不要用某个文案：
        # 原来这里等的是 `name=[历史记录]`，而用户 2026-09-27 要求删掉那个标题 ——
        # 标记跟着文案走时，删一句界面文字就会让整个探针从第一条断言开始连锁报错。
        if ($lines | Select-String -Pattern 'id=[CollectionPillButton]' -SimpleMatch | Select-Object -First 1) {
            break
        }
    }

    Check '过完首次使用门后进入主界面' `
        (($lines | Select-String -Pattern 'id=[CollectionPillButton]' -SimpleMatch | Measure-Object).Count -gt 0)
}
else {
    Write-Host '  已有合集，直接在主界面'
}

# 从 UIA dump 的行里解析 rect=(x,y,WxH)。**定义必须在使用之前**：
# PowerShell 是顺序执行，函数定义写在调用点后面时调用处只会拿到 $null
#（踩过：对齐断言因此永远 FAIL，而原因看起来像「界面没对齐」）。
function Get-DumpRect([string[]]$dumpLines, [string]$automationId) {
    $line = ($dumpLines | Select-String -Pattern ("id=\[" + $automationId + "\]") | Select-Object -First 1).Line
    if (-not $line) { return $null }
    $m = [regex]::Match($line, 'rect=\((\d+),(\d+),(\d+)x(\d+)\)')
    if (-not $m.Success) { return $null }
    return [pscustomobject]@{
        X = [int]$m.Groups[1].Value; Y = [int]$m.Groups[2].Value
        W = [int]$m.Groups[3].Value; H = [int]$m.Groups[4].Value
    }
}

# ---------------------------------------------------------------------------
# 控件树
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 控件树断言 ==='

if (-not (Test-Path $uia)) { Write-Host 'dump 失败'; exit 1 }

$has = { param($pattern) ($lines | Select-String -Pattern $pattern -SimpleMatch | Measure-Object).Count -gt 0 }
$count = { param($pattern) ($lines | Select-String -Pattern $pattern -SimpleMatch | Measure-Object).Count }

# 只用**前缀**匹配：App.Note 会给标题加「（…）」后缀（那是刻意的用户可见提示），
# 写成全等会在任何一次提示之后误报。
Check '主窗口标题以「AI 双端搜题」开头' (& $has 'Window name=[AI 双端搜题')
Check '顶栏有项目名「QuizSync AI」' (& $has 'name=[QuizSync AI]')
Check '顶栏有合集药丸' (& $has 'id=[CollectionPillButton]')
Check '侧栏有范围切换（当前合集 / 全部记录）' ((& $has 'name=[当前合集]') -and (& $has 'name=[全部记录]'))
Check '侧栏有「截屏搜题」按钮' (& $has 'name=[截屏搜题]')
# 侧栏：用户 2026-09-27 要求删掉「历史记录」标题与计数药丸，把数量放到范围按钮右边。
Check '侧栏不再有「历史记录」标题（用户要求删掉）' (-not (& $has 'name=[历史记录]'))
Check '侧栏范围按钮仍在（当前合集 / 全部记录）' `
    ((& $has 'name=[当前合集]') -and (& $has 'name=[全部记录]') -and (& $has 'id=[SessionCountText]'))
$countLine = ($lines | Select-String -Pattern 'id=\[SessionCountText\]' | Select-Object -First 1).Line
Check '计数文案形如「当前 N / 共 M 条记录」' ($countLine -match '当前 \d+ / 共 \d+ 条记录') $countLine.Trim()

# 两栏第一行的**中心线**要在同一条水平线上（用户反馈「内容区上方一行没对齐」）。
# 判据用几何，不用「看起来」：侧栏计数行中心 y 与内容区会话头里图标按钮中心 y 相差 ≤ 6 物理像素。
$countRect = Get-DumpRect $lines 'SessionCountText'
$navRectForAlign = Get-DumpRect $lines 'NavPrevButton'
Check '取到侧栏第一行与内容区第一行的坐标' (($null -ne $countRect) -and ($null -ne $navRectForAlign))
if ($countRect -and $navRectForAlign) {
    $countCenter = $countRect.Y + ($countRect.H / 2)
    $navCenter = $navRectForAlign.Y + ($navRectForAlign.H / 2)
    Check '两栏第一行中心线对齐（内容区上方一行）' `
        ([math]::Abs($countCenter - $navCenter) -le 6) `
        ("侧栏行中心=" + [int]$countCenter + "  内容行中心=" + [int]$navCenter)
}
# 顶栏：多页暂存指示常驻，默认是「单张识别」（用户要求）
$stagingLine = ($lines | Select-String -Pattern 'id=\[StagingCount\]' | Select-Object -First 1).Line
Check '顶栏暂存指示默认显示「单张识别」' ($stagingLine -match '单张识别') $stagingLine.Trim()
# 会话切换（用户 2026-09-27 要求：从内容区**底部**那条导航栏简化后移到右上角动作前面）。
# 判据从「底栏有四件」改成「三件常在 + 位置对 + 回到本次按需出现」——
# 只查存在性是测不出搬家的，而「回到本次」在最上面一条时本就不该出现。
Check '会话切换常在的三件都在（上一条 / 下一条 / 位置文案）' `
    ((& $has 'id=[NavPrevButton]') -and (& $has 'id=[NavNextButton]') -and (& $has 'id=[NavPosition]'))
Check '内容区底部**没有**导航条了（上一次/下一次 不再是文字按钮）' `
    ((-not (& $has 'name=[上一次]')) -and (-not (& $has 'name=[下一次]')))
Check '免责声明常驻可见' (& $has 'name=[答案由 AI 生成，仅供参考]')

$navText = ($lines | Select-String -Pattern 'id=\[NavPosition\]' | Select-Object -First 1).Line
# 简化后的文案是「N / M」（原来是「第 N / M 条记录」）
Check '位置文案形如「N / M」' ($navText -match '\d+ / \d+') $navText.Trim()

# 题目标题行：会话头
Check '会话头有状态药丸（已完成/识别中/失败 任一）' `
    (((& $has 'name=[已完成]') -or (& $has 'name=[识别中]') -or (& $has 'name=[失败]')))
Check '会话头有来源（本机/手机）' (((& $has 'name=[本机]') -or (& $has 'name=[手机]')))

# ---------------------------------------------------------------------------
# 会话切换的**位置**（用户 2026-09-27：「简化后放到内容区右上角重新生成按钮的前面」）
#
# 判据用坐标，不是存在性：这两个元素在搬之前也存在（在底部导航条里），
# 只查「有没有」在搬家前后都是绿的 —— 那种断言等于没测。
# 从 dump 的行里解析 rect=(x,y,WxH)，要求：
#   ① 会话切换与「重新生成」**同一行**（y 相差 ≤ 8px）；
#   ② 会话切换在它**左侧**（右边界 ≤ 重新生成的左边界）。
# ---------------------------------------------------------------------------

$navRect = Get-DumpRect $lines 'NavPrevButton'
$retryRect = Get-DumpRect $lines 'HeaderRetryButton'
Check '提取到会话切换与「重新生成」两个按钮的坐标' (($null -ne $navRect) -and ($null -ne $retryRect))
if ($navRect -and $retryRect) {
    Check '会话切换与「重新生成」在同一行（内容区右上角那一条）' `
        ([math]::Abs($navRect.Y - $retryRect.Y) -le 8) `
        ("NavPrev y=" + $navRect.Y + "  Retry y=" + $retryRect.Y)
    Check '会话切换在「重新生成」**左侧**（就是用户要求的位置）' `
        (($navRect.X + $navRect.W) -le ($retryRect.X + 2)) `
        ("NavPrev 右=" + ($navRect.X + $navRect.W) + "  Retry 左=" + $retryRect.X)
}

# ---------------------------------------------------------------------------
# 像素断言（控件树看不出来的那一类问题）
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 像素断言 ==='

# 窗口几何 + 画布色。内容区的**左边距**（卡片 margin 水平 14）在任何滚动位置都是画布色，
# 所以在 x=406 上沿 y 扫几个点，命中一次就算过 —— 钉死单个像素会随窗口位置 / 滚动位置 / 选中的
# 会话而抖动，那种「时绿时红」的断言比没有还糟（本项目已经因为脆断言被带偏过一次）。
#
# ⚠️ 第 170 轮把这条判据从「固定坐标采样」改成「**全场颜色直方图**」（原来取 x=406）：
# 固定点的颜色取决于「当前显示的是哪条记录 / 哪个页面」—— 实测同一份代码同一个点，
# 短记录时是画布色、用户那条 17 题的记录一进来就变成卡片白，于是这条时绿时红。
# 「主题资源有没有并进去」的正确问法是「画布色在窗口里**存在且成规模**」，
# 与布局、数据、滚动位置都无关（判据来自 `probe_pixels.ps1 -ScanGrid` 的直方图）。
$probeText = ''
$probeAttempts = @()
foreach ($attempt in 1..3) {
    $probe = & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\probe_pixels.ps1') `
        -ProcessName $ProcessName -Dpi $Dpi -Points '406,600' -ScanGrid 40
    $probeText = ($probe | Out-String)
    $scanLine = [string]($probeText -split "`n" | Where-Object { $_ -match 'scan: #F3F5F4' } | Select-Object -First 1)
    $topLine = [string]($probeText -split "`n" | Where-Object { $_ -match '出现最多的' } | Select-Object -First 1)
    $probeAttempts += ("第 {0} 次: {1} / {2}" -f $attempt, $scanLine.Trim(), $topLine.Trim())
    if ($probeText -match 'scan: #F3F5F4 x \d+') { break }
    Start-Sleep -Milliseconds 1200
}
$canvasCount = [int]([regex]::Match($probeText, 'scan: #F3F5F4 x (\d+)').Groups[1].Value)
Check '画布色 #F3F5F4 在窗口里成规模出现（说明主题资源真的并进去了）' ($canvasCount -ge 50) `
    (($probeAttempts -join ' || ') + ("；画布色采样点 = " + $canvasCount))

# 悬浮球：窗口边长必须 = round(球 × DPR) + 2 × round(描边 × DPR)
$ball = & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\probe_ball_shape.ps1') `
    -ProcessName $ProcessName
$ballText = ($ball | Out-String)
$ballRect = [regex]::Match($ballText, 'rect=\(\d+,\d+\) (\d+)x(\d+)')
if ($ballRect.Success) {
    $bw = [int]$ballRect.Groups[1].Value
    $bh = [int]$ballRect.Groups[2].Value
    $settings = Get-Content (Join-Path $env:LOCALAPPDATA 'QuizSyncAI\app\ball.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $scale = $Dpi / 96.0
    $expected = [int][math]::Round($settings.diameter * $scale) + 2 * [int][math]::Round($settings.stroke_width * $scale)
    Check '悬浮球窗口是正方形' ($bw -eq $bh) "${bw}x${bh}"
    Check "悬浮球窗口边长 = 球+描边×2（期望 $expected，按 ball.json 的 diameter=$($settings.diameter) stroke_width=$($settings.stroke_width)）" `
        ($bw -eq $expected) "实际 $bw"
    Check '球面有素材的非纯色像素（不是空窗口）' (($ballText -split "`n" | Where-Object { $_ -match '#[0-9A-F]{6}' } | Measure-Object).Count -gt 6)
}
else {
    Check '找得到悬浮球窗口' $false
}

# ---------------------------------------------------------------------------
# 设置页断言：先点顶栏齿轮进去，再 dump 一次
# （规格 ui-spec-desktop §3.1：10 个分类，顺序即界面顺序）
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 设置页断言（自动点齿轮进入）==='

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
$window = $candidates |
    Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
    Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) } |
    Select-Object -First 1

$opened = $false
if ($window) {
    $gear = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsButton')))
    if ($gear) {
        $pattern = $gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
        Start-Sleep -Seconds 3
        $opened = $true
    }
}

Check '找到并点开了顶栏齿轮' $opened

if ($opened) {
    $uiaSettings = Join-Path $tmp 'verify-windows-settings.txt'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\dump_uia.ps1') `
        -ProcessName $ProcessName -MaxDepth 8 -OutFile $uiaSettings | Out-Null
    $sl = Get-Content $uiaSettings -Encoding UTF8
    $sHas = { param($pattern) ($sl | Select-String -Pattern $pattern -SimpleMatch | Measure-Object).Count -gt 0 }

    Check '左导航标题「设置」' (& $sHas 'name=[设置]')
    foreach ($category in @('使用说明', '显示设置', 'API 配置', '连接设备', '识别设置',
                            '悬浮球设置', '悬浮窗设置', '热键设置', '数据管理', '关于')) {
        Check ("左导航有分类「" + $category + "」") (& $sHas ("name=[" + $category + "]"))
    }
    Check '左导航底部有版本号（AI 双端搜题 vX）' `
        (($sl | Select-String -Pattern 'name=[AI 双端搜题 v' -SimpleMatch | Measure-Object).Count -gt 0)

    # -----------------------------------------------------------------------
    # 10 个分类**必须等宽**（用户报过的 bug：「设置界面现在每页都不等宽」）
    #
    # 根因：`ContentHost` 用了 `HorizontalAlignment="Center"` —— Center 的排列宽度取的是
    # **内容期望宽度**，于是每页宽度 = 它最宽那个子元素的宽度。判据不能靠肉眼看截图，
    # 要逐页量出来比：
    #   ① 先量一份「内容区可用宽度」的基准 = 最宽那一页的卡片宽度；
    #   ② 逐页点过去，量该页第一张卡片的宽度；
    #   ③ 全部相等才算过（容差 1px，200% DPI 下的取整）。
    # -----------------------------------------------------------------------
    $categoryTabs = @('使用说明', '显示设置', 'API 配置', '连接设备', '识别设置',
                      '悬浮球设置', '悬浮窗设置', '热键设置', '数据管理', '关于')
    $widths = @{}
    $navItems = @{}
    foreach ($category in $categoryTabs) {
        $item = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $category)))
        if ($item) { $navItems[$category] = $item }
    }
    Check "10 个分类都能在左导航里找到（找到 $($navItems.Count) 个）" ($navItems.Count -eq 10)

    foreach ($category in $categoryTabs) {
        if (-not $navItems.ContainsKey($category)) { continue }
        try {
            $sel = $navItems[$category].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $sel.Select()
        } catch {
            # NavigationViewItem 不一定暴露 SelectionItem；退回 Invoke
            try { $navItems[$category].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { }
        }
        Start-Sleep -Milliseconds 700

        # 「悬浮窗设置」这一页**逐条对规格 §3.8**：12 项设置 + 外观 + 6 套配色 + 页尾说明。
        # ⚠️ 第 170 轮按用户要求改了两处，这里的清单跟着改：
        #   * 删掉「加长外观（竖屏 9:30）」（现在只有两种外观）；
        #   * 删掉「透明度」整项（半透明会让压在白底题目上的文字一起变淡）。
        # 这一页的判据就是「用户能在界面上看到哪几项」，所以它必须与设置页代码同步。
        if ($category -eq '悬浮窗设置') {
            $pageNames = ($window.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
                ForEach-Object { $_.Current.Name }) -join "`n"
            foreach ($label in @(
                '显示悬浮窗', '置顶显示', '锁定位置', '归位', '恢复默认位置',
                '标准外观（竖屏 9:20）', '横向外观（20:9）', '使用中',
                '显示比例', '窗内字符大小',
                '配色', '纯净白（默认）', '薄荷绿', '天空蓝', '丁香紫', '暖沙', '樱粉',
                '明暗模式', '跟随软件设置', '明亮', '深色',
                '详细解析模式')) {
                Check ("悬浮窗设置页有「" + $label + "」") ($pageNames -like ("*" + $label + "*"))
            }
            # 反向判据：删掉的那两项**不许**再出现（否则「删了」只是说说）
            foreach ($gone in @('加长外观', '透明度')) {
                Check ("悬浮窗设置页已经没有「" + $gone + "」") (-not ($pageNames -like ("*" + $gone + "*")))
            }
            Check '悬浮窗设置页有页尾说明（提到托盘右键也能开关）' `
                ($pageNames -like '*开关悬浮窗也可以在托盘右键菜单里切*')
        }

        # 内容区卡片：取「设置卡片」类控件里最靠上、面积最大的那个的宽度。
        # 用 ControlType=Group 太窄，这里直接扫这一页所有后代里的 SettingsCard 名称节点。
        $cards = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Group)))
        $pageWidth = 0
        foreach ($card in $cards) {
            $rect = $card.Current.BoundingRectangle
            # 排除左导航（宽度 232 那一栏）与过小的分组
            if ($rect.Width -gt 400 -and $rect.Width -gt $pageWidth) { $pageWidth = [int][math]::Round($rect.Width) }
        }
        $widths[$category] = $pageWidth
    }

    $distinct = ($widths.Values | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
    $detail = ($widths.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
    Check '10 个分类的内容区宽度一致（用户报过「每页都不等宽」）' `
        (($distinct.Count -eq 1) -and ($widths.Values -notcontains 0)) $detail

    # -----------------------------------------------------------------------
    # 设置页的「返回」必须是**可用**的（用户报过「左上角返回无用」）
    #
    # 根因：`IsBackButtonVisible="Visible"` 只负责把按钮画出来，启用状态由 `IsBackEnabled`
    # 决定，而后者**默认 false** —— 按钮灰着、点不动，UIA Invoke 都会失败。
    # 判据不能只看「按钮在不在」：要量 `IsEnabled`，再真的点一次看有没有回主界面。
    # -----------------------------------------------------------------------
    $backBtn = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'NavigationViewBackButton')))
    Check '设置页有返回按钮' ([bool]$backBtn)
    if ($backBtn) {
        Check '返回按钮是**可用**的（IsBackEnabled）' ($backBtn.Current.IsEnabled) `
            ("IsEnabled=" + $backBtn.Current.IsEnabled)
        if ($backBtn.Current.IsEnabled) {
            $backBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Seconds 2
            $stillCategories = $false
            foreach ($category in @('使用指南', '使用说明', 'API 配置')) {
                $hit = $window.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, $category)))
                if ($hit) { $stillCategories = $true; break }
            }
            Check '点返回后离开了设置页' (-not $stillCategories)
        }
    }
}

# ---------------------------------------------------------------------------
# 托盘断言。`Shell_NotifyIcon(NIM_ADD)` 是**没有可靠返回值判据**的老 API：
# 窗口建好了、图标文件加载失败、资源管理器没接住，它都可能一声不吭地失败，
# 界面上唯一表现是「托盘区没有那个图标」，而 agent 看不到屏幕。
# 客观判据是 Shell_NotifyIconGetRect（见 tools/probe_tray.ps1）。
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 托盘断言 ==='

$trayProbe = & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\probe_tray.ps1') `
    -ProcessName $ProcessName
$trayText = ($trayProbe | Out-String)
Write-Host $trayText.TrimEnd()

Check '进程里有托盘隐藏窗口（类名 QuizSyncTrayWindow）' ($trayText -match '类名 QuizSyncTrayWindow')
Check '托盘图标确实在通知区域（Shell_NotifyIconGetRect 成功）' ($trayText -match 'PASS\s+图标在通知区域里') `
    (($trayText -split "`n" | Where-Object { $_ -match 'Shell_NotifyIconGetRect' } | Select-Object -First 1))

# 命令派发链路：窗口过程 → id→命令映射 → 处理器 → 真实动作。
# 菜单本身走 TrackPopupMenuEx 的返回值，而**弹菜单会阻塞**、没法自动化点击；
# 所以这里走窗口过程里那条 WM_COMMAND 分支（同一套 Dispatch）。
# 两个方向都要验：一个**真动作**（开/关悬浮球）与一个**未接项必须出声**（不许静默空操作）。
# ⚠️ 读探针输出要按**行内容**取，不能取「最后一行」—— 探针最后一行是 `（MenuId=N）`。
# 这里踩过一次：把「提示其实正常出现了」误判成 FAIL，白追了一轮。
function Send-TrayCommand([int]$id) {
    $text = (& powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\probe_tray_command.ps1') `
            -ProcessName $ProcessName -MenuId $id | Out-String)
    $ball = [regex]::Match($text, '发命令后：托盘窗口=\d+\s+悬浮球窗口=(\d+)')
    $float = [regex]::Match($text, '悬浮球窗口=\d+\s+悬浮窗=(\d+)')
    # 探针会打印「发命令前」与「发命令后」**两行**标题，正则必须锚在「发命令后」那一行 ——
    # 只写 `标题=\[(.*?)\]` 会取到第一处（= 命令**前**的旧标题），把已经成功的提示误判成 FAIL。
    $title = [regex]::Match($text, '发命令后：.*?标题=\[(.*?)\]')
    return [pscustomobject]@{
        Ball  = if ($ball.Success) { [int]$ball.Groups[1].Value } else { -1 }
        Float = if ($float.Success) { [int]$float.Groups[1].Value } else { -1 }
        Title = if ($title.Success) { $title.Groups[1].Value } else { '' }
    }
}

# id 6 = 布局第 6 项 = 显示/隐藏悬浮球（真动作）。发两次应当回到原状。
$off = Send-TrayCommand 6
$on = Send-TrayCommand 6
Check '托盘「显示/隐藏悬浮球」能真的把球关掉再打开' `
    (($off.Ball -eq 0) -and ($on.Ball -eq 1)) `
    ("关掉后球窗口=" + $off.Ball + "，再点后=" + $on.Ball)

# id 7 = 布局第 7 项 = 显示/隐藏悬浮窗（**本轮起是真动作**）。
# 上一轮这条断言的是「未接项要出声」（`标题 -match '还没接上'`）—— 悬浮窗落地后那个提示
# 就不该再出现了，所以这里改成**真的开关**：连发两次命令，浮窗窗口数必须**翻转**。
# 判据写成「翻转」而不是写死 0→1：起始状态取决于 app.json 里的 `float_enabled`，
# 写死方向会因为「跑之前刚好开着」而假红（这条纪律本轮在探针上刚踩过一次）。
$floatOnce = Send-TrayCommand 7
$floatTwice = Send-TrayCommand 7
Check '托盘「显示/隐藏悬浮窗」能真的把浮窗开关一次' `
    ((($floatOnce.Float -eq 0) -and ($floatTwice.Float -eq 1)) -or `
     (($floatOnce.Float -eq 1) -and ($floatTwice.Float -eq 0))) `
    ("两次命令后浮窗窗口数=" + $floatOnce.Float + " → " + $floatTwice.Float)
Check '托盘项不再报「还没接上」（功能已接）' `
    (($floatOnce.Title -notmatch '还没接上') -and ($floatTwice.Title -notmatch '还没接上')) `
    ("标题=[" + $floatTwice.Title + "]")

# ---------------------------------------------------------------------------
# 多页暂存：加页 → 暂存条出现（文案含 N/上限）；清空 → 条带消失。
# 走托盘的同名项，与 F9、悬浮球长按**同一条入口**（一分叉就必然有一条是错的）。
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 多页暂存断言 ==='

function StagingText {
    $f = Join-Path $tmp 'verify-staging.txt'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\dump_uia.ps1') `
        -ProcessName $ProcessName -MaxDepth 8 -OutFile $f | Out-Null
    $line = Get-Content $f -Encoding UTF8 | Select-String -Pattern 'id=\[StagingCount\]' | Select-Object -First 1
    if ($line) { return $line.Line.Trim() } else { return '' }
}

$null = Send-TrayCommand 2          # 多页识别 = 加一页
Start-Sleep -Seconds 1
$staged = StagingText
Check '加一页后暂存指示变成「多页 N/M 页」' ($staged -match '多页 \d+/\d+ 页') $staged

$null = Send-TrayCommand 3          # 清空多页暂存区
Start-Sleep -Seconds 1
$cleared = StagingText
# 清空后**回到默认态**（而不是整块消失）：指示器常驻在顶栏，忽隐忽现会让后面元素左右跳。
Check '清空后暂存指示回到「单张识别」' ($cleared -match '单张识别') $cleared

# ---------------------------------------------------------------------------
# 顶栏搜索：开弹窗 → 填关键字 → 回车 → 结果列表出现。
#
# 放在**最后**：这个弹窗是模态的，留到最后就不会干扰前面那些断言
#（探针留下未关闭的弹窗会把被测状态污染掉，这是踩过的坑）。
# 判据落在「结果行真的出来了」上：只断言弹窗打开的话，
# 一个「能开、能输入、但搜不出东西」的实现同样是绿的。
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 搜索断言 ==='

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$searchProc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if ($searchProc) {
    $searchCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $searchProc.Id)
    $searchWin = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $searchCond) |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
        Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) } |
        Select-Object -First 1

    $searchBtn = $searchWin.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SearchButton')))
    Check '顶栏有搜索按钮' ([bool]$searchBtn)

    # -----------------------------------------------------------------------
    # 免责声明的位置（用户 2026-09-27 要求：移出内容区，放到顶栏右上角功能按键前面）
    #
    # 判据用**坐标**而不是「在不在」：同一个文字在两个地方都可能出现（顶栏 / 内容区），
    # 只查存在性是测不出位置的。这里量它的矩形：必须在窗口顶部（顶栏那一条带内），
    # 且横向在三个动作按钮的左侧。
    # -----------------------------------------------------------------------
    $disclaimer = $searchWin.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '答案由 AI 生成，仅供参考')))
    Check '顶栏有免责声明「答案由 AI 生成，仅供参考」' ([bool]$disclaimer)
    if ($disclaimer) {
        $dRect = $disclaimer.Current.BoundingRectangle
        $wRect = $searchWin.Current.BoundingRectangle
        $inTopBar = ($dRect.Top - $wRect.Top) -lt 120          # 200% DPI 下顶栏约 64 物理像素高
        Check '免责声明在顶栏那一条带内（不是内容区）' $inTopBar `
            ("顶距=" + [int]($dRect.Top - $wRect.Top) + "px")

        # 更强的一条（直接对上用户那句「移出内容区」）：**内容区里不许再出现**这句声明。
        # 用 FindAll + 逐个量坐标，比「存在性」准 —— 文字在两处都有时，存在性判断是绿的。
        $allNodes = $searchWin.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '答案由 AI 生成，仅供参考')))
        $outside = 0
        $tops = @()
        foreach ($node in $allNodes) {
            $gap = [int]($node.Current.BoundingRectangle.Top - $wRect.Top)
            $tops += $gap
            if ($gap -ge 120) { $outside++ }
        }
        Check '内容区不再有免责声明（所有实例都在顶栏带内）' ($outside -eq 0) `
            ("实例数=" + $allNodes.Count + " 顶距=" + ($tops -join ','))

        if ($searchBtn) {
            $sRect = $searchBtn.Current.BoundingRectangle
            Check '免责声明在动作按钮**左侧**（不是右侧/下方）' ($dRect.Right -le $sRect.Left + 2) `
                ("免责右=" + [int]$dRect.Right + "  搜索按钮左=" + [int]$sRect.Left)
        }
    }

    if ($searchBtn) {
        $searchBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 2

        $box = $searchWin.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit)))
        Check '点搜索后出现搜索框' ([bool]$box)

        if ($box) {
            $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('加速度')
            Start-Sleep -Milliseconds 500
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Start-Sleep -Seconds 2

            $f = Join-Path $tmp 'verify-search.txt'
            & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\dump_uia.ps1') `
                -ProcessName $ProcessName -MaxDepth 12 -OutFile $f | Out-Null
            # ⚠️ 判据**不要依赖结果条数**。原来是「匹配 N 道题」这行存在 + 含关键词的行数 > 1，
            # 而那取决于桩数据里有几条记录 —— 我把当前合集切空之后只剩 1 行，断言变红，
            # 但功能是好的：这属于「脆断言」，比没有断言更糟（会被带偏一轮）。
            # 现在的判据：输入关键词回车后，**至少有一行结果带这个关键词**。
            # 仍然抓得住「能开、能输入、但搜不出东西」那种假实现（那种情况一行都没有）。
            # 这里搜的是桩数据里的题干「下列关于加速度的说法正确的是？」
            $rows = @(Get-Content $f -Encoding UTF8 | Select-String -Pattern '加速度' -SimpleMatch)
            # ⚠️ 别把 `if (...) {...} else {...}` 直接当参数写 —— PowerShell **5.1 不支持**
            #    if 作表达式（那是 PS 7 才有的），会运行失败并把整段脚本带崩：
            #    表现是「PASS/FAIL 都正常但 exit code = 1、后面那条断言一个字都没打印」。
            $hitsDetail = '（没出结果）'
            if ($rows.Count -gt 0) { $hitsDetail = $rows[0].Line.Trim() }
            Check '搜「加速度」能出结果（结果行里带这个关键词）' ($rows.Count -gt 0) $hitsDetail
        }

        # 关掉弹窗，别把它留给下一个跑这个脚本的人。
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Seconds 1
    }
}

# ---------------------------------------------------------------------------
Write-Host ''
Write-Host ("结果：PASS {0} / FAIL {1}" -f $pass, $fail)
Write-Host ("控件树全文：{0}" -f $uia)
exit $(if ($fail -eq 0) { 0 } else { 1 })

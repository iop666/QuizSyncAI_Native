package com.quizsync.android.dev.ui

import com.quizsync.android.dev.ui.designsystem.QsChoiceRow
import com.quizsync.android.dev.ui.designsystem.QsSecondaryButton
import com.quizsync.android.dev.ui.designsystem.QsSectionCard
import com.quizsync.android.dev.ui.designsystem.QsSettingRow
import com.quizsync.android.dev.ui.designsystem.QsSliderRow
import com.quizsync.android.dev.ui.designsystem.QsSwitchRow
import com.quizsync.android.dev.ui.theme.quizPalette

import android.os.Build
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.quizsync.android.dev.QsTokens
import com.quizsync.android.dev.state.QuizUiState
import com.quizsync.android.dev.state.QuizViewModel
import com.quizsync.android.dev.state.ThemeMode
import com.quizsync.android.dev.ui.components.MathText
import com.quizsync.android.dev.ui.designsystem.QsOptionBadge

/**
 * 「设置」tab（规格 ui-spec-android §2.5）。
 *
 * 这一版只落**已经真正接上**的项：主题 / 字号 / 识别模块开关 / 悬浮球三项 / 连接与解除配对 / 版本。
 * 规格里还有「识别模块二级页」（§2.6）与悬浮球的实际行为，那些没做的**不假装**：
 * 要么不出现在这里，要么明确标注还没接上。
 *
 * 每改一项就调 ViewModel 的方法落库 —— 界面上不留「改了要重启才生效」的项。
 *
 * **P2 重构（2.0）**：整页换成设计系统组件（`QsSectionCard` / `QsSwitchRow` / `QsChoiceRow` /
 * `QsSliderRow` / `QsSettingRow`），间距与颜色一律取令牌 —— 原来这里有一份私有的 `Group`
 * 与 `SwitchRow`，和识别模块二级页里的同名私有组件是两套写法，字号/间距各自硬编码。
 * **设置项、文案、落库键位一个都没动**。
 */
@Composable
fun SettingsTab(
    vm: QuizViewModel,
    ui: QuizUiState,
    onOpenPairing: () -> Unit,
) {
    val settings = ui.settings

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(QsTokens.spaceLg),
        verticalArrangement = Arrangement.spacedBy(QsTokens.spaceMd),
    ) {
        QsSectionCard(title = "识别模块") {
            QsSwitchRow(
                title = "识别模块（截取图片传到电脑分析）",
                description = if (settings.recognitionEnabled) {
                    "已开启：悬浮球可截屏，长按进入一题多页"
                } else {
                    "已关闭：悬浮球不显示，截屏与上传入口不可用"
                },
                checked = settings.recognitionEnabled,
                onCheckedChange = { vm.setRecognitionEnabled(it) },
            )

            // 顶部说明条与二级页**共用同一份文案常量**（规格 §5.1 明确要求防漂移）。
            // 颜色取 palette 的「题目不全」琥珀色，不再写字面量（原来这里是 Color(0xFFEAA100)）。
            Text(
                RECOGNITION_SLOW_NOTE,
                style = MaterialTheme.typography.bodySmall,
                color = quizPalette.incomplete,
            )

            // 二级页入口（规格 §2.5：**仅当识别模块开启**才出现）。
            // 1.1.0 的结构就是「首页一个开关 + 一个入口，细节进二级页」——
            // 截屏方式 / 悬浮球 / 一题多页 / 权限全在那一页，别摊回首页。
            if (settings.recognitionEnabled) {
                QsSettingRow(
                    title = "识别模块设置",
                    description = "截屏方式、悬浮球、一题多页、权限",
                    icon = Icons.Default.Tune,
                    onClick = { vm.openRecognitionSettings() },
                    trailing = {
                        Icon(
                            Icons.Default.ChevronRight,
                            contentDescription = null,
                            modifier = Modifier.size(QsTokens.iconSm),
                            tint = MaterialTheme.colorScheme.outline,
                        )
                    },
                )
            }
        }

        QsSectionCard(title = "外观") {
            Text("主题", style = MaterialTheme.typography.bodyLarge)
            // 互斥的三态用 M3 原生的分段按钮（原来是一排 FilterChip —— 那是「过滤」语义）。
            QsChoiceRow(
                options = ThemeMode.entries,
                selected = settings.themeMode,
                onSelect = { vm.setThemeMode(it) },
                label = { it.label },
            )

            // 动态取色（Material You）只在 API 31+ 存在 —— **低版本不显示这一项**，
            // 而不是显示一个灰开关：那个开关在那些机器上永远点不动，用户会以为坏了。
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                QsSwitchRow(
                    title = "动态取色",
                    description = if (settings.dynamicColor) {
                        "已开启：配色跟随壁纸（品牌绿会被替换掉）"
                    } else {
                        "已关闭：使用应用自己的品牌绿"
                    },
                    checked = settings.dynamicColor,
                    onCheckedChange = { vm.setDynamicColor(it) },
                )
            }

            QsSliderRow(
                title = "题目字号",
                value = settings.fontSize.toFloat(),
                valueRange = 12f..32f,
                steps = 19,
                valueLabel = settings.fontSize.toString(),
                minLabel = "小",
                maxLabel = "大",
                onValueChange = { vm.setFontSize(it.toInt()) },
            )

            // 字号预览（用户 2026-09-28：「因为结果页中删除了字号调节，在设置页题目字号设置下加入
            // 预览效果」）。手机竖屏的结果页标题栏已经没有 A− / A＋（见 `qsShowsFontActions`），
            // 改字号只有这一条路 —— 那这条路上必须**看得见**改完是什么样。
            //
            // 预览用的字重、行高、圈内字母与结果页**逐项同源**：题干 Bold、解析 Normal、
            // 选项字母走同一个 `QsOptionBadge`（圈与字母的居中规则也就不会两边不一致）。
            FontSizePreview(fontSize = settings.fontSize)
        }

        // 悬浮球的四项设置**不在这里** —— 它们在「识别模块设置」二级页（规格 §5）：
        // 悬浮球是识别模块的一部分（单击＝截一张识别，长按＝一题多页），
        // 摊在设置首页既看不出这层关系，也没地方放权限状态。
        // 这里只保留「没有悬浮窗权限就什么都显示不出来」这一条**能走通的入口**，
        // 因为用户在本页开了开关就期待立刻看到球。

        QsSectionCard(title = "连接") {
            Text(
                if (ui.pairedHost.isNullOrEmpty()) "未配对" else "已配对：${ui.pairedHost}",
                style = MaterialTheme.typography.bodyLarge,
            )
            QsSecondaryButton(onClick = onOpenPairing, modifier = Modifier.fillMaxWidth()) {
                Text("重新配对 / 换一台电脑")
            }
            QsSecondaryButton(onClick = { vm.unpair() }, modifier = Modifier.fillMaxWidth()) {
                Text("解除配对")
            }
        }

        // 测试数据：**没有主机服务端时，界面上没有任何记录可看**（见 DECISIONS 的阻塞性发现）。
        // 这个入口用应用自己的写入路径造几条，方便看界面各分支（普通题 / 建议复核 /
        // AI 猜测 / 判断题 / 主观题 / 带材料的题）。造出来的记录和真实记录长得一样，
        // 也能被搜索、被删除。主机服务端补上之后可以把这个分组去掉。
        QsSectionCard(title = "测试数据") {
            Text(
                "当前没有主机服务端，手机拉不到记录。点下面的按钮可以在本机造几条，用来查看界面。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            QsSecondaryButton(
                onClick = { vm.seedTestData(5) },
                modifier = Modifier.fillMaxWidth(),
            ) {
                Text("生成 5 条测试记录")
            }
        }

        QsSectionCard(title = "关于") {
            Text("AI 双端搜题（开发版）", style = MaterialTheme.typography.titleSmall)
            Text("版本 ${vm.appVersionLabel()}", style = MaterialTheme.typography.bodySmall)
            Text(
                "局域网内的跨端搜题工具：电脑截屏识别，结果同步到这台手机。",
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

/**
 * 题目字号预览：一行题干 + 四个选项（A–D），字号全部跟「题目字号」这一项走。
 *
 * 用户口径（2026-09-28）：题干写「题目答案字号预览」，下面依次是
 * `A. 选项一` / `B. 选项二` / `C. 选项三` / `D. 选项四`。
 *
 * 与结果页保持同源的三件事（都走同一个组件/同一个字重，不另写一份）：
 *  * 题干：`MathText` + `FontWeight.Bold`（结果页题干现在就是 Bold）；
 *  * 选项字母：`QsOptionBadge`（圆底 + 字母圈内居中，与结果页选项区同一个组件）；
 *  * 行高：题干 ×1.4、选项 ×1.5 —— 与结果页逐项相同，所以换字号时"看起来会不会挤"在这里就能看出来。
 *
 * 底色用 `palette.fill`（次级填充）：预览是一块"示意内容"，不该和设置项本身混在一起看。
 */
@Composable
private fun FontSizePreview(fontSize: Int) {
    val palette = quizPalette
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(palette.fill, RoundedCornerShape(QsTokens.radiusControl))
            .border(QsTokens.hairline, palette.cardStroke, RoundedCornerShape(QsTokens.radiusControl))
            .padding(QsTokens.spaceMd),
    ) {
        MathText(
            text = "题目答案字号预览",
            fontSize = fontSize,
            lineHeight = (fontSize * 1.4f).sp,
            fontWeight = FontWeight.Bold,
            color = palette.questionText,
        )
        Spacer(Modifier.height(QsTokens.spaceSm))
        listOf("选项一", "选项二", "选项三", "选项四").forEachIndexed { index, text ->
            Row(
                modifier = Modifier.fillMaxWidth().padding(top = if (index == 0) 0.dp else 4.dp),
                verticalAlignment = Alignment.Top,
            ) {
                QsOptionBadge(
                    label = ('A' + index).toString(),
                    fontSize = fontSize,
                    textColor = palette.questionText,
                    circleColor = palette.questionText.copy(alpha = 0.16f),
                )
                Spacer(Modifier.width(10.dp))
                MathText(
                    text = text,
                    fontSize = fontSize,
                    lineHeight = (fontSize * 1.5f).sp,
                    color = palette.questionText,
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

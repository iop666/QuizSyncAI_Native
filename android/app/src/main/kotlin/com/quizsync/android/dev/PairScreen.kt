package com.quizsync.android.dev

import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
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
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.outlined.ContentPasteGo
import androidx.compose.material.icons.outlined.ErrorOutline
import androidx.compose.material.icons.outlined.Keyboard
import androidx.compose.material.icons.outlined.NoPhotography
import androidx.compose.material.icons.outlined.QrCodeScanner
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.NotFoundException
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.quizsync.android.dev.state.PairingUiState
import com.quizsync.android.dev.ui.designsystem.QsCard
import com.quizsync.android.dev.ui.designsystem.QsErrorCard
import com.quizsync.android.dev.ui.designsystem.QsPrimaryButton
import com.quizsync.android.dev.ui.designsystem.QsSecondaryButton
import com.quizsync.android.dev.ui.designsystem.QsSectionCard
import com.quizsync.android.dev.ui.designsystem.QsTextField
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * 配对页（**规格 §2.1 逐条复刻**）——三个并列模块：扫码 / 粘贴配对链接 / 手动输入。
 *
 * ## 这个文件为什么被整个重写
 *
 * 旧实现是「一张卡 + 三个输入框」，自己 `remember` 状态并自己调 `HostApi.pair()`。
 * 而 `QuizViewModel` 里那套 `PairingUiState` + 7 个配对方法**早就写好了**（连规格的
 * 错误文案都逐字对得上），只是没有任何调用点 —— 本项目第 8 次「状态层齐了、连接线没接」。
 *
 * 更麻烦的是它**同时是第二条配对路径**：`MainActivity` 的注释里记着真机抓到的竞态 ——
 * ViewModel 的 `onPairLink` 与旧页面的 `LaunchedEffect(prefill)` 会**各发一次** `POST /pair`，
 * 而协议里重复配对是「签发新令牌并作废旧令牌」，于是屏幕上写「配对成功」、之后每个请求 401。
 * 所以这次是**一次替换**（旧的自管状态与自提交在本文件里彻底消失），
 * 与 `docs/DECISIONS.md` 里那条「配对只能有一条路径」对齐。
 *
 * ## 本页是**纯视图**
 *
 * 所有状态来自 `PairingUiState`，所有动作走回调；页面自己不碰网络、不碰库、不碰剪贴板以外的系统服务。
 * 唯一的局部状态是扫码模块的三件事（是否在扫、是否正在申请权限、权限是否被拒）与手动输入的两个输入框 ——
 * 规格 §2.1 里它们本来就是页面本地状态。
 *
 * ## 时序（规格明确要求，别改）
 *
 * **首次进入不碰相机**：只有用户点「开始使用（扫码配对）」才会申请相机权限。
 * 不授权也不影响另外两种方式 —— 权限被拒时展开「相机权限未授予」卡，给出「重新申请」与「去系统设置」。
 *
 * ## 组件层（P2 的硬规矩①）
 *
 * 卡 / 按钮 / 输入框一律走 `ui/designsystem/QsComponents.kt`，本页**不再出现裸** `Card(...)`
 * `Button(...)` `OutlinedButton(...)` `OutlinedTextField(...)`。
 * 但**卡内的 14dp 内距与 10dp 图标间距原样留着**：它们是规格 §2.1 的内容指标，
 * `QsTokens` 的间距阶梯里没有 14 / 10 这两档（只有 12 / 16），硬套会改变观感 ——
 * 按 `docs/design-system-plan.md` §8.4 划的边界，这类内容指标暂时保留字面量。
 */
@Composable
fun PairScreen(
    pairing: PairingUiState,
    onHostChange: (String) -> Unit,
    onCodeChange: (String) -> Unit,
    onPasteLink: (String?) -> Unit,
    onScanLink: (String) -> Unit,
    onSubmit: () -> Unit,
    onBack: () -> Unit,
) {
    val context = LocalContext.current

    // ---- 扫码模块的本地状态（规格 §2.1 的 _scanActive / _requestingCamera / _cameraDenied）----
    var scanActive by rememberSaveable { mutableStateOf(false) }
    var requestingCamera by remember { mutableStateOf(false) }
    var cameraDenied by rememberSaveable { mutableStateOf(false) }

    // ---- 手动输入的两个框（规格：地址一个框、端口与配对码同一行，端口 flex1 / 码 flex2）----
    // 初值从 UiState 的 host 拆出来（深链进来时它已经填好了地址与端口）。
    var address by rememberSaveable(pairing.host) {
        mutableStateOf(pairing.host.substringBeforeLast(':', pairing.host))
    }
    var port by rememberSaveable(pairing.host) {
        mutableStateOf(pairing.host.substringAfterLast(':', "").ifBlank { DEFAULT_PAIR_PORT })
    }

    /** 把「地址 + 端口」拼成一个 host 交给 ViewModel —— 归一化与校验都在 ViewModel/`HostAddress` 里做。 */
    fun pushHost() {
        val trimmedAddress = address.trim()
        if (trimmedAddress.isEmpty()) {
            onHostChange("")
            return
        }
        val trimmedPort = port.trim()
        onHostChange(if (trimmedPort.isEmpty()) trimmedAddress else "$trimmedAddress:$trimmedPort")
    }

    val permissionLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { granted ->
        requestingCamera = false
        if (granted) {
            cameraDenied = false
            scanActive = true
        } else {
            scanActive = false
            cameraDenied = true
        }
    }

    // ---- 配对中：整页换成转圈 + 过程文案（规格 §2.1 的第一支）----
    if (pairing.busy) {
        Column(
            modifier = Modifier.fillMaxSize().padding(QsTokens.spaceXl),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            CircularProgressIndicator()
            Spacer(Modifier.height(18.dp))
            Text(
                text = pairing.status ?: "正在配对…",
                style = MaterialTheme.typography.bodyMedium,
                textAlign = TextAlign.Center,
            )
        }
        return
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(start = 16.dp, top = 16.dp, end = 16.dp, bottom = 32.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        // ── 说明卡（不是 SectionCard：规格里它是独立的卡 + Row(icon + 文案)，
        //    所以用 QsCard 的空壳形态：不引入描边，内距仍由下面那行自己给）──
        QsCard(
            modifier = Modifier.fillMaxWidth(),
            border = null,
            contentPadding = PaddingValues(0.dp),
        ) {
            Row(
                modifier = Modifier.padding(14.dp),
                verticalAlignment = Alignment.Top,
            ) {
                Icon(
                    imageVector = Icons.Outlined.Wifi,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(18.dp),
                )
                Spacer(Modifier.width(10.dp))
                Text(
                    text = "电脑和手机需要在同一个 Wi-Fi 下。\n" +
                        "Windows 端：设置 → 连接设备 → 显示二维码与 6 位配对码。",
                    style = MaterialTheme.typography.bodySmall,
                    lineHeight = MaterialTheme.typography.bodySmall.fontSize * 1.6,
                )
            }
        }

        // ── 方式一：扫码 ──
        QsSectionCard(title = "方式一：扫码", icon = Icons.Outlined.QrCodeScanner) {
            if (!scanActive) {
                Text(
                    text = "点击下面的按钮才会申请相机权限并打开扫码。" +
                        "不授权也不影响配对：方式二 / 方式三随时可用。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Spacer(Modifier.height(12.dp))
                QsPrimaryButton(
                    onClick = {
                        if (cameraDenied) {
                            // 被拒过之后系统可能不再弹框；先照常申请，用户也可以用下面的「去系统设置」。
                            cameraDenied = false
                        }
                        requestingCamera = true
                        permissionLauncher.launch(android.Manifest.permission.CAMERA)
                    },
                    modifier = Modifier.fillMaxWidth().height(46.dp),
                ) {
                    if (requestingCamera) {
                        CircularProgressIndicator(
                            strokeWidth = 2.dp,
                            modifier = Modifier.size(16.dp),
                            color = MaterialTheme.colorScheme.onPrimary,
                        )
                        Spacer(Modifier.width(8.dp))
                        Text("正在申请权限…")
                    } else {
                        Icon(
                            imageVector = Icons.Outlined.QrCodeScanner,
                            contentDescription = null,
                            modifier = Modifier.size(18.dp),
                        )
                        Spacer(Modifier.width(8.dp))
                        Text("开始使用（扫码配对）")
                    }
                }
            } else {
                QrScanner(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(240.dp)
                        .clip(RoundedCornerShape(QsTokens.radiusControl)),
                    onDecoded = { text ->
                        scanActive = false
                        onScanLink(text)
                    },
                )
            }

            if (cameraDenied) {
                Spacer(Modifier.height(12.dp))
                QsErrorCard(modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.padding(14.dp)) {
                        Row(verticalAlignment = Alignment.Top) {
                            Icon(
                                imageVector = Icons.Outlined.NoPhotography,
                                contentDescription = null,
                                tint = MaterialTheme.colorScheme.error,
                                modifier = Modifier.size(18.dp),
                            )
                            Spacer(Modifier.width(10.dp))
                            Text(
                                text = "相机权限未授予，无法扫码。可以点「重新申请」；" +
                                    "若系统已不再弹出授权框，就到系统设置里为 QuizSync 打开相机权限，" +
                                    "或改用方式二 / 方式三。",
                                style = MaterialTheme.typography.bodySmall,
                                lineHeight = MaterialTheme.typography.bodySmall.fontSize * 1.5,
                            )
                        }
                        Spacer(Modifier.height(8.dp))
                        Row {
                            QsPrimaryButton(
                                onClick = {
                                    requestingCamera = true
                                    permissionLauncher.launch(android.Manifest.permission.CAMERA)
                                },
                            ) { Text("重新申请") }
                            Spacer(Modifier.width(8.dp))
                            QsSecondaryButton(
                                onClick = {
                                    val intent = Intent(
                                        Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                                        Uri.fromParts("package", context.packageName, null),
                                    )
                                    context.startActivity(intent)
                                },
                            ) { Text("去系统设置") }
                        }
                    }
                }
            }
        }

        // ── 方式二：粘贴配对链接 ──
        QsSectionCard(
            title = "方式二：粘贴配对链接",
            subtitle = "先在电脑端点「复制配对链接」，再回到这里粘贴。",
            icon = Icons.Outlined.ContentPasteGo,
        ) {
            QsSecondaryButton(
                onClick = { onPasteLink(readClipboardText(context)) },
                modifier = Modifier.fillMaxWidth().height(44.dp),
            ) {
                Icon(
                    imageVector = Icons.Outlined.ContentPasteGo,
                    contentDescription = null,
                    modifier = Modifier.size(18.dp),
                )
                Spacer(Modifier.width(8.dp))
                Text("粘贴配对链接")
            }
        }

        // ── 方式三：手动输入 ──
        QsSectionCard(
            title = "方式三：手动输入",
            subtitle = "地址与配对码都在 Windows 端设置页的「连接设备」里。",
            icon = Icons.Outlined.Keyboard,
        ) {
            QsTextField(
                value = address,
                onValueChange = {
                    address = it
                    pushHost()
                },
                label = { Text("主机地址（如 192.168.1.23）") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
                modifier = Modifier.fillMaxWidth(),
            )
            Spacer(Modifier.height(12.dp))
            Row {
                QsTextField(
                    value = port,
                    onValueChange = {
                        port = it.filter { ch -> ch.isDigit() }.take(5)
                        pushHost()
                    },
                    label = { Text("端口") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(12.dp))
                QsTextField(
                    value = pairing.code,
                    onValueChange = onCodeChange,
                    label = { Text("6 位配对码") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
                    modifier = Modifier.weight(2f),
                )
            }
            Spacer(Modifier.height(8.dp))
            QsPrimaryButton(
                onClick = onSubmit,
                modifier = Modifier.fillMaxWidth().height(46.dp),
            ) {
                Icon(
                    imageVector = Icons.Filled.Link,
                    contentDescription = null,
                    modifier = Modifier.size(18.dp),
                )
                Spacer(Modifier.width(8.dp))
                Text("开始配对")
            }
        }

        // ── 错误卡（规格 §2.1 的最后一条：页面底部，errorContainer 半透明）──
        pairing.error?.let { message ->
            QsErrorCard(modifier = Modifier.fillMaxWidth()) {
                Row(modifier = Modifier.padding(14.dp), verticalAlignment = Alignment.Top) {
                    Icon(
                        imageVector = Icons.Outlined.ErrorOutline,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.error,
                        modifier = Modifier.size(18.dp),
                    )
                    Spacer(Modifier.width(10.dp))
                    Text(
                        text = message,
                        style = MaterialTheme.typography.bodySmall,
                        lineHeight = MaterialTheme.typography.bodySmall.fontSize * 1.5,
                    )
                }
            }
        }

        QsSecondaryButton(
            onClick = onBack,
            modifier = Modifier.fillMaxWidth(),
        ) { Text("返回") }
    }
}

/** 端口缺省值：协议里主机的首选端口（`02-transport.md`）。 */
private const val DEFAULT_PAIR_PORT = "8765"

/**
 * 读剪贴板文本。**走 Android 框架而不是 Compose 的剪贴板 API**：
 * 后者在近几个版本里换代了（旧的是 `LocalClipboardManager`，新的 `LocalClipboard` 是挂起式），
 * 用框架 API 既不用赌版本、也不会引入弃用警告 —— 本仓的绿线是「0 警告」。
 *
 * 只在用户点按钮时调用（此时本应用在前台，读剪贴板不需要额外授权）。
 */
private fun readClipboardText(context: Context): String? {
    val manager = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
    val clip = manager?.primaryClip ?: return null
    if (clip.itemCount <= 0) return null
    return clip.getItemAt(0).coerceToText(context)?.toString()
}

/**
 * 扫码视图：CameraX 预览 + ZXing 取帧解码。
 *
 * **只用 ZXing core**（不是 ML Kit）：目标机是小米平板，大概率没有 Google Play 服务，
 * 而 ML Kit 的条码识别依赖它。ZXing core 是纯 Java、离线可用、无 Play 依赖
 * （与计划 §5.2 列的备选一致）。
 *
 * 两个实现要点：
 * ① 分析器跑在**自己的单线程执行器**上（不在主线程解码）；
 * ② 每一帧**必须** `close()`，否则 ImageReader 的缓冲区很快耗尽、预览直接卡死。
 */
@Composable
private fun QrScanner(modifier: Modifier = Modifier, onDecoded: (String) -> Unit) {
    val lifecycleOwner = LocalLifecycleOwner.current
    // 用 rememberUpdatedState：分析器是长生命周期的 lambda，直接捕获 onDecoded 会一直用旧闭包。
    val currentOnDecoded by rememberUpdatedState(onDecoded)
    // 解出一帧之后不再重复回调（同一个二维码会连续命中几十帧）。
    val handled = remember { AtomicBoolean(false) }
    // 执行器与相机绑定都必须**由本 composable 自己收尾**：离开配对页时若不解绑，
    // 相机会一直被占着（预览没了、摄像头灯还亮），线程也会漏。
    val analysisExecutor = remember { Executors.newSingleThreadExecutor() }
    val boundProvider = remember { mutableStateOf<ProcessCameraProvider?>(null) }

    DisposableEffect(Unit) {
        onDispose {
            handled.set(true)
            boundProvider.value?.unbindAll()
            boundProvider.value = null
            analysisExecutor.shutdown()
        }
    }

    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            val previewView = PreviewView(ctx)
            val mainExecutor = ContextCompat.getMainExecutor(ctx)
            val providerFuture = ProcessCameraProvider.getInstance(ctx)
            providerFuture.addListener({
                val provider = providerFuture.get()
                boundProvider.value = provider
                val preview = Preview.Builder().build().also {
                    it.surfaceProvider = previewView.surfaceProvider
                }
                val analysis = ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .build()
                analysis.setAnalyzer(analysisExecutor) { image ->
                    try {
                        if (handled.get()) return@setAnalyzer
                        val text = decodeQr(image)
                        if (text != null && handled.compareAndSet(false, true)) {
                            mainExecutor.execute { currentOnDecoded(text) }
                        }
                    } finally {
                        // **每一帧都必须 close**：否则 ImageReader 的缓冲区很快耗尽、预览直接卡死。
                        image.close()
                    }
                }
                // 相机不可用（没权限 / 没有摄像头 / 被别的应用占用）时**不能让页面崩**：
                // 预览区留空，方式二与方式三照常可用。
                runCatching {
                    provider.unbindAll()
                    provider.bindToLifecycle(
                        lifecycleOwner,
                        CameraSelector.DEFAULT_BACK_CAMERA,
                        preview,
                        analysis,
                    )
                }
            }, mainExecutor)
            previewView
        },
    )
}

/** 一帧 YUV 里解一个二维码；没有就返回 null。 */
private fun decodeQr(image: ImageProxy): String? {
    val plane = image.planes.firstOrNull() ?: return null
    val buffer = plane.buffer
    val data = ByteArray(buffer.remaining())
    buffer.get(data)
    // 行距可能大于宽度（对齐填充），所以 dataWidth 传 rowStride、再按 width 裁。
    val source = PlanarYUVLuminanceSource(
        data,
        plane.rowStride,
        image.height,
        0,
        0,
        image.width,
        image.height,
        false,
    )
    val reader = MultiFormatReader().apply {
        setHints(mapOf(DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE)))
    }
    return try {
        reader.decodeWithState(BinaryBitmap(HybridBinarizer(source))).text
    } catch (_: NotFoundException) {
        null
    } catch (_: Throwable) {
        // 解不出来是常态（帧糊、没码），绝不能因为一帧异常把分析器打死。
        null
    } finally {
        reader.reset()
    }
}

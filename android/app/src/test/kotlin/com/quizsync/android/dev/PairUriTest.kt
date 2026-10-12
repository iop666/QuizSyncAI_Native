package com.quizsync.android.dev

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull

class PairUriTest {
    @Test
    fun `解析服务端真实发出的 pair_uri`() {
        val request = PairUri.parse("quizsync://pair?host=127.0.0.1&port=8765&code=699450")
        assertEquals(PairRequest(host = "127.0.0.1:8765", code = "699450"), request)
    }

    @Test
    fun `局域网地址与端口按原样保留`() {
        val request = PairUri.parse("quizsync://pair?host=192.168.1.23&port=8765&code=618517")
        assertEquals(PairRequest(host = "192.168.1.23:8765", code = "618517"), request)
    }

    @Test
    fun `没写端口时用默认 8765`() {
        assertEquals(
            PairRequest(host = "10.0.0.5:8765", code = "123456"),
            PairUri.parse("quizsync://pair?host=10.0.0.5&code=123456"),
        )
    }

    @Test
    fun `不是配对深链的一律拒绝`() {
        assertNull(PairUri.parse(null))
        assertNull(PairUri.parse(""))
        assertNull(PairUri.parse("https://pair?host=1.2.3.4&code=123456"), "别的 scheme 不认")
        assertNull(PairUri.parse("quizsync://other?host=1.2.3.4&code=123456"), "别的 authority 不认")
        assertNull(PairUri.parse("quizsync://pair?code=123456"), "缺 host")
        assertNull(PairUri.parse("quizsync://pair?host=1.2.3.4"), "缺 code")
        assertNull(PairUri.parse("quizsync://pair?host=1.2.3.4&code=12345"), "码必须是 6 位")
    }

    @Test
    fun `码里的非数字会被剔除后再判长度`() {
        assertEquals(
            PairRequest(host = "1.2.3.4:8765", code = "123456"),
            PairUri.parse("quizsync://pair?host=1.2.3.4&code=12-34-56"),
        )
    }

    /**
     * **端到端的一条**：这段深链不是手写的，而是第 168 轮探针
     * （`tools/verify_pair_qr.ps1`）从**真屏幕**上把二维码解回来的原文 ——
     * 也就是「Windows 设置页画出来的那张二维码，手机端到底认不认」。
     *
     * 为什么值得单列一条：二维码内容是 Windows 侧拼的（`QuizSync.Core/PairQr.cs`），
     * 解析是安卓侧做的（`PairUri`），两边只有 `docs/protocol.md` §2.1 这一个契约。
     * 少了这条，就只能靠人举着手机去扫一次才知道对不对。
     * 注意它同时钉住两件事：`sid`（服务端 device_id）与 `v`（协议版本）这些**多余参数
     * 不能把解析搞崩**，以及局域网地址 + 端口要原样保留。
     */
    @Test
    fun `解析 Windows 真机上画出来的那张二维码（探针解回来的原文）`() {
        val request = PairUri.parse(
            "quizsync://pair?host=192.168.101.23&port=8765&code=221500&sid=windows-local&v=1",
        )

        assertEquals(PairRequest(host = "192.168.101.23:8765", code = "221500"), request)
    }
}

package com.quizsync.android.dev.ui

import com.quizsync.android.dev.model.CollectionGroup
import com.quizsync.android.dev.model.qsSelectionLabel
import com.quizsync.android.dev.model.qsToggleCollectionSelection
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 历史页「合集多选删除」的纯逻辑（规格 §3.4：长按合集分组进入多选模式）。
 *
 * 为什么值得单独测（都是容易写错、而且错了用户会直接碰到的规则）：
 * ① **「未分类」不是真实合集**，不能进选择集 —— 否则「删除」按钮会把一个不存在的对象当目标；
 * ② 取消最后一个选中项要**退出多选**（`null`）—— 停在「在多选但一个都没选」的界面上，
 *    删除按钮灰着，用户会以为界面坏了；
 * ③ `null`（不在多选）与空集是**两种状态**，不能混。
 */
class CollectionSelectionTest {

    private fun group(id: String?) = CollectionGroup(
        collectionId = id,
        title = id ?: "未分类",
        count = 3,
    )

    @Test
    fun `长按未分类不进选择集`() {
        assertNull(qsToggleCollectionSelection(null, group(null)))
        // 已经在多选里时，点未分类不改变选择（不是"清空选择"）
        val current = setOf("c-1")
        assertEquals(current, qsToggleCollectionSelection(current, group(null)))
    }

    @Test
    fun `选中一个之后再点它退出多选`() {
        val once = qsToggleCollectionSelection(null, group("c-1"))
        assertEquals(setOf("c-1"), once)

        // 取消最后一个 → 回到「不在多选」（null），不是空集
        assertNull(qsToggleCollectionSelection(once, group("c-1")))
    }

    @Test
    fun `多选里可以同时选中多个`() {
        var selection = qsToggleCollectionSelection(null, group("c-1"))
        selection = qsToggleCollectionSelection(selection, group("c-2"))
        selection = qsToggleCollectionSelection(selection, group("c-3"))
        assertEquals(setOf("c-1", "c-2", "c-3"), selection)

        // 取消中间那个，其余保留
        selection = qsToggleCollectionSelection(selection, group("c-2"))
        assertEquals(setOf("c-1", "c-3"), selection)
    }

    @Test
    fun `计数文案与选择集大小一致`() {
        assertEquals("已选 1 个合集", qsSelectionLabel(1))
        assertEquals("已选 3 个合集", qsSelectionLabel(3))
        assertTrue(qsSelectionLabel(2).contains("2"))
    }
}

using QuizSync.Core;
using QuizSync.Provider;

namespace QuizSync.App;

/// <summary>
/// 多页暂存区（1.1.0 的「多页识别」）。
///
/// 契约（ui-spec-desktop §1.2.2 与 `AGENTS.md` M46）：
/// * **F9** 进入多页并抓第一张，继续按接着加；
/// * **F8** 结束多页，把已抓的图**一次**上传识别；
/// * 抓满上限（默认 6、硬上限 6）**自动上传**，不用再按键；
/// * 悬浮球左键长按 ≥500ms 或右键 = 加一页（与 F9 等价）；
/// * 页按抓取顺序排列；「清空暂存区」丢弃全部暂存页、不上传。
///
/// 暂存只在内存里（1.1.0 也是这样）：它是「还没提交的一次识别」，落库发生在结束上传那一刻。
/// </summary>
internal sealed class MultipageStaging
{
    /// <summary>硬上限 6 页（`kHardMaxPagesPerTask`，1.1.0 与规格一致）。</summary>
    public const int HardMaxPages = 6;

    private readonly List<CapturedJpeg> _pages = [];

    /// <summary>
    /// 当前上限：设置里的值再夹到硬上限（设置项允许 1–6）。
    /// 缓存成字段而不是每次读设置：**一次攒页过程里上限必须是稳定的** ——
    /// 边攒边被改会让「抓满自动上传」在攒到一半时突然触发。
    /// </summary>
    private int _limit = Math.Clamp(AppSettings.Load().MultiPageLimit, 1, HardMaxPages);

    /// <summary>页数变了（加页 / 清空 / 取走）。界面据此显隐暂存条、托盘据此刷新文案。</summary>
    public event Action? Changed;

    public int Count => _pages.Count;

    /// <summary>当前上限（每次加页时从设置刷新一次）。</summary>
    public int Limit => _limit;

    public IReadOnlyList<CapturedJpeg> Pages => _pages;

    /// <summary>
    /// 追加一页。返回 true 表示**已经抓满**（调用方应当立刻结束并上传）。
    /// 满了之后再调不会越界（页面不会被塞进第 7 张）。
    /// </summary>
    public bool Append(CapturedJpeg page)
    {
        ArgumentNullException.ThrowIfNull(page);

        // 每次加页刷新一次上限：设置改完立刻生效，而攒页过程中保持稳定。
        _limit = Math.Clamp(AppSettings.Load().MultiPageLimit, 1, HardMaxPages);

        if (_pages.Count >= _limit)
        {
            return true;
        }

        _pages.Add(page);
        Changed?.Invoke();
        return _pages.Count >= _limit;
    }

    /// <summary>取走全部暂存页并清空（结束多页上传时用）。</summary>
    public IReadOnlyList<CapturedJpeg> TakeAll()
    {
        var taken = _pages.ToArray();
        _pages.Clear();
        Changed?.Invoke();
        return taken;
    }

    /// <summary>丢弃全部暂存页，不上传。</summary>
    public void Clear()
    {
        if (_pages.Count == 0)
        {
            return;
        }

        _pages.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// 暂存条的提示语（三种状态，逐字照 ui-spec-desktop §1.2.2）。
    ///
    /// **委托给 `QuizSync.Core` 的 <see cref="UiText.StagingHint"/>，不在这里重写一遍** ——
    /// 原来两处各写了一份一模一样的文案，那是漂移隐患（改一处忘另一处，
    /// 界面上就会出现自相矛盾的两句提示）；而且 Core 那份当时没有任何调用点，是死代码。
    /// 现在文案只有一处，且它落在可单测的 Core 里。
    /// </summary>
    public string Hint() => UiText.StagingHint(Count, Limit);
}

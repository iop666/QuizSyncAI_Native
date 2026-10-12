using System.Security.Cryptography;
using System.Text;

namespace QuizSync.Provider;

/// <summary>
/// 本机敏感值（AI Key、设备令牌）的**加密落盘**。
///
/// 之前这两样都是**明文**写在 `ai.json` 与 `device.token` 里 —— 设置页上还专门写明了「目前明文」。
/// 这一版用 **DPAPI**（`ProtectedData`，`CurrentUser` 作用域）加密：只有**同一台机器上的
/// 同一个 Windows 用户**能解开，把文件拷到别的机器 / 别的账户都打不开。
///
/// **边界要说清楚**（不夸大）：DPAPI 挡的是「**文件被拷走**」这一类风险，
/// 它**挡不住**同一用户下运行的程序 —— 解密本来就要以该用户身份进行。
/// 所以它不是保险箱，只是把「明文躺在磁盘上」变成「密文躺在磁盘上」。
///
/// 格式：`dpapi:` 前缀 + Base64 密文。读到**没有前缀**的值时按明文处理（下次保存时升级为密文），
/// 这样老配置文件不会因为这次改动而失效，也不会静默丢掉用户已经填好的 Key。
///
/// **位置说明**（本轮从 `QuizSync.App` 搬到这里）：现在有**两个**进程读同一份 `ai.json`
/// —— 带界面的应用与无界面的 Provider（`QuizSync.Provider.Cli provider`）。
/// 各写一份实现的话，「怎么加密、`dpapi:` 前缀什么含义、解不开怎么办」就有两个真相，
/// 而 `QuizSync.Provider` 正是两者共同的下游，所以放这里。
/// </summary>
public static class SecretStore
{
    private const string Prefix = "dpapi:";

    /// <summary>加密。加密不可用时**返回原文** —— 宁可不加密，也不能让用户配置不了。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain) || plain.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return plain;
        }

        // `ProtectedData` 是 Windows 专有 API，而本项目的目标框架是平台中立的 `net10.0`
        // （只有 UI 与 CLI 那两个项目带 `-windows`）。这行判断不是装饰：不加它过不了 CA1416，
        // 而那条规则是对的 —— 如实标注平台即可，不要抑制分析器。
        if (!OperatingSystem.IsWindows())
        {
            return plain;
        }

        try
        {
            return Prefix + Convert.ToBase64String(ProtectOnWindows(plain));
        }
        catch (CryptographicException)
        {
            return plain;
        }
    }

    /// <summary>解密。老配置里的明文原样返回（下次保存时会被升级成密文）。</summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(UnprotectOnWindows(stored));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            // 换个 Windows 用户或换台机器就解不开 —— 这不是崩溃的理由：
            // 当作「没有配置」返回，界面会提示用户重新填一次。
            return string.Empty;
        }
    }

    /// <summary>这个值是不是已经加密过了（设置页用它来如实告诉用户当前状态）。</summary>
    public static bool IsProtected(string stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] ProtectOnWindows(string plain) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), optionalEntropy: null, DataProtectionScope.CurrentUser);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] UnprotectOnWindows(string stored) =>
        ProtectedData.Unprotect(
            Convert.FromBase64String(stored[Prefix.Length..]), optionalEntropy: null, DataProtectionScope.CurrentUser);
}

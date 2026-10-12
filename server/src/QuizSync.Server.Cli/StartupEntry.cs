using System.Text;

namespace QuizSync.Server.Cli;

/// <summary>
/// 「随用户登录启动」的落点。
///
/// **为什么不是注册表 Run 键**：本 CLI 的 TFM 是纯 `net10.0`，`Microsoft.Win32.Registry`
/// 不在基础共享框架里（要额外引包）；而启动文件夹只需要写一个文件。
///
/// **为什么用 `.vbs` 而不是 `.cmd`**：`.cmd` 在登录时会**闪一个控制台窗口**，
/// 而这个进程是要常驻后台的。`WScript.Shell.Run(..., 0, False)` 的窗口样式 0 = 完全隐藏，
/// 且 `wscript.exe` 在所有 Windows 上都在。
///
/// **代价（如实记下来，别当成没有）**：
/// ① 启动文件夹里多一个 `.vbs`，用户看得见（这是好事：可发现、可手动删）；
/// ② 少数安全软件会对「登录时运行的 .vbs」提高警惕 —— 若真遇到拦截，
///    换成注册表 `HKCU\...\Run` 只是把 <see cref="Install"/> 的实现换掉，调用方不用动；
/// ③ 它不是服务（**架构上刻意不做服务**：API Key 是 DPAPI CurrentUser 密文，
///    服务账户解不开，见 `NATIVE-2.0-PLAN` §8）。
/// </summary>
internal static class StartupEntry
{
    /// <summary>启动文件夹里的文件名（固定，便于幂等覆盖与卸载）。</summary>
    public const string FileName = "QuizSyncServer.vbs";

    public static string LauncherPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        FileName);

    public static bool IsInstalled => File.Exists(LauncherPath);

    /// <summary>这份启动项会执行什么（给 dry-run 计划与 `doctor` 看的一句话）。</summary>
    public static string Describe(string dotnetPath, string dllPath, int port, string dataDirectory) =>
        $"\"{dotnetPath}\" \"{dllPath}\" run --port {port} --data \"{dataDirectory}\"（登录时隐藏启动）";

    /// <summary>
    /// 写入启动项。**幂等**：同一路径重复执行只是覆盖，不会堆出多个。
    /// 返回写入的文件路径。
    /// </summary>
    public static string Install(string dotnetPath, string dllPath, int port, string dataDirectory)
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Directory.CreateDirectory(startup);

        // VBScript：引号在 VBS 里靠**成对的双引号**转义，所以路径一律包在 ""…"" 里。
        var command = $"\"\"{dotnetPath}\"\" \"\"{dllPath}\"\" run --port {port} --data \"\"{dataDirectory}\"\"";
        var script = new StringBuilder()
            .AppendLine("' QuizSync 主机：随用户登录隐藏启动。")
            .AppendLine("' 这个文件由 `QuizSync.Server.Cli setup --apply --autostart` 生成；")
            .AppendLine("' 卸载用 `setup --apply --no-autostart`，或直接删掉本文件。")
            .AppendLine("Set shell = CreateObject(\"WScript.Shell\")")
            .AppendLine($"shell.Run \"{command}\", 0, False")
            .ToString();

        File.WriteAllText(LauncherPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return LauncherPath;
    }

    /// <summary>移除启动项。返回 true = 确实删掉了（false = 本来就没有）。</summary>
    public static bool Remove()
    {
        if (!File.Exists(LauncherPath))
        {
            return false;
        }

        File.Delete(LauncherPath);
        return true;
    }
}

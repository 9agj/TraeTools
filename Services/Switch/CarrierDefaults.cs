namespace TraeSwitch.Services;

public static class CarrierDefaults
{
    public static string SettingsDir => TraeTools.Services.DataPaths.SwitchDir;

    public static string DefaultUserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TRAE SOLO CN");

    /// <summary>
    /// 默认 Trae 客户端可执行文件路径。
    /// 优先自动探测常见安装位置（已缓存结果），全部未命中时回退到历史默认 D 盘路径，
    /// 用户可在 settings.json 的 ClientExe 中显式覆盖。
    /// </summary>
    public static string DefaultClientExe
    {
        get
        {
            if (string.IsNullOrEmpty(_detectedExe))
            {
                lock (DetectLock)
                {
                    if (string.IsNullOrEmpty(_detectedExe))
                        _detectedExe = AutoDetectClientExe() ?? @"D:\TRAE SOLO CN\TRAE SOLO CN.exe";   // 全部未命中：回退历史默认
                }
            }
            return _detectedExe;
        }
    }

    public static string DefaultProcessName => "TRAE SOLO CN";

    private static readonly object DetectLock = new();
    private static string _detectedExe = "";

    /// <summary>清空探测缓存，下次读取 DefaultClientExe 会重新探测（供 UI「重新探测」使用）。</summary>
    public static void ResetDetection() => _detectedExe = "";

    /// <summary>按候选目录探测客户端 exe：精确文件优先，其次目录内名字含 TRAE 的主程序，最后目录内唯一 exe；均未命中返回 null。</summary>
    public static string? AutoDetectClientExe()
    {
        const string exeName = "TRAE SOLO CN.exe";
        foreach (var dir in CandidateClientDirs())
        {
            try
            {
                var exact = Path.Combine(dir, exeName);
                if (File.Exists(exact)) return exact;

                // 目录内 exe：名字含 TRAE（如 Trae.exe）优先；否则目录里只有一个 exe 时采用它（#25：Program Files 安装可能主程序名不同）
                var exes = Directory.GetFiles(dir, "*.exe");
                var hit = exes.FirstOrDefault(f =>
                    Path.GetFileNameWithoutExtension(f).Contains("TRAE", StringComparison.OrdinalIgnoreCase))
                    ?? (exes.Length == 1 ? exes[0] : null);
                if (hit != null) return hit;
            }
            catch { /* 单目录判断失败继续下一个 */ }
        }
        return null;
    }

    /// <summary>候选客户端目录：历史默认 + 各常见位置 + 任意固定盘（C/D/E…）的 Program Files / Program Files (x86)。</summary>
    private static List<string> CandidateClientDirs()
    {
        var dirs = new List<string>
        {
            @"D:\TRAE SOLO CN",                                                               // 历史默认（D 盘安装）
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TRAE SOLO CN"), // 绿色版/便携版
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TRAE SOLO CN"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TRAE SOLO CN"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TRAE SOLO CN"),
        };
        try
        {
            foreach (var drv in DriveInfo.GetDrives())
            {
                if (!drv.IsReady || drv.DriveType != DriveType.Fixed) continue;
                dirs.Add(Path.Combine(drv.RootDirectory.FullName, "Program Files", "TRAE SOLO CN"));
                dirs.Add(Path.Combine(drv.RootDirectory.FullName, "Program Files (x86)", "TRAE SOLO CN"));
            }
        }
        catch { /* 枚举磁盘失败忽略 */ }
        return dirs;
    }

    /// <summary>
    /// 自动检测「随账号切换而变化」的登录态载体（相对 DefaultUserDataDir 的路径），
    /// 用于 settings.json Fingerprint 为空时回填。优先文档化的 globalStorage/storage.json，
    /// 再补充实际的 leveldb 目录；未安装客户端或未检测到任何载体时返回空列表。
    /// </summary>
    public static List<string> DetectFingerprint()
    {
        var found = new List<string>();
        var root = DefaultUserDataDir;
        if (!Directory.Exists(root)) return found;
        try
        {
            var storage = Path.Combine(root, "User", "globalStorage", "storage.json");
            if (File.Exists(storage)) found.Add("User/globalStorage/storage.json");

            // 登录态索引（LevelDB）目录：按相对路径记录，深度受限避免遍历过深/大目录
            found.AddRange(ScanLevelDbDirs(root, depth: 0));
        }
        catch { /* 检测失败返回已找到部分 */ }
        return found.Count > 12 ? found.Take(12).ToList() : found;
    }

    private static List<string> ScanLevelDbDirs(string root, int depth, List<string>? result = null, string current = "")
    {
        result ??= new List<string>();
        if (depth > 5 || result.Count >= 12) return result;
        var dir = current.Length == 0 ? root : Path.Combine(root, current);
        foreach (var child in Directory.GetDirectories(dir))
        {
            var name = Path.GetFileName(child);
            string rel = current.Length == 0 ? name : current + "/" + name;
            if (name.Equals("leveldb", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(rel);
                if (result.Count >= 12) break;
            }
            else if (!name.StartsWith("Cache", StringComparison.OrdinalIgnoreCase)
                     && !name.Equals("Code Cache", StringComparison.OrdinalIgnoreCase))
            {
                ScanLevelDbDirs(root, depth + 1, result, rel);
            }
        }
        return result;
    }
}
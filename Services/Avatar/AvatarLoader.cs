using System.Net.Http;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TraeTools.Services.Avatar;

/// <summary>
/// 远程头像下载缓存（Avalonia 默认不加载 http 图片，需自行取字节解码）。
/// 按 URL 缓存 Bitmap，重复账号切换时零网络开销。
/// </summary>
public static class AvatarLoader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);
    private static readonly List<string> Order = new();          // 插入顺序，用于 LRU 淘汰
    private const int MaxCacheCount = 24;                        // 内存缓存上限，防止无限增长

    /// <summary>下载并解码头像；失败（网络/解码/URL 为空）返回 null，不影响 UI。</summary>
    public static async Task<IImage?> LoadAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(url, out var hit)) return hit;
        }
        try
        {
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            await using var ms = new System.IO.MemoryStream();
            await resp.Content.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new Bitmap(ms);
            lock (Cache)
            {
                // 超上限：按插入顺序淘汰最旧的，避免多账号/头像多次更换导致内存无限增长
                if (Cache.Count >= MaxCacheCount && !Cache.ContainsKey(url) && Order.Count > 0)
                {
                    var oldest = Order[0];
                    Order.RemoveAt(0);
                    Cache.Remove(oldest);
                }
                if (!Cache.ContainsKey(url)) Order.Add(url);
                Cache[url] = bmp;
            }
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>下载后切回 UI 线程赋值（Avalonia 绑定建议在 UI 线程产生 PropertyChanged）。</summary>
    public static async Task LoadIntoAsync(string? url, Action<IImage?> apply)
    {
        var img = await LoadAsync(url);
        if (img == null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => apply(img));
    }
}
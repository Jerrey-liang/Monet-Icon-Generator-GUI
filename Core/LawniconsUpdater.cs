using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace MonetIconGenerator.Core;

/// <summary>
/// Lawnicons 自动同步：GitHub API、二进制 ARSC/XML 解析、SVG 渲染。
/// 对应 Python main.py 第 329-844 行。
/// </summary>
public static class LawniconsUpdater
{
    private static readonly HttpClient Http = new()
    {
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("HyperOS-Monet-Icon-Generator", "1.0") } },
        Timeout = TimeSpan.FromSeconds(90)
    };

    // === 版本号解析 ===
    public static Version ParseVersionTag(string? tag)
    {
        tag = tag?.Trim() ?? "";
        if (tag.StartsWith("v")) tag = tag[1..];
        var parts = tag.Split(new[] { '.', '-', '+' });
        var numbers = new List<int>();
        foreach (var p in parts)
        {
            if (int.TryParse(p, out var n))
                numbers.Add(n);
            else break;
        }
        while (numbers.Count < 3) numbers.Add(0);
        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    // === 本地版本记录 ===
    public static string? ReadLocalTag()
    {
        try
        {
            if (!File.Exists(Config.LawniconsVersionJson)) return null;
            var json = File.ReadAllText(Config.LawniconsVersionJson);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("tag_name").GetString();
        }
        catch { return null; }
    }

    public static void WriteLocalVersion(JsonElement release, int pngCount, int appfilterCount)
    {
        var dir = Path.GetDirectoryName(Config.LawniconsVersionJson);
        if (dir != null) Directory.CreateDirectory(dir);
        var data = new Dictionary<string, object?>
        {
            ["tag_name"] = release.GetProperty("tag_name").GetString(),
            ["name"] = release.GetProperty("name").GetString(),
            ["html_url"] = release.GetProperty("html_url").GetString(),
            ["updated_at"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            ["png_count"] = pngCount,
            ["appfilter_item_count"] = appfilterCount
        };
        File.WriteAllText(Config.LawniconsVersionJson,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    // === GitHub API ===
    public static async Task<JsonElement> LatestStableReleaseAsync()
    {
        Http.DefaultRequestHeaders.Accept.Clear();
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var release = await Http.GetFromJsonAsync<JsonElement>(Config.LawniconsReleaseApi);
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new Exception("GitHub latest release 不是稳定版。");
        return release;
    }

    public static JsonElement FindApkAsset(JsonElement release)
    {
        var assets = release.GetProperty("assets").EnumerateArray();
        var apks = assets
            .Where(a => a.GetProperty("name").GetString()?.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        if (apks.Count == 0)
            throw new Exception("最新稳定版没有找到 APK 附件。");
        apks.Sort((a, b) =>
        {
            var an = a.GetProperty("name").GetString() ?? "";
            var bn = b.GetProperty("name").GetString() ?? "";
            int aIsLawnicons = an.Contains("lawnicons", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            int bIsLawnicons = bn.Contains("lawnicons", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            var r = aIsLawnicons.CompareTo(bIsLawnicons);
            return r != 0 ? r : string.Compare(an, bn, StringComparison.Ordinal);
        });
        return apks[0];
    }

    // === 文件下载 ===
    public static async Task DownloadFileAsync(string url, string dst, string label, ProgressReporter? reporter = null)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        using var stream = await response.Content.ReadAsStreamAsync();
        using var fs = File.Create(dst);
        var buffer = new byte[1024 * 256];
        long done = 0;
        int read;
        var sw = Stopwatch.StartNew();
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            fs.Write(buffer, 0, read);
            done += read;
            if (sw.ElapsedMilliseconds >= 200)
            {
                if (total > 0)
                    reporter?.Report((double)done / total, $"{label} ({done * 100.0 / total:F1}%)...");
                reporter?.Log($"{label} ({done / 1024} KB)...");
                sw.Restart();
            }
        }
        reporter?.Log("");
    }

    // === 二进制 ARSC 解析 ===
    // (省略了完整的手工二进制解析——翻译自 Python 版的 u16/u32/parse_android_string_pool/
    //  parse_resource_table_values/binary_xml_to_element。此处展示核心接口。)

    public static int BuildAppfilterFromApk(string apkPath, string outputPath)
    {
        using var apk = ZipFile.OpenRead(apkPath);
        var arscEntry = apk.GetEntry("resources.arsc")
                        ?? throw new Exception("APK 中未找到 resources.arsc");
        using var arscStream = arscEntry.Open();
        byte[] arscData;
        using (var ms = new MemoryStream())
        {
            arscStream.CopyTo(ms);
            arscData = ms.ToArray();
        }

        var entries = ArscParser.ParseResourceTableValues(arscData);
        if (!entries.TryGetValue(("xml", "appfilter"), out var appfilterEntry))
            throw new Exception("APK 中未找到 xml/appfilter。");

        var xmlPath = appfilterEntry.Value;
        if (string.IsNullOrEmpty(xmlPath))
            throw new Exception("appfilter value 为空。");

        var xmlEntry = apk.GetEntry(xmlPath)
                       ?? throw new Exception($"APK 中未找到 {xmlPath}");
        using var xmlStream = xmlEntry.Open();
        byte[] xmlData;
        using (var ms = new MemoryStream())
        {
            xmlStream.CopyTo(ms);
            xmlData = ms.ToArray();
        }

        var root = BinaryXmlParser.ToXElement(xmlData);
        var itemCount = root.Elements("item").Count();
        if (itemCount < 1000)
            throw new Exception($"appfilter 条目数量异常：{itemCount}");

        root.Save(outputPath);
        return itemCount;
    }

    // === SVG 提取 ===
    public static int ExtractSvgsFromSourceZip(string sourceZip, string svgDir)
    {
        if (Directory.Exists(svgDir)) Directory.Delete(svgDir, true);
        Directory.CreateDirectory(svgDir);
        int count = 0;
        using var zf = ZipFile.OpenRead(sourceZip);
        foreach (var entry in zf.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(entry.Name) || !normalized.Contains("/svgs/") ||
                !entry.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                continue;
            var outPath = Path.Combine(svgDir, Path.GetFileName(normalized));
            entry.ExtractToFile(outPath, true);
            count++;
        }
        if (count < 1000)
            throw new Exception($"源码包中的 SVG 数量异常：{count}");
        return count;
    }

    // === SVG 渲染（直接调用 C# 渲染器，不再走 PowerShell） ===
    public static void RenderSvgsToDrawableZip(string svgDir, string pngDir, string zipPath)
    {
        // 直接调用同项目内的 RenderLawniconsSvgs 类
        RenderLawniconsSvgs.Run(svgDir, pngDir, zipPath);
    }

    // === 资源完整性校验 ===
    public static (int pngCount, int itemCount) ValidateResources(string appfilterPath, string drawableZipPath, string svgDir)
    {
        var root = XElement.Load(appfilterPath);
        var appfilterDrawables = new HashSet<string>(
            root.Elements("item")
                .Select(e => e.Attribute("drawable")?.Value)
                .Where(v => v != null)!);

        var svgNames = new HashSet<string>(
            Directory.GetFiles(svgDir, "*.svg")
                .Select(f => Path.GetFileNameWithoutExtension(f)));

        var zipNames = new HashSet<string>();
        using var zf = ZipFile.OpenRead(drawableZipPath);
        foreach (var entry in zf.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith("drawable/"))
                throw new Exception("drawable.zip 顶层结构错误。");
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;
            if (entry.Length <= 0)
                throw new Exception($"drawable.zip 存在空 PNG：{name}");
            zipNames.Add(Path.GetFileNameWithoutExtension(name));
        }

        var missing = appfilterDrawables.Except(zipNames).OrderBy(x => x).Take(10).ToList();
        if (missing.Count > 0)
            throw new Exception("appfilter 中存在未生成的图标：" + string.Join(", ", missing));

        var required = new HashSet<string> { "wechat", "coolapk", "themed_icon_calendar_31" };
        if (!required.IsSubsetOf(zipNames))
            throw new Exception("关键样例图标缺失。");
        if (svgNames.Count != zipNames.Count)
            throw new Exception($"SVG 与 PNG 数量不一致：{svgNames.Count} / {zipNames.Count}");

        return (zipNames.Count, root.Elements("item").Count());
    }

    // === 备份旧资源 ===
    public static string BackupAssets(string suffix)
    {
        var backupRoot = Path.Combine(Config.BaseDir, "lawnicons_assets", "backup",
            DateTime.Now.ToString("yyyyMMdd-HHmmss") + suffix);
        Directory.CreateDirectory(backupRoot);
        foreach (var path in new[] { Config.AppfilterXml, Config.DrawableZipPath, Config.LawniconsVersionJson })
        {
            if (File.Exists(path))
                File.Copy(path, Path.Combine(backupRoot, Path.GetFileName(path)), true);
        }
        return backupRoot;
    }

    // === 清理旧缓存 ===
    public static void ClearGeneratedCache()
    {
        void SafeDeleteDir(string path) { if (Directory.Exists(path)) Directory.Delete(path, true); }
        void SafeDeleteFile(string path) { if (File.Exists(path)) File.Delete(path); }

        SafeDeleteDir(Config.DrawableDir);
        SafeDeleteDir(Config.PreprocessDir);
        SafeDeleteDir(Config.PreprocessNightDir);
        SafeDeleteFile(Config.ThemeFallbackXml);
        SafeDeleteFile(Config.GeneralXmlPath);
    }

    // === 主更新流程 ===
    public static async Task UpdateResourcesAsync(JsonElement release, ProgressReporter? reporter = null)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "unknown";
        var apkAsset = FindApkAsset(release);

        var workDir = Path.Combine(Config.BaseDir, Config.TempDir, "_lawnicons_auto_update",
            System.Text.RegularExpressions.Regex.Replace(tag, @"[^A-Za-z0-9_.-]", "_"));
        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        Directory.CreateDirectory(workDir);

        var apkPath = Path.Combine(workDir, apkAsset.GetProperty("name").GetString()!);
        var sourceZip = Path.Combine(workDir, "source.zip");
        var svgDir = Path.Combine(workDir, "svgs");
        var pngDir = Path.Combine(workDir, "drawable");
        var newAppfilter = Path.Combine(workDir, "appfilter_plain.xml");
        var newDrawableZip = Path.Combine(workDir, "drawable.zip");

        reporter?.Log($"正在同步 Lawnicons {tag} 稳定版资源...");
        var apkUrl = apkAsset.GetProperty("browser_download_url").GetString()!;
        var zipUrl = release.GetProperty("zipball_url").GetString()!;
        await DownloadFileAsync(apkUrl, apkPath, "下载 Lawnicons APK", reporter);
        await DownloadFileAsync(zipUrl, sourceZip, "下载 Lawnicons 源码", reporter);

        reporter?.Log("正在解析 appfilter...");
        var appfilterCount = BuildAppfilterFromApk(apkPath, newAppfilter);

        reporter?.Log("正在提取并渲染 SVG 图标...");
        ExtractSvgsFromSourceZip(sourceZip, svgDir);
        RenderSvgsToDrawableZip(svgDir, pngDir, newDrawableZip);

        reporter?.Log("正在校验资源对应关系...");
        var (pngCount, _) = ValidateResources(newAppfilter, newDrawableZip, svgDir);

        var backupDir = BackupAssets("-auto-lawnicons");
        File.Copy(newAppfilter, Config.AppfilterXml, true);
        File.Copy(newDrawableZip, Config.DrawableZipPath, true);
        WriteLocalVersion(release, pngCount, appfilterCount);
        ClearGeneratedCache();
        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);

        reporter?.Log($"Lawnicons 已更新到 {tag}，旧资源已备份至 {backupDir}");
    }

    // === 启动时检查资源完整性 ===
    public static List<string> ResourceProblems()
    {
        var problems = new List<string>();
        if (!File.Exists(Config.AppfilterXml))
        {
            problems.Add($"缺少 {Config.AppfilterXml}");
        }
        else
        {
            try
            {
                var root = XElement.Load(Config.AppfilterXml);
                var count = root.Elements("item").Count();
                if (count < Config.LawniconsMinResourceCount)
                    problems.Add($"{Config.AppfilterXml} 条目数量异常：{count}");
            }
            catch (Exception ex) { problems.Add($"{Config.AppfilterXml} 无法读取：{ex.Message}"); }
        }

        if (!File.Exists(Config.DrawableZipPath))
        {
            problems.Add($"缺少 {Config.DrawableZipPath}");
        }
        else
        {
            try
            {
                using var zf = ZipFile.OpenRead(Config.DrawableZipPath);
                var pngCount = zf.Entries.Count(e => e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                if (pngCount < Config.LawniconsMinResourceCount)
                    problems.Add($"{Config.DrawableZipPath} 图标数量异常：{pngCount}");
            }
            catch (Exception ex) { problems.Add($"{Config.DrawableZipPath} 无法读取：{ex.Message}"); }
        }
        return problems;
    }

    public static async Task EnsureResourcesAsync(ProgressReporter? reporter = null)
    {
        var problems = ResourceProblems();
        if (problems.Count == 0) return;

        if (Environment.GetEnvironmentVariable("MONET_SKIP_LAWNICONS_UPDATE") == "1")
            throw new Exception("Lawnicons 自动下载已被 MONET_SKIP_LAWNICONS_UPDATE=1 关闭：" +
                                string.Join("；", problems));

        reporter?.Log("检测到 Lawnicons 本地资源缺失或损坏，将自动重新下载：");
        foreach (var p in problems) reporter?.Log($" - {p}");

        var release = await LatestStableReleaseAsync();
        await UpdateResourcesAsync(release, reporter);

        var remaining = ResourceProblems();
        if (remaining.Count > 0)
            throw new Exception("自动下载后资源仍不可用：" + string.Join("；", remaining));
    }
}

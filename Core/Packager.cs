using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace MonetIconGenerator.Core;

/// <summary>
/// 图标打包：icons 文件生成、Magisk 模块打包、MTZ 主题打包。
/// 对应 Python main.py 第 846-989 行 + 第 1207-1311 行。
/// </summary>
public static class Packager
{
    // === 功能4：打包 icons ===
    public static void IconPackage(
        bool enableDarkMode, bool lightMode,
        ProgressReporter? reporter = null)
    {
        var outputPath = Config.OutputIconsFull;

        if (enableDarkMode)
            PackageFancyIcons(outputPath, reporter);
        else
            PackageSimpleIcons(outputPath, lightMode, reporter);
    }

    private static void PackageFancyIcons(string outputPath, ProgressReporter? reporter)
    {
        // 读取 appfilter 和 name_mapping
        var (packageNames, validItems) = ParseAppfilterItems();
        var filteredMapping = LoadNameMapping();

        // 构建 包名→drawable 字典：先填 appfilter，再用 name_mapping 覆盖
        var packageDrawable = new Dictionary<string, string>();
        foreach (var (fullPath, drawable) in validItems)
        {
            var packageName = fullPath.Split('/')[0];
            if (!packageDrawable.ContainsKey(packageName))
                packageDrawable[packageName] = drawable;
        }
        foreach (var kv in filteredMapping)
        {
            var pkg = kv.Key.Contains('/') ? kv.Key.Split('/')[0] : kv.Key;
            // 仅当 drawable PNG 确实存在才覆盖，否则保留 appfilter 的值
            var pngPath = Path.Combine(Config.PreprocessDir, $"{kv.Value}.png");
            if (File.Exists(pngPath))
                packageDrawable[pkg] = kv.Value;
        }
        // 排除日历包（单独处理）
        packageDrawable.Remove("com.android.calendar");

        int total = packageDrawable.Count;
        int current = 0;

        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        foreach (var (packageName, drawable) in packageDrawable)
        {
            WriteFancyIconEntry(zip, packageName, drawable);
            current++;
            reporter?.Report((double)current / total);
        }

        // 日历图标（浅色 + 深色）
        for (int i = 1; i <= 31; i++)
        {
            var srcLight = Path.Combine(Config.PreprocessDir, $"themed_icon_calendar_{i}.png");
            if (File.Exists(srcLight))
                zip.CreateEntryFromFile(srcLight,
                    $"fancy_icons/com.android.calendar/calendar_0/themed_icon_calendar_{i}.png");
        }
        for (int i = 1; i <= 31; i++)
        {
            var srcDark = Path.Combine(Config.PreprocessNightDir, $"themed_icon_calendar_{i}.png");
            if (File.Exists(srcDark))
                zip.CreateEntryFromFile(srcDark,
                    $"fancy_icons/com.android.calendar/calendar_1/themed_icon_calendar_{i}.png");
        }

        // manifest-duo.xml
        if (File.Exists(Config.CalendarDuoXmlPath))
            zip.CreateEntryFromFile(Config.CalendarDuoXmlPath,
                "fancy_icons/com.android.calendar/manifest.xml");

        // transform_config.xml
        if (File.Exists(Config.GeneralXmlPath))
            zip.CreateEntryFromFile(Config.GeneralXmlPath, "transform_config.xml");
    }

    private static void PackageSimpleIcons(string outputPath, bool lightMode, ProgressReporter? reporter)
    {
        var inputDir = lightMode ? Config.PreprocessDir : Config.PreprocessNightDir;
        var pngFiles = Directory.GetFiles(inputDir, "*.png");

        int total = pngFiles.Length;
        int current = 0;

        using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        foreach (var srcFile in pngFiles)
        {
            var fileName = Path.GetFileName(srcFile);
            zip.CreateEntryFromFile(srcFile, $"{Config.ResDir}/{fileName}");
            current++;
            reporter?.Report((double)current / total);
        }

        // 日历图标
        for (int i = 1; i <= 31; i++)
        {
            var src = Path.Combine(Config.DrawableDir, $"themed_icon_calendar_{i}.png");
            if (File.Exists(src))
                zip.CreateEntryFromFile(src,
                    $"fancy_icons/com.android.calendar/calendar/themed_icon_calendar_{i}.png");
        }

        // manifest.xml（单模式日历）
        if (File.Exists(Config.CalendarXmlPath))
            zip.CreateEntryFromFile(Config.CalendarXmlPath,
                "fancy_icons/com.android.calendar/manifest.xml");

        // theme_fallback.xml 和 transform_config.xml
        if (File.Exists(Config.ThemeFallbackXml))
            zip.CreateEntryFromFile(Config.ThemeFallbackXml, "theme_fallback.xml");
        if (File.Exists(Config.GeneralXmlPath))
            zip.CreateEntryFromFile(Config.GeneralXmlPath, "transform_config.xml");
    }

    private static void WriteFancyIconEntry(ZipArchive zip, string packageName, string drawable)
    {
        var folder = $"{Config.FancyIconsDir}/{packageName}";
        var srcLight = Path.Combine(Config.PreprocessDir, $"{drawable}.png");
        var srcDark = Path.Combine(Config.PreprocessNightDir, $"{drawable}.png");

        if (!File.Exists(srcLight) || !File.Exists(srcDark) || !File.Exists(Config.SubXmlPath))
            return;

        zip.CreateEntryFromFile(srcLight, $"{folder}/iconBg_0.png");
        zip.CreateEntryFromFile(srcDark, $"{folder}/iconBg_1.png");
        zip.CreateEntryFromFile(Config.SubXmlPath, $"{folder}/manifest.xml");
    }

    // === 解析 appfilter XML ===
    private static (HashSet<string> packages, List<(string, string)> items) ParseAppfilterItems()
    {
        var packages = new HashSet<string>();
        var items = new List<(string, string)>();

        if (!File.Exists(Config.AppfilterXml)) return (packages, items);

        var root = XElement.Load(Config.AppfilterXml);
        foreach (var item in root.Elements("item"))
        {
            var component = item.Attribute("component")?.Value ?? "";
            var drawable = item.Attribute("drawable")?.Value ?? "";
            if (string.IsNullOrEmpty(component) || string.IsNullOrEmpty(drawable))
                continue;
            if (!component.StartsWith("ComponentInfo{") || !component.EndsWith("}"))
                continue;

            var compStr = component["ComponentInfo{".Length..^1];
            var slashIdx = compStr.IndexOf('/');
            if (slashIdx < 0) continue;

            var fullPath = compStr;
            var packageName = compStr[..slashIdx];
            var clsName = compStr[(slashIdx + 1)..];
            if (clsName.Contains('*')) continue;

            packages.Add(packageName);
            items.Add((fullPath, drawable));
        }

        return (packages, items);
    }

    private static Dictionary<string, string> LoadNameMapping()
    {
        if (!File.Exists(Config.NameMapping)) return new();

        try
        {
            var json = File.ReadAllText(Config.NameMapping, System.Text.Encoding.UTF8);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return data?
                .Where(kv => !kv.Key.StartsWith("_comment-"))
                .ToDictionary(kv => kv.Key, kv => kv.Value)
                ?? new();
        }
        catch { return new(); }
    }

    // === 功能5：Magisk 模块打包 ===
    public static void PackageMagisk()
    {
        if (!File.Exists(Config.OutputIconsFull))
            throw new Exception("找不到 icons 文件，请先生成图标包。");

        // 复制模板
        if (Directory.Exists(Config.PackMagiskTemp))
            Directory.Delete(Config.PackMagiskTemp, true);
        CopyDirectory(Config.PackMagisk, Config.PackMagiskTemp);

        // 修改 module.prop
        var modulePropPath = Path.Combine(Config.PackMagiskTemp, "module.prop");
        if (File.Exists(modulePropPath))
        {
            var lines = File.ReadAllLines(modulePropPath, System.Text.Encoding.UTF8).ToList();
            var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            if (lines.Count > 0)
                lines[^1] = lines[^1].TrimEnd() + nowStr + "\n";
            File.WriteAllLines(modulePropPath, lines, System.Text.Encoding.UTF8);
        }

        // 打包
        using var zip = ZipFile.Open(Config.PackMagiskOutputFull, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(Config.PackMagiskTemp, "*", SearchOption.AllDirectories))
        {
            var arcname = Path.GetRelativePath(Config.PackMagiskTemp, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, arcname);
        }
        zip.CreateEntryFromFile(Config.OutputIconsFull,
            "product/media/theme/default/icons");
    }

    // === 功能6：MTZ 主题打包 ===
    public static void PackageMtz()
    {
        if (!File.Exists(Config.OutputIconsFull))
            throw new Exception("找不到 icons 文件，请先生成图标包。");

        // 复制模板
        if (Directory.Exists(Config.PackMtzTemp))
            Directory.Delete(Config.PackMtzTemp, true);
        CopyDirectory(Config.PackMtz, Config.PackMtzTemp);

        // 修改 description.xml（防时间戳累积的 regex 替换）
        var descPath = Path.Combine(Config.PackMtzTemp, "description.xml");
        if (File.Exists(descPath))
        {
            var content = File.ReadAllText(descPath, System.Text.Encoding.UTF8);
            var nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            content = System.Text.RegularExpressions.Regex.Replace(
                content, @"(构建时间：).*?(?=\n|</)", "$1" + nowStr);
            File.WriteAllText(descPath, content, System.Text.Encoding.UTF8);
        }

        // 打包
        using var zip = ZipFile.Open(Config.PackMtzOutputFull, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(Config.PackMtzTemp, "*", SearchOption.AllDirectories))
        {
            var arcname = Path.GetRelativePath(Config.PackMtzTemp, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, arcname);
        }
        zip.CreateEntryFromFile(Config.OutputIconsFull, "icons");
    }

    // === drawable 缓存管理 ===
    public static bool DrawableCacheIsValid()
    {
        if (!Directory.Exists(Config.DrawableDir)) return false;
        if (!File.Exists(Config.DrawableZipPath)) return false;

        using var zf = ZipFile.OpenRead(Config.DrawableZipPath);
        var expected = new HashSet<string>(
            zf.Entries
                .Where(e => e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Select(e => Path.GetFileName(e.FullName.Replace('\\', '/')))
        );
        var actual = new HashSet<string>(
            Directory.GetFiles(Config.DrawableDir, "*.png")
                .Select(Path.GetFileName)
        );

        if (!expected.SetEquals(actual)) return false;

        foreach (var name in actual)
        {
            var path = Path.Combine(Config.DrawableDir, name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return false;
        }
        return true;
    }

    public static void ExtractDrawableCache(ProgressReporter? reporter = null)
    {
        if (!File.Exists(Config.DrawableZipPath))
            throw new Exception("drawable.zip 不存在。");

        using var zf = ZipFile.OpenRead(Config.DrawableZipPath);
        var fileList = zf.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();

        if (DrawableCacheIsValid()) return;

        if (Directory.Exists(Config.DrawableDir))
            Directory.Delete(Config.DrawableDir, true);
        Directory.CreateDirectory(Config.DrawableDir);

        for (int i = 0; i < fileList.Count; i++)
        {
            var entry = fileList[i];
            var destPath = Path.Combine(Config.BaseDir, Config.TempDir, entry.FullName);
            var destDir = Path.GetDirectoryName(destPath);
            if (destDir != null) Directory.CreateDirectory(destDir);
            entry.ExtractToFile(destPath, true);
            reporter?.Report((double)(i + 1) / fileList.Count);
        }

        if (!DrawableCacheIsValid())
            throw new Exception("资源文件解压不完整，请删除 temp/drawable 后重试。");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }
}

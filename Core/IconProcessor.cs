using System.Drawing;
using System.Drawing.Imaging;
using System.Xml.Linq;

namespace MonetIconGenerator.Core;

/// <summary>
/// 图标预处理：将 Lawnicons PNG 按 Monet 颜色重绘前景/背景，套用遮罩。
/// 对应 Python main.py 第 226-270 + 第 270-320 行。
/// </summary>
public static class IconProcessor
{
    // === 图标合成 ===
    public static Bitmap GenerateIcon(
        string foregroundColor, string backgroundColor,
        Image baseImg, Image clipAlpha)
    {
        var fgColor = ColorTranslator.FromHtml(foregroundColor);
        var bgColor = ColorTranslator.FromHtml(backgroundColor);

        // 背景层
        var bg = new Bitmap(clipAlpha.Width, clipAlpha.Height);
        using (var g = Graphics.FromImage(bg))
        {
            g.Clear(bgColor);
        }

        // 前景图（baseImg 尺寸），填充前景色 + 原图 alpha
        var fgRaw = new Bitmap(baseImg.Width, baseImg.Height);
        using (var g = Graphics.FromImage(fgRaw))
        {
            g.Clear(fgColor);
        }

        // 应用原图 alpha 到前景
        var fgAlpha = (baseImg as Bitmap) ?? new Bitmap(baseImg);
        ApplyAlphaChannel(fgRaw, fgAlpha);

        // 创建前景画布（clipAlpha 尺寸），将前景居中粘贴
        var fg = new Bitmap(clipAlpha.Width, clipAlpha.Height);
        using (var g = Graphics.FromImage(fg))
        {
            g.Clear(Color.Transparent);
            var offsetX = (clipAlpha.Width - fgRaw.Width) / 2;
            var offsetY = (clipAlpha.Height - fgRaw.Height) / 2;
            g.DrawImage(fgRaw, offsetX, offsetY);
        }

        // 合成：bg + fg
        var composed = AlphaComposite(bg, fg);

        // 应用 clip.png 的 alpha 通道
        var clipBmp = clipAlpha as Bitmap ?? new Bitmap(clipAlpha);
        ApplyAlphaChannel(composed, clipBmp);

        return composed;
    }

    // === 批量处理 ===
    public static void ProcessFile(
        string fileName, string inputDir,
        string accent1_100, string accent1_200, string accent1_700,
        string clipPngPath)
    {
        Directory.CreateDirectory(Config.PreprocessDir);
        Directory.CreateDirectory(Config.PreprocessNightDir);

        var inputPath = Path.Combine(inputDir, fileName);
        using var baseImg = Image.FromFile(inputPath);
        using var clipImg = Image.FromFile(clipPngPath);

        // 浅色图标：前景 accent1_700，背景 accent1_100
        using var iconLight = GenerateIcon(accent1_700, accent1_100, baseImg, clipImg);
        iconLight.Save(Path.Combine(Config.PreprocessDir, fileName), ImageFormat.Png);

        // 深色图标：前景 accent1_200，背景 accent1_700
        using var iconDark = GenerateIcon(accent1_200, accent1_700, baseImg, clipImg);
        iconDark.Save(Path.Combine(Config.PreprocessNightDir, fileName), ImageFormat.Png);
    }

    // === theme_fallback.xml 生成 ===
    public static void CreateThemeFallbackXml()
    {
        Console.WriteLine($"正在生成 {Config.ThemeFallbackXml}...");

        // 读取 name_mapping（去注释键）
        var nameMapping = new Dictionary<string, string>();
        if (File.Exists(Config.NameMapping))
        {
            var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Config.NameMapping, System.Text.Encoding.UTF8));
            if (json != null)
                foreach (var kv in json)
                    if (!kv.Key.StartsWith("_comment-"))
                        nameMapping[kv.Key] = kv.Value;
        }

        // 构建 包名→drawable 字典：先填 appfilter，再用 name_mapping 覆盖
        var packageDrawable = new Dictionary<string, string>();

        if (File.Exists(Config.AppfilterXml))
        {
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

                var pkgName = compStr[..slashIdx];
                var clsName = compStr[(slashIdx + 1)..];
                if (clsName.Contains('*')) continue;
                if (packageDrawable.ContainsKey(pkgName)) continue;

                packageDrawable[pkgName] = drawable;
            }
        }

        // name_mapping 覆盖（带'/'的键提取包名）
        foreach (var kv in nameMapping)
        {
            var pkgName = kv.Key.Contains('/') ? kv.Key.Split('/')[0] : kv.Key;
            packageDrawable[pkgName] = kv.Value;
        }

        var lines = new List<string>
        {
            "<?xml version='1.0' encoding='utf-8' standalone='yes'?>",
            "<MIUI_Theme_Values>"
        };
        foreach (var kv in packageDrawable)
            lines.Add($"<drawable name=\"{kv.Key}.png\">{kv.Value}.png</drawable>");
        lines.Add("</MIUI_Theme_Values>");

        File.WriteAllText(Config.ThemeFallbackXml, string.Join("\n", lines), System.Text.Encoding.UTF8);
    }

    // === 工具：应用 alpha 通道 ===
    private static void ApplyAlphaChannel(Bitmap target, Bitmap alphaSource)
    {
        var rect = new Rectangle(0, 0, Math.Min(target.Width, alphaSource.Width),
                                         Math.Min(target.Height, alphaSource.Height));
        var targetData = target.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var alphaData = alphaSource.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        unsafe
        {
            for (int y = 0; y < rect.Height; y++)
            {
                var targetRow = (byte*)targetData.Scan0 + y * targetData.Stride;
                var alphaRow = (byte*)alphaData.Scan0 + y * alphaData.Stride;
                for (int x = 0; x < rect.Width; x++)
                {
                    // BGRA 格式：target[3]=alpha, alphaSource[3]=source alpha
                    targetRow[x * 4 + 3] = alphaRow[x * 4 + 3];
                }
            }
        }

        target.UnlockBits(targetData);
        alphaSource.UnlockBits(alphaData);
    }

    // === 工具：AlphaComposite（bg + fg） ===
    private static Bitmap AlphaComposite(Bitmap bg, Bitmap fg)
    {
        var result = new Bitmap(bg.Width, bg.Height);
        var rect = new Rectangle(0, 0, Math.Min(bg.Width, fg.Width), Math.Min(bg.Height, fg.Height));
        var bgData = bg.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var fgData = fg.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var resultData = result.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        unsafe
        {
            for (int y = 0; y < rect.Height; y++)
            {
                var bgRow = (byte*)bgData.Scan0 + y * bgData.Stride;
                var fgRow = (byte*)fgData.Scan0 + y * fgData.Stride;
                var rRow = (byte*)resultData.Scan0 + y * resultData.Stride;
                for (int x = 0; x < rect.Width; x++)
                {
                    var i = x * 4;
                    float fgA = fgRow[i + 3] / 255f;
                    float bgA = bgRow[i + 3] / 255f;

                    // "over" 合成：r = fg + bg*(1 - fg_a)
                    rRow[i + 0] = (byte)(fgRow[i + 0] * fgA + bgRow[i + 0] * bgA * (1 - fgA));
                    rRow[i + 1] = (byte)(fgRow[i + 1] * fgA + bgRow[i + 1] * bgA * (1 - fgA));
                    rRow[i + 2] = (byte)(fgRow[i + 2] * fgA + bgRow[i + 2] * bgA * (1 - fgA));
                    rRow[i + 3] = (byte)(Math.Min(255, (fgA + bgA * (1 - fgA)) * 255));
                }
            }
        }

        bg.UnlockBits(bgData);
        fg.UnlockBits(fgData);
        result.UnlockBits(resultData);
        return result;
    }
}

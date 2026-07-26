using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MonetIconGenerator.Core;

/// <summary>
/// 颜色管理：ADB 取色、配置文件读写、预填合法性校验。
/// 对应 Python main.py 第 62-224 行。
/// </summary>
public static class ColorManager
{
    private static readonly Regex HexColorRe = new(
        @"(?:#|0x)([0-9a-fA-F]{6}|[0-9a-fA-F]{8})\b",
        RegexOptions.Compiled);

    // === JSON 尾部多余逗号修复 ===
    public static void FixColorJson(string path)
    {
        if (!File.Exists(path)) return;
        var content = File.ReadAllText(path, System.Text.Encoding.UTF8);
        // 去掉最后一个 } 前的多余逗号（支持多行）
        var fixedContent = Regex.Replace(content, @",\s*(\n\s*})", "$1", RegexOptions.RightToLeft);
        if (fixedContent != content)
            File.WriteAllText(path, fixedContent, System.Text.Encoding.UTF8);
    }

    // === 校验颜色配置 ===
    public static bool ValidateColors(Dictionary<string, string> colors)
    {
        string[] required = { "accent1_100", "accent1_200", "accent1_700" };
        return required.All(key =>
            colors.TryGetValue(key, out var val) &&
            val.StartsWith('#') &&
            (val.Length == 7 || val.Length == 9));
    }

    // === 读取现有 colors.json ===
    public static Dictionary<string, string> LoadExistingColors()
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, Config.ColorsJson);
        try
        {
            FixColorJson(jsonPath);
            if (!File.Exists(jsonPath)) return new();
            var text = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new();
        }
        catch { return new(); }
    }

    // === 规范化 ADB 返回的颜色值 ===
    public static string NormalizeAdbColor(string rawValue)
    {
        var text = rawValue?.Trim() ?? "";
        var match = HexColorRe.Match(text);
        if (!match.Success)
            throw new FormatException($"无法解析颜色值：{text}");

        var hex = match.Groups[1].Value.ToUpperInvariant();
        if (hex.Length == 8)
            // 取后 6 位 RGB，兼容 #AARRGGBB 和 #RRGGBBAA
            hex = hex[^6..];
        return $"#{hex}";
    }

    // === 通过 ADB 查询单个系统颜色 ===
    public static string LookupAdbColor(string resourceName, int timeoutSec = 10)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "adb",
                    Arguments = $"shell cmd overlay lookup android \"android:color/{resourceName}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(timeoutSec * 1000))
            {
                process.Kill();
                throw new TimeoutException("ADB 读取超时，请确认手机已连接并允许 USB 调试。");
            }

            var result = (output + error).Trim();
            if (process.ExitCode != 0)
                throw new Exception(result.Length > 0 ? result : $"adb 执行失败，退出码：{process.ExitCode}");

            return NormalizeAdbColor(result);
        }
        catch (Win32Exception)
        {
            throw new Exception("未找到 adb，请先安装 Android SDK Platform Tools 并将 adb 加入 PATH。");
        }
    }

    // === 从手机获取完整 Monet 颜色配置 ===
    public static Dictionary<string, string> FetchColorsFromAdb(ProgressReporter? reporter = null)
    {
        var colors = LoadExistingColors();
        foreach (var tone in Config.ColorTones)
        {
            var resourceName = $"system_accent1_{tone}";
            colors[$"accent1_{tone}"] = LookupAdbColor(resourceName);
            reporter?.Log($"{resourceName} = {colors[$"accent1_{tone}"]}");
        }

        var jsonPath = Path.Combine(AppContext.BaseDirectory, Config.ColorsJson);
        var json = JsonSerializer.Serialize(colors, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(jsonPath, json + "\n", System.Text.Encoding.UTF8);
        return colors;
    }

    // === 读取并校验颜色配置，返回关键三个色调 ===
    public static (string accent1_100, string accent1_200, string accent1_700) PrepareColors()
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, Config.ColorsJson);
        var text = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
        var colors = JsonSerializer.Deserialize<Dictionary<string, string>>(text)
                     ?? throw new Exception("colors.json 解析失败");

        if (!colors.TryGetValue("accent1_100", out var a100) ||
            !colors.TryGetValue("accent1_200", out var a200) ||
            !colors.TryGetValue("accent1_700", out var a700))
            throw new Exception("colors.json 缺少必需的 accented1_100/200/700 字段");

        return (a100, a200, a700);
    }

    // === 返回排序后的 accent1 系列供预览 ===
    public static Dictionary<string, string> GetAccent1ColorsForPreview()
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, Config.ColorsJson);
        var text = File.ReadAllText(jsonPath, System.Text.Encoding.UTF8);
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(text) ?? new();

        return data
            .Where(kv => kv.Key.StartsWith("accent1_"))
            .OrderBy(kv => int.Parse(kv.Key.Split('_')[1]))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}

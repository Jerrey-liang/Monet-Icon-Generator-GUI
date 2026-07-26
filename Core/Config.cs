namespace MonetIconGenerator.Core;

/// <summary>
/// 集中管理所有路径常量和配置项。
/// 对应 Python 版 main.py 第 18-55 行的全局常量。
/// </summary>
public static class Config
{
    // === 基础路径 ===
    public static readonly string BaseDir = AppContext.BaseDirectory;

    // === 颜色资源 ===
    public const string ColorsJson = "colors.json";
    public static readonly int[] ColorTones = { 0, 10, 50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000 };

    // === 模板文件 ===
    public static readonly string ClipPngPath         = Path.Combine(BaseDir, "assets", "clip.png");
    public static readonly string ClipRoundPngPath    = Path.Combine(BaseDir, "assets", "clip-round.png");
    public static readonly string SubXmlPath          = Path.Combine(BaseDir, "assets", "manifest.xml");
    public static readonly string CalendarXmlPath     = Path.Combine(BaseDir, "assets", "com.android.calendar", "manifest.xml");
    public static readonly string CalendarDuoXmlPath  = Path.Combine(BaseDir, "assets", "com.android.calendar", "manifest-duo.xml");
    public static readonly string NameMapping         = Path.Combine(BaseDir, "assets", "name_mapping_by_MrBocchi.json");
    public static readonly string TransformConfigXml  = Path.Combine(BaseDir, "assets", "transform_config.xml");
    public static readonly string TransformConfigRoundXml = Path.Combine(BaseDir, "assets", "transform_config-round.xml");

    // === Lawnicons 资源（相对于 BaseDir，首次运行时自动下载） ===
    public static readonly string DrawableZipPath     = Path.Combine(BaseDir, "lawnicons_assets", "drawable.zip");
    public static readonly string AppfilterXml        = Path.Combine(BaseDir, "lawnicons_assets", "appfilter_plain.xml");
    public static readonly string LawniconsVersionJson = Path.Combine(BaseDir, "lawnicons_assets", "version.json");
    public const string LawniconsReleaseApi = "https://api.github.com/repos/LawnchairLauncher/lawnicons/releases/latest";
    public static readonly string LawniconsRendererCs = Path.Combine(BaseDir, "assets", "render_lawnicons_svgs.cs");
    public const int LawniconsMinResourceCount = 1000;

    // === 临时目录与缓存 ===
    public const string TempDir = "temp";
    public static readonly string DrawableDir         = Path.Combine(BaseDir, TempDir, "drawable");
    public static readonly string PreprocessDir       = Path.Combine(BaseDir, TempDir, "_Preprocess");
    public static readonly string PreprocessNightDir  = Path.Combine(BaseDir, TempDir, "_Preprocess-night");
    public static readonly string ThemeFallbackXml    = Path.Combine(BaseDir, TempDir, "theme_fallback.xml");
    public static readonly string GeneralXmlPath      = Path.Combine(BaseDir, TempDir, "transform_config.xml");

    // === 输出 ===
    public const string OutputIcons = "icons";
    public const string FancyIconsDir = "fancy_icons";
    public const string ResDir = "res/drawable-xxhdpi";

    // === Magisk 打包 ===
    public static readonly string PackMagisk         = Path.Combine(BaseDir, "assets", "pack-magisk");
    public static readonly string PackMagiskTemp     = Path.Combine(BaseDir, TempDir, "pack-magisk");
    public const string PackMagiskOutput = "HyperOS Monet Launcher.zip";

    // === MTZ 打包 ===
    public static readonly string PackMtz            = Path.Combine(BaseDir, "assets", "pack-mtz");
    public static readonly string PackMtzTemp        = Path.Combine(BaseDir, TempDir, "pack-mtz");
    public const string PackMtzOutput = "HyperOS Monet Launcher.mtz";

    // === 常用目录确保 ===
    public static string OutputIconsFull => Path.Combine(BaseDir, OutputIcons);
    public static string PackMagiskOutputFull => Path.Combine(BaseDir, PackMagiskOutput);
    public static string PackMtzOutputFull => Path.Combine(BaseDir, PackMtzOutput);
}

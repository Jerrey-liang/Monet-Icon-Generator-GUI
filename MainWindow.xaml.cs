using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonetIconGenerator.Core;

namespace MonetIconGenerator;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        CbDarkMode.Checked += (_, _) => PanelLightMode.IsEnabled = false;
        CbDarkMode.Unchecked += (_, _) => PanelLightMode.IsEnabled = true;

        // 启动时所有按钮禁用，资源就绪后逐步开放
        BtnFetchColors.IsEnabled = false;
        BtnProcess.IsEnabled = false;
        BtnPackageMagisk.IsEnabled = false;
        BtnPackageMtz.IsEnabled = false;

        Loaded += async (_, _) => await StartupCheckAsync();
    }

    // === 启动时 Lawnicons 资源检查 ===
    private async Task StartupCheckAsync()
    {
        var startupReporter = new ProgressReporter((detail, pct) =>
        {
            Dispatcher.Invoke(() =>
                TxtStatus.Text = $"{detail}（{pct * 100:F0}%）");
        });

        try
        {
            TxtStatus.Text = "正在检查 Lawnicons 资源...";
            await LawniconsUpdater.EnsureResourcesAsync(startupReporter);
            TxtStatus.Text = "就绪";

            if (!Packager.DrawableCacheIsValid())
            {
                TxtStatus.Text = "正在解压图标资源...";
                await Task.Run(() => Packager.ExtractDrawableCache(startupReporter));
                TxtStatus.Text = "就绪";
            }

            // 资源就绪，开放第1步
            BtnFetchColors.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"资源初始化失败：{ex.Message}";
        }
    }

    // === 步骤1：获取颜色（自动预览）===
    private async void BtnFetchColors_Click(object sender, RoutedEventArgs e)
    {
        BtnFetchColors.IsEnabled = false;
        TxtColorStatus.Text = "正在通过 ADB 获取颜色...";
        TxtColorStatus.Foreground = new SolidColorBrush(Colors.Gray);

        try
        {
            var colors = await Task.Run(() => ColorManager.FetchColorsFromAdb());
            var valid = ColorManager.ValidateColors(colors);
            if (valid)
            {
                var accentCount = colors.Count(kv => kv.Key.StartsWith("accent1_"));
                TxtColorStatus.Text = $"配置读取成功！已获取 {accentCount} 个色调。";
                TxtColorStatus.Foreground = new SolidColorBrush(Colors.Green);
                TxtStatus.Text = $"已获取 {accentCount} 个色调";
                LoadColorPreview();
                BtnProcess.IsEnabled = true; // 开放第2步
            }
            else
            {
                TxtColorStatus.Text = "颜色数据格式异常，请检查 colors.json";
                TxtColorStatus.Foreground = new SolidColorBrush(Colors.Red);
            }
        }
        catch (Exception ex)
        {
            TxtColorStatus.Text = $"获取失败：{ex.Message}";
            TxtColorStatus.Foreground = new SolidColorBrush(Colors.Red);
        }
        finally
        {
            BtnFetchColors.IsEnabled = true;
        }
    }

    // === 加载颜色预览 ===
    private void LoadColorPreview()
    {
        ColorPreviewList.Items.Clear();
        try
        {
            var colors = ColorManager.GetAccent1ColorsForPreview();
            foreach (var kv in colors)
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(kv.Value);
                    var border = new Border
                    {
                        Background = new SolidColorBrush(color),
                        Height = 30,
                        Margin = new Thickness(0, 0, 0, 2),
                        CornerRadius = new CornerRadius(3)
                    };
                    var text = new TextBlock
                    {
                        Text = $"system_{kv.Key}  {kv.Value}",
                        Foreground = color.R < 128 ? Brushes.White : Brushes.Black,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                        FontSize = 12
                    };
                    border.Child = text;
                    ColorPreviewList.Items.Add(border);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"预览加载失败：{ex.Message}";
        }
    }

    // === 步骤2：预处理 + 打包 icons（合并，使用同一条进度条）===
    private async void BtnProcess_Click(object sender, RoutedEventArgs e)
    {
        BtnProcess.IsEnabled = false;
        PbProcess.Value = 0;

        try
        {
            // ---- 校验颜色 ----
            var jsonPath = Path.Combine(AppContext.BaseDirectory, Config.ColorsJson);
            ColorManager.FixColorJson(jsonPath);
            var colors = ColorManager.LoadExistingColors();
            if (!ColorManager.ValidateColors(colors))
            {
                MessageBox.Show("颜色配置无效，请先获取颜色配置。", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 删除已有的旧 icons 文件
            if (File.Exists(Config.OutputIconsFull))
            {
                File.Delete(Config.OutputIconsFull);
                TxtStatus.Text = "已删除旧 icons 文件";
            }

            var (a100, a200, a700) = ColorManager.PrepareColors();
            var clipPngPath = RbRect.IsChecked == true
                ? Config.ClipPngPath : Config.ClipRoundPngPath;
            var configSrc = RbRect.IsChecked == true
                ? Config.TransformConfigXml : Config.TransformConfigRoundXml;
            File.Copy(configSrc, Config.GeneralXmlPath, true);

            var pngFiles = Directory.GetFiles(Config.DrawableDir, "*.png");
            var preprocessTotal = pngFiles.Length;

            // ---- 阶段 1：预处理（占 75%） ----
            TxtProcessStatus.Text = "正在预处理图标...";
            await Task.Run(() =>
            {
                for (int i = 0; i < preprocessTotal; i++)
                {
                    var file = Path.GetFileName(pngFiles[i]);
                    IconProcessor.ProcessFile(file, Config.DrawableDir, a100, a200, a700, clipPngPath);
                    var pct = (double)(i + 1) / preprocessTotal * 75;
                    Dispatcher.Invoke(() =>
                    {
                        PbProcess.Value = pct;
                        TxtProcessStatus.Text = $"预处理：{i + 1}/{preprocessTotal}";
                    });
                }
            });

            IconProcessor.CreateThemeFallbackXml();

            // ---- 阶段 2：打包 icons（占 25%） ----
            TxtProcessStatus.Text = "正在打包 icons...";
            var darkMode = CbDarkMode.IsChecked == true;
            var lightMode = RbLightIcon.IsChecked == true;

            var packageReporter = new ProgressReporter((_, pct) =>
            {
                Dispatcher.Invoke(() =>
                {
                    PbProcess.Value = 75 + pct * 25;
                    TxtProcessStatus.Text = $"打包 icons：{pct * 100:F0}%";
                });
            });

            await Task.Run(() => Packager.IconPackage(darkMode, lightMode, packageReporter));

            PbProcess.Value = 100;
            TxtProcessStatus.Text = $"完成！icons 已生成。\n{Config.OutputIconsFull}";
            TxtStatus.Text = "图标已生成";
            BtnPackageMagisk.IsEnabled = true; // 开放第3步
            BtnPackageMtz.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TxtProcessStatus.Text = $"处理失败：{ex.Message}";
            MessageBox.Show(ex.Message, "生成失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnProcess.IsEnabled = true;
        }
    }

    // === 步骤3：打包 Magisk 模块 ===
    private void BtnPackageMagisk_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(Config.OutputIconsFull))
            {
                MessageBox.Show("请先生成图标。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (File.Exists(Config.PackMagiskOutputFull))
                File.Delete(Config.PackMagiskOutputFull);

            TxtPackageStatus.Text = "正在打包 Magisk 模块...";
            TxtPackageStatus.Foreground = new SolidColorBrush(Colors.Gray);
            Packager.PackageMagisk();
            TxtPackageStatus.Text = $"完成：{Config.PackMagiskOutput}";
            TxtPackageStatus.Foreground = new SolidColorBrush(Colors.Green);
        }
        catch (Exception ex)
        {
            TxtPackageStatus.Text = $"打包失败：{ex.Message}";
            TxtPackageStatus.Foreground = new SolidColorBrush(Colors.Red);
        }
    }

    // === 步骤3：打包 MTZ 主题 ===
    private void BtnPackageMtz_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(Config.OutputIconsFull))
            {
                MessageBox.Show("请先生成图标。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (File.Exists(Config.PackMtzOutputFull))
                File.Delete(Config.PackMtzOutputFull);

            TxtPackageStatus.Text = "正在打包 MTZ 主题...";
            TxtPackageStatus.Foreground = new SolidColorBrush(Colors.Gray);
            Packager.PackageMtz();
            TxtPackageStatus.Text = $"完成：{Config.PackMtzOutput}";
            TxtPackageStatus.Foreground = new SolidColorBrush(Colors.Green);
        }
        catch (Exception ex)
        {
            TxtPackageStatus.Text = $"打包失败：{ex.Message}";
            TxtPackageStatus.Foreground = new SolidColorBrush(Colors.Red);
        }
    }
}

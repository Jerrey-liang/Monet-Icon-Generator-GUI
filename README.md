<div align="center">

# HyperOS 桌面莫奈图标生成器（GUI 版）

简体中文&nbsp;&nbsp;|&nbsp;&nbsp;[English](#english)

</div>

## 📖 项目说明

基于原 [Python CLI 版](https://github.com/Jerrey-liang/Monet-Icon-Generator) 完整重构的 **C# WPF 桌面应用**。

- 向导式图形界面，无需记忆命令
- 自动获取手机 Monet 颜色、实时预览
- 一键生成图标并打包为 Magisk 模块 / MTZ 主题包
- 启动时自动同步最新 Lawnicons 图标资源

## 🛠️ 使用说明

### 1. 环境准备

- **手机端**：解锁 Bootloader 并获取 root 权限
- **电脑端**：安装 [Android SDK Platform Tools](https://developer.android.com/tools/releases/platform-tools)，确保 `adb` 可在命令行中直接运行
- **手机端**：开启 USB 调试，连接电脑后允许调试授权

### 2. 操作步骤

| 步骤 | 操作 | 说明 |
|---|---|---|
| **获取颜色** | 点击「获取颜色配置」 | 通过 ADB 读取手机当前 Monet 颜色，右侧自动预览 |
| **生成图标** | 选择图标风格 → 点击「开始生成」 | 自动完成预处理 + 打包，生成 `icons` 文件 |
| **生成刷入包** | 点击对应按钮 | 一键打包 Magisk 模块 或 MTZ 主题包 |

### 3. 使用图标包

`icons` 文件生成于程序根目录。三种使用方式：

1. **【推荐】直接使用**
   - 应用任意随机主题后，使用 MT 管理器将 `icons` 复制至 `/data/system/theme/`
   - 赋予完整读取权限，重启桌面

2. **刷入 Magisk 模块**
   - 在 Magisk / KernelSU 中刷入 `HyperOS Monet Launcher.zip`
   - 重启手机

3. **导入 MTZ 主题**
   - 使用主题破解模块导入 `HyperOS Monet Launcher.mtz`
   - 应用主题

## 🧩 工作原理

### Monet 取色

通过 ADB 读取系统 `system_accent1_*` 系列动态色值并写入 `colors.json`。

- 浅色模式：前景 `accent1_700` / 背景 `accent1_100`
- 深色模式：前景 `accent1_200` / 背景 `accent1_700`

由于 MIUI 主题引擎不支持动态引用 `@android:color/`，因此每次更换壁纸后需重新获取颜色并生成图标。

### 自动切换深色模式

启用后，图标包使用 `fancy_icons/` 目录结构，每个应用包含 `iconBg_0.png`（浅色）+ `iconBg_1.png`（深色）+ `manifest.xml`（切换动画），跟随系统深色模式自动切换。

关闭时使用 `res/drawable-xxhdpi/` + `theme_fallback.xml` 结构，多应用共享同一图标文件，有效减小体积。

### Lawnicons 自动同步

启动时自动检查 [Lawnicons 发行版](https://github.com/LawnchairLauncher/lawnicons/releases) 最新稳定版。检测到更新后自动：

1. 下载 APK 和源码包
2. 解析 ARSC / 二进制 XML 提取图标映射
3. 渲染 SVG 为 215px PNG
4. 校验完整性后替换本地资源

如需跳过自动检查：

```powershell
$env:MONET_SKIP_LAWNICONS_UPDATE = "1"
.\MonetIconGenerator.exe
```

## 🔧 开发相关

### 环境要求

- Visual Studio 2022 / VS Code
- .NET 8.0 SDK
- Windows 10+

### 项目结构

```
MonetIconGenerator/
├── Core/
│   ├── Config.cs                # 路径常量
│   ├── ColorManager.cs          # ADB 取色、颜色校验
│   ├── IconProcessor.cs         # 图标合成、批量预处理
│   ├── ArscParser.cs            # Android ARSC 二进制解析
│   ├── BinaryXmlParser.cs       # Android 二进制 XML 解析
│   ├── LawniconsUpdater.cs     # Lawnicons 自动同步
│   ├── Packager.cs              # icons / Magisk / MTZ 打包
│   ├── ProgressReporter.cs      # 进度回调抽象
│   └── RenderLawniconsSvgs.cs  # SVG → PNG 渲染器
├── MainWindow.xaml / .cs        # WPF 主界面
├── assets/                      # 静态模板资源
└── lawnicons_assets/            # 图标素材（自动下载）
```

### 构建发布

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

生成单个自包含 .exe 文件，用户无需安装 .NET 运行时。

## 💖 特别感谢

- 原版 Python 脚本作者：[酷安@Mr_Bocchi](http://www.coolapk.com/u/10895092)
- 图标来源：[Lawnicons](https://github.com/LawnchairLauncher/lawnicons)
- 深浅切换方案：[酷安@阿尼亚超爱吃花生](http://www.coolapk.com/u/10895092)

<a name="english"></a>

## HyperOS Monet Icon Generator (GUI)

C# WPF desktop application — a full rewrite of the Python CLI tool. Provides a guided GUI for fetching Monet colors via ADB, batch-generating themed icons from Lawnicons assets, and packaging into Magisk modules or MTZ theme packages.

**Quick start:** Double-click `MonetIconGenerator.exe` → Fetch colors → Generate icons → Package.

See the Chinese section above for detailed usage, or refer to the [original Python project README](https://github.com/Jerrey-liang/Monet-Icon-Generator) for the underlying principles.

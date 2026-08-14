<p align="center">
  <img src="Assets/Finder-reference.png" width="160" alt="FinderWin 图标">
</p>

<h1 align="center">FinderWin</h1>

<p align="center">在 Windows 上提供接近 macOS Finder 的原生文件管理体验。</p>

FinderWin 是一个面向 Windows 10/11 的原生 WPF 文件管理器，界面与交互参考 macOS Finder。项目不使用 Electron、WebView 或 Node.js 运行时。

> 这是社区制作的兼容项目，并非 Apple 官方产品，也不隶属于 Apple Inc.

## 下载与架构选择

请从 GitHub Releases 下载与电脑架构对应的版本：

| 系统 | 运行时标识 | 应选择的文件 |
| --- | --- | --- |
| 常见 Intel/AMD 64 位 Windows 10/11 | `win-x64` | `FinderWin-win-x64-single.exe` 或 `FinderWin-win-x64-portable.zip` |
| Windows on ARM | `win-arm64` | `FinderWin-win-arm64-single.exe` 或 `FinderWin-win-arm64-portable.zip` |
| 真正的 32 位 Windows 10 | `win-x86` | `FinderWin-win-x86-single.exe` 或 `FinderWin-win-x86-portable.zip` |

`x86` 特指 32 位系统，并不是所有 Intel/AMD 电脑的统称。绝大多数 Intel/AMD Windows 电脑应下载 `x64`。

- `single.exe`：单个自包含程序，不要求预装 .NET；第一次启动需要展开运行时，可能稍慢。
- `portable.zip`：解压后运行目录内的 `FinderWin.exe`；文件较多，但兼容性和启动诊断更好，遇到单文件无法启动时优先使用这一版。

Windows 可能对从互联网下载的 EXE 显示安全提示。请确认文件来自本项目 Release，再选择“更多信息”→“仍要运行”。

## 使用方法

1. 下载正确架构的 Release。
2. 对于便携版，完整解压 ZIP，不要直接在压缩包预览中运行。
3. 双击 `FinderWin.exe`。
4. 使用左侧栏或“打开文件夹…”进入目录。

主要操作：

- 双击文件夹进入，双击文件使用 Windows 默认应用打开。
- `Win + Shift + .`：显示或隐藏点号文件、`.DS_Store`、`__MACOSX` 等隐藏项目。
- `Ctrl + F`：展开搜索；可按名称、扩展名、种类和标签筛选；`Esc` 退出搜索。
- 工具栏的分享与标签按钮仅在选中项目后启用。
- 图标视图中可自由拖动项目；位置会保存到 FinderWin 的本地布局数据库，并尽力镜像到当前目录的 `.DS_Store`。
- 右键菜单支持打开、快速查看、简介、重命名、复制/剪切/粘贴、废纸篓、压缩和解压缩。

如果程序在托管代码启动后发生异常，会在 Windows 桌面生成 `FinderWin-startup.log`。

## 从源码运行

### 环境

- .NET 8 SDK
- Windows 10/11，或在 macOS/Linux 上启用 Windows targeting 进行交叉编译

```powershell
git clone https://github.com/XmShrine/FinderWin.git
cd FinderWin
dotnet restore
dotnet run
```

WPF 程序只能在 Windows 上实际运行；macOS/Linux 可以交叉编译，但不能直接启动生成的 EXE。

## 编译

普通 Release 构建：

```powershell
dotnet build FinderWin.csproj -c Release
```

生成自包含文件夹版：

```powershell
dotnet publish FinderWin.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64-folder
dotnet publish FinderWin.csproj -c Release -r win-arm64 --self-contained true -o artifacts/win-arm64-folder
dotnet publish FinderWin.csproj -c Release -r win-x86 --self-contained true -o artifacts/win-x86-folder
```

生成自包含单文件版：

```powershell
dotnet publish FinderWin.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x64-single
dotnet publish FinderWin.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-arm64-single
dotnet publish FinderWin.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/win-x86-single
```

在 macOS/Linux 上交叉编译时，项目中的 `EnableWindowsTargeting` 已经启用。生成的文件仍需复制到相应架构的 Windows 设备上验证。

## 性能设计

- 使用原生 WPF，不携带 Chromium。
- 在后台通过 `Directory.EnumerateFileSystemEntries` 枚举目录，避免阻塞界面线程。
- 列表视图使用 recycling virtualization，减少大型目录中的 UI 对象数量。
- 不进行启动全盘扫描、索引或缩略图预生成，只读取用户实际打开的目录。
- 隐藏项目快捷键通过 Win32 `RegisterHotKey` 注册。

## macOS 兼容元数据与压缩

- 只有用户通过 FinderWin 成功打开的目录，才会尽力创建 `.DS_Store`。未打开的目录不会被扫描或修改。
- `__MACOSX` 不会出现在普通源文件夹中，只会在 FinderWin 创建 ZIP 时写入压缩包。
- FinderWin 创建的 ZIP 使用 macOS 风格的 Unicode 分解名称。FinderWin 和 macOS 可正常解压；部分旧式 Windows 解压工具可能显示中文乱码，这是为复现 macOS 归档兼容行为而保留的差异。

## 第三方资源

- 应用图标参考公开的 [VeryIcon iOS7 Style Finder](https://www.veryicon.com/icons/phone/ios7-style/finder-100.html) 素材。
- 内嵌字体及其许可文本位于 `Assets/Fonts/`。分发或修改时请保留相应许可文件。

Apple、macOS、Finder 及相关标识是 Apple Inc. 的商标。本项目仅用于兼容性与界面研究。

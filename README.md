# 原神帧率解锁 (GenshinFpsUnlocker)

自定义目标 FPS · 检测游戏启动后后台注入 · 托盘设置 · 开机自启（无 UAC）

界面为设计稿一比一实现的 Web UI（WebView2 嵌入）：概览 / 设置 / 日志 / 指南 / 关于；深浅色切换。

关闭或最小化窗口会**驻留系统托盘**（与「启动后最小化」一致）；托盘菜单文案与快捷设置对齐，左键恢复主窗口。

## 要求

- **操作系统**：64 位 **Windows 10**（1607 / 版本 14393 及以上）或 **Windows 11**
- **架构**：x64（与游戏客户端一致）
- 构建：.NET 9 SDK、CMake、MSVC（或 VS Build Tools）、**Node.js 20+**（界面）；安装包另需 [kachina-builder](https://github.com/YuehaiTeam/kachina-installer/releases)
- 运行界面：系统需安装 **Microsoft Edge WebView2 Runtime**（Win10/11 通常已自带）
- 运行依赖策略：
  - **应用自身**（托管 DLL、`FpsUnlockerStub.dll`、MinHook 静态编入 Stub 等）随安装包**自包含**
  - **无** Node / Python 等其它编程语言运行时依赖
  - **.NET Desktop Runtime 9**、**VC++ 2015+ x64**：由 **Kachina** 安装器按配置检测/安装（亦可首次运行时由主程序提示下载）
  - 可选 `build.ps1 -SelfContained` 将 .NET 也打进主程序（离线、包体更大）

## 构建

默认：**主程序框架依赖（包体小）**。安装包由 **Kachina**（`kachina-builder`）生成：

```powershell
# 仅编译主程序 + Stub
.\build.ps1 -Configuration Release -SkipSetup

# 完整：编译 + Kachina 离线安装器（需 Build\kachina-builder.exe）
.\build.ps1 -Configuration Release

# 或
.\Build\setup_build.cmd

# 可选：主程序也自包含
.\build.ps1 -Configuration Release -SelfContained
```

首次打包前，从 [kachina-installer Releases](https://github.com/YuehaiTeam/kachina-installer/releases) 下载 `kachina-builder.exe` 放到 `Build\`。CI 会自动下载。

产物：

```text
dist\GenshinFpsUnlocker.exe
dist\FpsUnlockerStub.dll
dist\GenshinFpsUnlocker.Install.{ver}.exe   # Kachina 离线安装器
dist\GenshinFpsUnlocker\                    # 便携目录（含 .update.exe）
dist\GenshinFpsUnlocker_v{ver}.7z           # 可选便携 7z
```

配置见 `Build/kachina.config.json`（默认安装目录 `Program Files\GenshinFpsUnlocker`、GitHub 在线源、运行库列表）。

## 安装 / 卸载

```powershell
.\dist\GenshinFpsUnlocker.Install.1.0.0.exe
```

- 可选安装目录（默认 `C:\Program Files\GenshinFpsUnlocker\`）
- 安装时按 UAC 策略提权；装完后日常与开机自启不再弹 UAC
- 可自动处理 .NET Desktop Runtime 9 / VCRedist（见配置 `runtimes`）
- 安装目录生成 **`GenshinFpsUnlocker.uninst.exe`**、**`GenshinFpsUnlocker.update.exe`**

卸载：

- 开始菜单 / 「应用和功能」/ 安装目录 **`GenshinFpsUnlocker.uninst.exe`**
- 程序内「卸载清理」与托盘：优先启动官方 uninst；便携无该文件时回退内置白名单清理
- 配置与日志默认在 `%LocalAppData%\GenshinFpsUnlocker\`（程序内卸载会尽量清理）

在线更新：已安装副本可使用 `GenshinFpsUnlocker.update.exe`，从配置的 GitHub Release 源拉取（需已发布对应 `Install` 包）。

## 依赖说明

| 依赖 | 处理方式 |
|------|----------|
| 应用托管程序集 / 资源 | 打进安装包 |
| `FpsUnlockerStub.dll` + MinHook | 打进安装包；CRT **静态链接**（/MT） |
| 安装器 / 卸载器 / 更新器 | **Kachina**（`Install` / `uninst` / `update`） |
| .NET Desktop Runtime 9 x64 | 安装器 `runtimes`；亦可首次运行提示 |
| VC++ 2015+ x64 | 安装器 `runtimes`（通常 Stub 已静态 CRT） |
| Node / Python 等 | **不需要、不安装** |

## 配置

- 路径：`%LocalAppData%\GenshinFpsUnlocker\config.json`
- 原子写入（临时文件 + `File.Replace`）并保留 `config.json.bak`
- 主文件损坏时自动从 `.bak` / 临时文件恢复
- 若 LocalAppData 不可写，依次尝试 AppData、文档目录

## 布局

```text
{安装目录}\GenshinFpsUnlocker\     # 默认 Program Files 下
  GenshinFpsUnlocker.exe
  FpsUnlockerStub.dll
  GenshinFpsUnlocker.uninst.exe    # Kachina 卸载
  GenshinFpsUnlocker.update.exe    # Kachina 更新（可选）
  GenshinFpsUnlocker.install       # 可选标记（内置清理用）

%LocalAppData%\GenshinFpsUnlocker\
  config.json
  logs\
```

## CI

GitHub Actions **仅手动运行**（Actions → Build → Run workflow），不会在 push/PR 时自动执行。

产物：

- `GenshinFpsUnlocker-portable-win-x64.zip`
- `GenshinFpsUnlocker_v*.7z`（若 runner 有 7z）
- `GenshinFpsUnlocker.Install.*.exe`（Kachina）

工作流可选 `host_mode=self-contained` 打全量自包含主程序。

将 `Install` 包发布到 Release 且 tag 为 `v{version}` 后，配置中的 GitHub 在线源即可用于更新器。

## 安全说明

本工具属于第三方注入类软件，使用风险自负；详见程序内安全声明。请关闭游戏 V-Sync 后使用自定义帧率。

## License

MIT · MinHook：BSD-2-Clause · 安装包构建工具 Kachina（kachina-installer）按其上游许可使用

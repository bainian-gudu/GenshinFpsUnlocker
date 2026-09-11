# 原神帧率解锁 (GenshinFpsUnlocker)

自定义目标 FPS · 检测游戏启动后后台注入 · 托盘设置 · 开机自启（无 UAC）

界面字体与浅色/深色主题跟随 Windows 系统设置。

## 要求

- **操作系统**：64 位 **Windows 10**（1607 / 版本 14393 及以上）或 **Windows 11**
- **架构**：x64（与游戏客户端一致）
- 构建：.NET 9 SDK、CMake、MSVC（或 VS Build Tools）；安装包另需 7-Zip + [MicaSetup](https://github.com/lemutec/MicaSetup) `makemica`
- 运行依赖策略：
  - **应用自身**（托管 DLL、`FpsUnlockerStub.dll`、MinHook 静态编入 Stub 等）随安装包**自包含**
  - **无** Node / Python 等其它编程语言运行时依赖
  - **仅** .NET Desktop Runtime 不打进分发包：首次运行检测；缺失时提示并提供官方下载链接
  - 可选 `build.ps1 -SelfContained` 将 .NET 也打进主程序（离线、包体更大）

## 构建

默认：**主程序框架依赖（包体小）**。安装包由 **MicaSetup**（`makemica`）生成（含 `Uninst.exe`、桌面/开始菜单、ARP）：

```powershell
# 仅编译主程序 + Stub
.\build.ps1 -Configuration Release -SkipSetup

# 完整：编译 + 7z 载荷 + MicaSetup 安装器（需本机 Build\makemica.exe）
.\build.ps1 -Configuration Release

# 或一键脚本
.\Build\setup_build.cmd

# 可选：主程序也自包含（离线场景，包体更大）
.\build.ps1 -Configuration Release -SelfContained
```

首次生成安装器前，请从 [MicaSetup Releases](https://github.com/lemutec/MicaSetup/releases) 下载 `MicaSetup_v*.7z`，解压到 `Build\`（得到 `makemica.exe` 与 `template\`）。CI 会自动下载。

产物：

```text
dist\GenshinFpsUnlocker.exe
dist\FpsUnlockerStub.dll
dist\GenshinFpsUnlocker_v{ver}.7z              # 便携 7z
dist\GenshinFpsUnlocker_Setup_v{ver}.exe       # MicaSetup 安装器（内含 Uninst.exe）
```

## 安装 / 卸载

```powershell
.\dist\GenshinFpsUnlocker_Setup_v1.0.0.exe
```

- 可选安装目录（默认 `C:\Program Files\GenshinFpsUnlocker\`）
- 安装时一次管理员授权；装完后日常与开机自启不再弹 UAC
- 自动创建桌面与开始菜单快捷方式、写入「应用和功能」卸载项
- 安装目录生成 **`Uninst.exe`**（官方卸载入口）

卸载：

- 开始菜单「卸载」或安装目录 **`Uninst.exe`**
- 程序内「卸载清理」/ 托盘菜单：优先启动 `Uninst.exe`；便携目录无该文件时回退内置白名单清理
- 将清理安装文件、本软件配置/日志（`%LocalAppData%\GenshinFpsUnlocker`）、自启与快捷方式

## 依赖说明

| 依赖 | 处理方式 |
|------|----------|
| 应用托管程序集 / 资源 | 打进安装包 |
| `FpsUnlockerStub.dll` + MinHook | 打进安装包；CRT **静态链接**（/MT） |
| 安装器 / 卸载器 | **MicaSetup** 生成的单文件 Setup.exe + Uninst.exe |
| .NET Desktop Runtime 8/9 x64 | **不进分发包**；首次运行检测并提示官方下载 |
| Node / Python 等 | **不需要、不安装** |
| VC++ 可再发行组件 | 通常不需要（Stub 已静态 CRT） |

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
  Uninst.exe                       # MicaSetup 卸载程序
  GenshinFpsUnlocker.install       # 可选标记（内置清理用）

%LocalAppData%\GenshinFpsUnlocker\
  config.json
  logs\
```

## CI

GitHub Actions **仅手动运行**（Actions → Build → Run workflow），不会在 push/PR 时自动执行。

产物：

- `GenshinFpsUnlocker-portable-win-x64.zip`
- `GenshinFpsUnlocker_v*.7z`
- `GenshinFpsUnlocker_Setup_v*.exe`（MicaSetup）

工作流可选 `host_mode=self-contained` 打全量自包含主程序。

## 安全说明

本工具属于第三方注入类软件，使用风险自负；详见程序内安全声明。请关闭游戏 V-Sync 后使用自定义帧率。

## License

MIT · MinHook：BSD-2-Clause · 安装包构建工具 MicaSetup 按其上游许可使用

# 原神帧率解锁 (GenshinFpsUnlocker)

自定义目标 FPS · 检测游戏启动后后台注入 · 托盘设置 · 开机自启（无 UAC）· 图形安装器

界面字体与浅色/深色主题跟随 Windows 系统设置。

## 要求

- **操作系统**：64 位 **Windows 10**（1607 / 版本 14393 及以上）或 **Windows 11**
- **架构**：x64（与游戏客户端一致）
- 构建：.NET 9 SDK、CMake、MSVC（或 VS Build Tools）
- 运行依赖策略：
  - **应用自身**（托管 DLL、`FpsUnlockerStub.dll`、MinHook 静态编入 Stub 等）随安装包**自包含**
  - **无** Node / Python 等其它编程语言运行时依赖
  - **仅** .NET Desktop Runtime 不打进分发包：安装时下载官方 **.exe** 并 **静默安装**（需联网；已具备则跳过）
  - 可选 `build.ps1 -SelfContained` 将 .NET 也打进主程序（离线、包体更大）

## 构建

默认：**主程序框架依赖（包体小）** + **安装器自包含**（无 .NET 也能跑安装向导，并自动装运行库）：

```powershell
.\build.ps1 -Configuration Release
# 可选：主程序也自包含（离线场景，包体更大）
.\build.ps1 -Configuration Release -SelfContained
```

产物：

```text
dist\GenshinFpsUnlocker.exe               # 默认 FDD，体积小
dist\FpsUnlockerStub.dll
dist\Setup\GenshinFpsUnlocker.Setup.exe   # 自包含图形安装器
dist\Setup\Payload\                       # 安装包内容
```

## 安装

```powershell
.\dist\Setup\GenshinFpsUnlocker.Setup.exe
```

- 可选安装目录；进度条与文件列表滚动显示
- 上一步 / 下一步 / 取消
- 安装时一次管理员授权；装完后日常与开机自启不再弹 UAC
- **依赖**：应用文件在 Payload 内（无 Node/Python）；若无 .NET 8/9 Desktop Runtime，安装器下载官方 .exe 并静默安装（约 50–60 MB）。Stub 静态 CRT，一般无需 VC++ 红包

静默：

```text
GenshinFpsUnlocker.Setup.exe --quiet [--dir 路径] [--no-run] [--no-desktop] [--autostart]
```

卸载：开始菜单「卸载 原神帧率解锁」或 `GenshinFpsUnlocker.exe --uninstall`

## 依赖说明

| 依赖 | 处理方式 |
|------|----------|
| 应用托管程序集 / 资源 | 打进安装包（Payload） |
| `FpsUnlockerStub.dll` + MinHook | 打进安装包；CRT **静态链接**（/MT） |
| 安装器自身 | **自包含**（无 .NET 也能运行向导） |
| .NET Desktop Runtime 8/9 x64 | **不进分发包**；安装时下载官方 **.exe** 并静默安装（已有则跳过） |
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
  GenshinFpsUnlocker.install

%LocalAppData%\GenshinFpsUnlocker\
  config.json
  logs\
```

## CI

GitHub Actions **仅手动运行**（Actions → Build → Run workflow），不会在 push/PR 时自动执行。

产物（默认主程序 FDD + 安装器 SC）：

- `GenshinFpsUnlocker-portable-win-x64.zip`
- `GenshinFpsUnlocker-setup-win-x64.zip`

工作流可选 `host_mode=self-contained` 打全量自包含主程序。

## 安全说明

本工具属于第三方注入类软件，使用风险自负；详见程序内安全声明。请关闭游戏 V-Sync 后使用自定义帧率。

## License

MIT · MinHook：BSD-2-Clause

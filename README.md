# 原神帧率解锁 (GenshinFpsUnlocker)

自定义目标 FPS · 检测游戏启动后后台注入 · 托盘设置 · 开机自启（无 UAC）· 图形安装器

## 要求

- **操作系统**：64 位 **Windows 10**（1607 / 版本 14393 及以上）或 **Windows 11**
- **架构**：x64（与游戏客户端一致）
- 构建：.NET 8 SDK、CMake、MSVC（或 VS Build Tools）
- 运行（框架依赖包）：.NET 8 Desktop Runtime x64  
  https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe

## 构建

```powershell
.\build.ps1 -Configuration Release
# 自包含（目标机可不装 .NET Desktop Runtime）
.\build.ps1 -Configuration Release -SelfContained
```

产物：

```text
dist\GenshinFpsUnlocker.exe
dist\FpsUnlockerStub.dll
dist\Setup\GenshinFpsUnlocker.Setup.exe   # 图形安装器
dist\Setup\Payload\                       # 安装包内容
```

## 安装

```powershell
.\dist\Setup\GenshinFpsUnlocker.Setup.exe
```

- 可选安装目录；进度条与文件列表滚动显示
- 上一步 / 下一步 / 取消
- 安装时一次管理员授权；装完后日常与开机自启不再弹 UAC

静默：

```text
GenshinFpsUnlocker.Setup.exe --quiet [--dir 路径] [--no-run] [--no-desktop] [--autostart]
```

卸载：开始菜单「卸载 原神帧率解锁」或 `GenshinFpsUnlocker.exe --uninstall`

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

产物：

- `GenshinFpsUnlocker-portable-win-x64.zip`
- `GenshinFpsUnlocker-setup-win-x64.zip`
- （可选）`GenshinFpsUnlocker-setup-sc-win-x64.zip` 自包含安装包

## 安全说明

本工具属于第三方注入类软件，使用风险自负；详见程序内安全声明。请关闭游戏 V-Sync 后使用自定义帧率。

## License

MIT · MinHook：BSD-2-Clause

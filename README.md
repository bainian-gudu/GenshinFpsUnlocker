# GenshinFpsUnlocker

独立的 **原神帧率解锁** 工具：自定义目标 FPS、检测游戏启动后后台注入、托盘设置与开机自启。

- 只做一件事：解锁 / 自定义原神 PC 端帧率上限  
- 支持自定义目标 FPS（1–540）  
- **检测游戏启动后自动注入**，可最小化托盘后台运行
- **开机自启动开关**（登录后 `--minimized` 托盘后台）
- **总开关 / 帧率解锁开关**（主界面 + 托盘均可切换）
- **托盘内快捷设置**：目标 FPS 预设、自定义 FPS、自动监视、开机自启、启动游戏、路径查找
- **多源自动查找游戏路径**（配置 / 运行进程 / Unity 日志 / 启动器 config.ini / 注册表）+ **手动选择**
- **注入功能安全声明**（自带注入仅 FPS，不影响游戏公平性）  
- 游戏退出后继续监视，下次启动会再次自动解锁  

> 本工具**不修改**游戏文件；通过注入轻量 Stub DLL，调用 Unity `set_targetFrameRate` 等价路径。  
> 请在游戏画质设置中 **关闭垂直同步 (V-Sync)**，否则帧率仍会被显示器刷新率/垂直同步限制。

## 功能对照

| 能力 | 本工具 |
|------|--------------|--------|
| 自定义目标帧率 | ✅ | ✅ |
| 动态修改帧率（运行中） | ✅ | ✅ |
| 检测已启动游戏并附着 | resume 路径 | ✅ 自动监视 |
| FOV / 去雾 / 其它 Island 功能 | ✅ | ❌ 已移除 |
| 托盘后台 + 开机自启 | 随主程序 | ✅ |

## 原理

Stub 源码路径对应：

- Stub 内特征扫描 Get/Set targetFrameRate
- 特征码（`Constants.cpp`）：
  - `GetFrameCountPattern` → `Application.get_targetFrameRate` 调用点
  - `SetFrameCountPattern` → `Application.set_targetFrameRate` 调用点
- 周期性调用 `set_targetFrameRate(TargetFps)`
- Hook `get_targetFrameRate`，把菜单显示值钳制回 30/45/60，避免设置界面异常

Host 与 Stub 通过命名共享内存通信：

```
Global\GenshinFpsUnlocker.Shared.v1
```

Host 写入 `TargetFps` / `Enabled`，Stub 回报 `Status` / `CurrentFps`。

## 目录结构

```
GenshinFpsUnlocker/
├── src/
│   ├── Common/IpcData.h          # 共享内存布局
│   ├── Stub/                     # FpsUnlockerStub.dll (C++ / MinHook)
│   └── Host/                     # GenshinFpsUnlocker.exe (C# WinForms)
├── third_party/minhook/          # MinHook
├── build.ps1                     # 一键编译
├── config.example.json
└── README.md
```

## 编译

### 环境

- Windows 10/11 x64
- Visual Studio 2022（含「使用 C++ 的桌面开发」）或 CMake + MSVC
- .NET 8 SDK

### 一键

```powershell
# 在仓库根目录
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1 -Configuration Release
```

产物在 `dist\`：

- `GenshinFpsUnlocker.exe`
- `FpsUnlockerStub.dll`（必须与 exe 同目录）
- 依赖的 `.dll`（framework-dependent 发布）

自包含发布可改 `build.ps1` 中 `dotnet publish` 为 `--self-contained true`。

### 分别编译

```powershell
# Stub
cmake -S src/Stub -B build/stub
cmake --build build/stub --config Release

# Host
dotnet publish src/Host/GenshinFpsUnlocker.Host.csproj -c Release -r win-x64 -o dist
copy build\stub\bin\Release\FpsUnlockerStub.dll dist\
```


## 安装布局（重要）

默认安装到 **专用子目录**（不会把文件直接扔在 `C:\Program Files\` 根下）：

```
C:\Program Files\GenshinFpsUnlocker\     ← 程序与 Stub DLL
%LocalAppData%\GenshinFpsUnlocker\       ← config.json / logs（可写配置）
```

```powershell
# 编译后安装（管理员 PowerShell）
.\build.ps1 -Configuration Release -Install
# 或
```

卸载（清理安装目录 + 配置 + 开机自启 + 卸载注册表，无残留）：

```powershell
# 或
& "C:\Program Files\GenshinFpsUnlocker\GenshinFpsUnlocker.exe" --uninstall
# 或 设置 → 应用 → 原神帧率解锁 → 卸载
# 或 托盘 / 主界面「卸载清理」
```

## 日志

- **默认开启**调试日志：`%LocalAppData%\GenshinFpsUnlocker\logs\app-yyyyMMdd.log`
- 主界面勾选「调试日志」或托盘开关；「打开日志目录 / 当前日志文件」
- CLI：`--no-log` 关闭；`--log-level Info` 调整级别

## 运行库

- 默认 `dotnet publish` 为 framework-dependent，需要 **.NET 8 Desktop Runtime (x64)**
- 图形安装器选项页会检测运行库；缺失可打开下载链接
- 也可用 `.\build.ps1 -SelfContained` 打出自包含包（体积更大，免安装运行库）

## 快捷方式

安装时自动创建开始菜单 + 桌面快捷方式；托盘可「创建/刷新快捷方式」。

## 使用

1. **以管理员身份**运行 `GenshinFpsUnlocker.exe`（清单已要求 elevation）
2. 首次启动阅读并确认 **注入功能安全声明**
3. 设置目标帧率（主界面预设按钮，或托盘「目标帧率」）
4. 开启 **总开关** + **启用帧率解锁** + **检测游戏启动后自动注入**
5. （可选）开启 **开机自启动** — 登录后自动托盘后台运行
6. **游戏路径**：点「自动查找」或「手动选择」`YuanShen.exe` / `GenshinImpact.exe`
7. 可最小化到托盘；也可用主界面 / 托盘的「启动游戏」
8. 程序检测到进程并出现主窗口后自动注入并解锁
9. 游戏内关闭 V-Sync

### 托盘菜单（最小化后）

- 总开关 / 启用帧率解锁 / 自动监视并注入 / 开机自启动
- 目标帧率预设 + 自定义帧率
- 启动游戏 / 自动查找路径 / 手动选择路径
- 查看安全声明 / 退出

### 命令行

```text
GenshinFpsUnlocker.exe --fps 144 --minimized
GenshinFpsUnlocker.exe --no-watch
```

### 配置文件

首次运行会在 exe 旁生成 `config.json`（参见 `config.example.json`）。

## 稳定性 / 性能 / 卸载安全

- 监视循环空闲降频；IPC 与 UI 刷新节流；日志批量刷盘
- Stub 仅在目标 FPS 变化或每 2s 时写回游戏，降低开销
- 注入失败指数退避；中文/长路径使用 Unicode API + 可选 8.3 短路径
- **卸载白名单**：须 `GenshinFpsUnlocker` 目录名 + 安装标记/注册表/Stub 校验，拒绝删除盘符根、用户目录等

## 注意事项

1. **仅单机画质体验**，不涉及战斗数值、透视、自动战斗等。请遵守当地法律与游戏用户协议。  
2. 游戏大版本更新后，特征码可能失效；若 Stub 状态为 `Error`，需同步更新 `src/Stub/dllmain.cpp` 中的 pattern（更新 `src/Stub/dllmain.cpp` 中的特征码）。  
3. 部分反作弊/安全软件可能拦截注入，需自行添加信任。  
4. 「千星奇域」等场景在原版 Island 会强制 60 帧；本精简版未移植区域检测，如需可自行加回 `BeyondResist` 逻辑。

## 致谢

- [SnapHutaoRemasteringProject/Snap.Hutao.Remastered](https://github.com/SnapHutaoRemasteringProject/Snap.Hutao.Remastered)  
- [Snap.Hutao.Remastered.UnlockerIsland](https://github.com/SnapHutaoRemasteringProject/Snap.Hutao.Remastered.UnlockerIsland)（FPS 特征与 hook 思路）  
- [TsudaKageyu/minhook](https://github.com/TsudaKageyu/minhook)  
- 社区经典 FPS unlocker（进程监视 / 注入 UX 参考）

## License

本项目源码以 MIT 许可；MinHook 为 BSD-2-Clause。


## 安全声明

本软件及注入功能属于《米哈游用户协议》第十条第二款规定的“插件、外挂或非经合法授权的第三方工具/服务”。

您需要自行承担因使用内容而引起的所有风险，本软件无法且不会对您因前述风险而导致的任何损失或损害承担责任。

出于安全因素考虑，我们有权不经向您特别通知而对软件及相关功能进行更新，或者对软件的部分功能效果进行改变或限制。

完整文案见程序内「安全声明」对话框（`SafetyNotice.cs`）。



## 权限与开机自启（无 UAC 弹窗）

- 应用程序清单为 **`asInvoker`**，与用户同完整性级别启动。
- **开机自启**写入 `HKCU\...\Run`，登录后**不会**弹出 Windows 用户账户控制（UAC）。
- 仅在用户主动执行 **安装到 Program Files / 卸载** 时，程序会按需 `runas` 提权（此时才可能出现一次 UAC）。
- 日常监视与注入在标准权限下运行；若目标进程权限更高导致注入失败，可在托盘查看状态后手动“以管理员身份运行”一次（非必须）。


## 安装（图形安装器）

```powershell
.\build.ps1 -Configuration Release
.\dist\Setup\GenshinFpsUnlocker.Setup.exe
```

- 可选安装目录；进度条 + 文件名/目录滚动列表  
- 上一步 / 下一步 / 取消  
- 仅需 `dotnet` 构建，无需 Inno/NSIS  
- 安装一次 UAC；装完后日常与开机自启无授权框  

详见 `installer/README.md`。


见 `installer/README.md`。

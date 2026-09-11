# 架构说明

## 组件

```
GenshinFpsUnlocker.exe  (Host, WinForms, .NET 8)
        │
        │  命名共享内存  Global\GenshinFpsUnlocker.Shared.v1
        │  结构见 src/Common/IpcData.h 与 Host/IpcSharedMemory.cs
        ▼
FpsUnlockerStub.dll     (注入到 YuanShen / GenshinImpact)
        │
        ├─ 特征扫描 Get/Set targetFrameRate
        ├─ MinHook：Hook Get，菜单显示合法档位
        └─ 按 IPC 中 TargetFps 调用 Set（变化或每 2s）
```

## Host 模块划分（大文件已拆 partial）

| 文件 | 职责 |
|------|------|
| `Program.cs` | 入口、单实例、安装/卸载分支 |
| `Elevation.cs` | asInvoker 日常运行；安装卸载按需 runas |
| `MainForm.cs` / `MainForm.Tray.cs` | 主界面 / 托盘 |
| `SafetyDialog.cs` | 安全声明窗 |
| `UnlockService.cs` / `UnlockService.Watch.cs` | 配置 API / 监视注入循环 |
| `DllInjector.cs` | LoadLibraryW + Hook 备用 |
| `GameLocator*.cs` | 多源定位游戏路径 |
| `InstallUninstall*.cs` | 安装标记、白名单卸载 |
| `AppPaths` / `PathUtil` / `AppLog` / `Config` | 路径、日志、配置 |
| `Autostart` / `ShortcutHelper` / `BackgroundResilience` | 自启、快捷方式、后台韧性 |
| `RuntimePrerequisite` | 运行库检测与下载提示 |

## 权限模型

- 清单：`asInvoker` → **开机自启不弹 UAC**
- 自启：仅 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
- 提权：仅用户触发的 `--install` / `--uninstall`（或安装器本身）

## IPC

- Magic：`0x465053554E4C4B52`（"FPSUNLKR"）
- Host 写：`TargetFps`、`Enabled`
- Stub 写：`Status`、`CurrentFps`、`LastError`

## 构建

```powershell
.\build.ps1 -Configuration Release [-SelfContained] [-Install]
```

产物在 `dist\`。安装器说明见 `installer/README.md`。


## 图形安装器

- 项目：`src/Setup/` → `GenshinFpsUnlocker.Setup.exe`
- 布局：`dist/Setup/GenshinFpsUnlocker.Setup.exe` + `dist/Setup/Payload/*`
- 清单：`requireAdministrator`（仅安装时 UAC）
- 主程序：`asInvoker`（自启无 UAC）

# installer/ — 唯一的安装器打包目录

本项目的安装 / 卸载 / 更新**只有 Kachina 一种实现**。宿主程序（`src/Host`）里已经
没有任何自带的安装卸载路径（`--install`、`--uninstall`、`Uninstall.cmd` 垫片、
自写 ARP 卸载注册表项、内置白名单删目录都已删除）。

所有与「打包安装器」相关的代码都集中在本目录：

```
installer/
├── README.md               本文件
├── kachina.config.json     Kachina 配置（安装目录、ARP 名称、运行库、卸载时清理的数据目录）
├── build-kachina.ps1       从 kachina/ 源码构建 kachina-builder.exe
├── pack.ps1                打包总入口：dist\ → Install.exe / 便携 zip / 便携 7z
├── tools/                  构建产物 kachina-builder.exe（.gitignore，不进版本库）
└── kachina/                上游 kachina-installer 源码快照（tag 0.5.1，见 kachina/UPSTREAM.md）
```

## 一条命令打包

```powershell
# 仓库根目录：编译 UI + Stub + Host，再打包安装器
.\build.ps1

# 已经编译过、只想重新打包
.\installer\pack.ps1

# 只编译，不打包
.\build.ps1 -SkipSetup
```

首次执行会从源码构建 `kachina-builder.exe`（需要 Rust nightly + Node/pnpm + MSVC，
详见 `kachina/UPSTREAM.md`）。之后 `installer\tools\kachina-builder.exe` 存在就跳过：

```powershell
.\build.ps1 -SkipKachinaBuild    # 要求 tools\kachina-builder.exe 已存在
.\build.ps1 -ForceKachinaBuild   # 强制重建
```

## 产物

统一落在仓库根的 `artifacts\`：

| 文件 | 说明 |
| --- | --- |
| `GenshinFpsUnlocker.Install.<ver>.exe` | 离线安装器。装完的安装目录里含 `GenshinFpsUnlocker.uninst.exe`（卸载）与 `GenshinFpsUnlocker.update.exe`（在线更新） |
| `GenshinFpsUnlocker-portable-win-x64.zip` | 便携包（内含 `update.exe`，可直接升级） |
| `GenshinFpsUnlocker_v<ver>.7z` | 便携 7z（本机检测到 7-Zip 时才生成） |

## 打包步骤（`pack.ps1` 内部做的事）

与上游 README 完全一致，三步：

```powershell
# 1) 更新器（也会被塞进便携包，用于在线升级）
kachina-builder.exe pack -c installer\kachina.config.json -o <app>\GenshinFpsUnlocker.update.exe

# 2) 生成 metadata + 分块 hashed 目录
kachina-builder.exe gen -j 6 -i GenshinFpsUnlocker -m metadata.json -o hashed `
    -r bainian-gudu/GenshinFpsUnlocker -t <ver> -u .\GenshinFpsUnlocker\GenshinFpsUnlocker.update.exe

# 3) 离线安装器
kachina-builder.exe pack -c installer\kachina.config.json -m metadata.json -d hashed `
    -o GenshinFpsUnlocker.Install.<ver>.exe
```

中间目录用 `out\kachina-pack\`（已被 `.gitignore` 排除）。
> 不要用 `build\`：Windows 路径大小写不敏感，会和历史上的 `Build\` 目录混淆。

## Kachina 负责什么 / 不负责什么

**负责**：铺文件到 `Program Files\GenshinFpsUnlocker`、写「应用和功能」卸载项、
生成 `uninst.exe` / `update.exe`、按 `runtimes` 装 .NET Desktop Runtime 9 与 VCRedist、
按 `uacStrategy` 提权、安装时创建桌面 + 开始菜单快捷方式（安装界面有勾选项，默认勾上）、
卸载时删除这些快捷方式、删除 ARP 注册表项，并按 `userDataPath` 清
`%LOCALAPPDATA%\GenshinFpsUnlocker`（配置 / 日志 / WebView2 数据）。

**不负责**，由宿主自己维护：

| 事项 | 归属 | 说明 |
| --- | --- | --- |
| 快捷方式的**中文显示名** | `src/Host/ShortcutHelper.cs` | Kachina 建的是 `GenshinFpsUnlocker.lnk`（英文 `appName`），宿主每次启动把它规范成 `原神帧率解锁.lnk` 并清掉英文重复项 |
| 开机自启（`HKCU\...\Run`） | `src/Host/Autostart.cs` | 按配置项「开机自启动」同步；Kachina 卸载**不会**删这个值，卸载前请先在设置里关掉 |

## CI

`.github/workflows/build.yml` 三个 job：

1. `build-kachina` —— 用 `installer/kachina` 源码构建 `kachina-builder.exe`；
   源码未变时命中 `actions/cache`（key = `hashFiles('installer/kachina/**')`）直接复用，
   也可用 `rebuild_kachina` 输入强制重建。
2. `build-app` —— `build.ps1 -SkipSetup` 产出 `dist\`。
3. `pack` —— 下载前两者，执行 `installer\pack.ps1 -SkipKachinaBuild`。

**不再从上游 Release 下载 `kachina-builder.exe`。**

## 升级上游 Kachina

见 `kachina/UPSTREAM.md` 的「升级上游版本」小节；升级后记得同步更新
`kachina.config.json`（若上游新增了配置项）与本目录的说明。

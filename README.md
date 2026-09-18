# 原神帧率解锁 (GenshinFpsUnlocker)

> **项目性质（请先读这一段）**
>
> - **个人自用**：本项目只为作者自己玩游戏服务，不提供任何形式的对外服务、
>   不做商业化、不接受付费；仓库公开仅作为个人备份与学习记录。
> - **AI 辅助开发**：部分代码、界面和文档使用 AI 辅助生成与整理，由作者负责需求确认、
>   结果验收和风险承担。**UI 使用 gpt-6-astra-max 设计**：界面设计稿由该模型出稿，
>   代码按稿 1:1 实现。**项目未经独立安全审计，请不要将其视为生产级软件。**
> - **与米哈游无关**：「原神 / Genshin Impact」及其角色、图标、场景素材的版权归
>   米哈游 / HoYoverse 所有，本项目未获授权或背书，详见下文「素材与版权」。

自定义目标 FPS · 反角色虚化 / 移除水下马赛克 / 隐藏 UID（画面效果注入） · 检测游戏启动后后台注入 · 托盘设置 · 开机自启（标准权限 / 最高权限计划任务，都不弹 UAC）

界面为 **gpt-6-astra-max** 设计的设计稿一比一实现的 Web UI（WebView2 嵌入）：概览 / 设置 / 日志 / 指南 / 关于；深浅色切换。

关闭或最小化窗口会**驻留系统托盘**（与「启动后最小化」一致）；托盘菜单文案与快捷设置对齐，左键恢复主窗口。
右键菜单为自绘：文字在整行内水平居中、√ 固定在左侧勾选槽内，弹出窗口四角为圆边
（Win11 用 DWM 原生圆角，Win10 用窗口 Region 裁角并把 1px 边框画成同样的圆角）。
进托盘的同时界面页签会复位到「游戏概览」，下次打开不会还停在上次浏览的页面。

## 要求

- **运行**：64 位 **Windows 10**（1607 / 14393 及以上）或 **Windows 11**，x64；
  需要 **Microsoft Edge WebView2 Runtime**（Win10/11 通常已自带），
  **.NET Desktop Runtime 9** 与 **VC++ 2015+ x64** 由安装器按配置检测/安装
  （亦可首次运行时由主程序提示下载）。**不需要** Node / Python 等开发运行时，
  逐项见下文「依赖说明」。
- **构建**：.NET 9 SDK、CMake、MSVC（或 VS Build Tools）、Node.js 20+；
  打安装器另需 Rust nightly + `rust-src` 与 pnpm 10（源码随仓库提供，CI 会自动装）。

## 快速使用

1. 从 Releases 下载 `GenshinFpsUnlocker.Install.<版本>.exe`，运行安装器并选择安装目录。
2. 首次启动时按提示安装缺少的 .NET Desktop Runtime、VC++ 或 WebView2 运行库。
3. 在「设置」中启用帧率解锁并选择目标 FPS；启动游戏后保持程序运行即可注入。
4. 关闭或最小化窗口会进入系统托盘，左键托盘图标可恢复窗口。

安装器会创建开始菜单快捷方式、可选的桌面快捷方式和卸载项。卸载时可选择是否同时删除
配置、日志和 WebView2 缓存；不删除用户数据时，重新安装仍会沿用原设置。便携版直接运行
压缩包内的 `GenshinFpsUnlocker.exe`，退出程序后删除整个目录即可。

## 技术栈

| 部分 | 技术 | 位置与说明 |
| --- | --- | --- |
| 宿主主程序 | C# / .NET 9（`net9.0-windows10.0.17763.0`）/ WinForms | `src/Host/`：WebView2 承载 Web UI、系统托盘、自绘标题栏、注入调度、游戏定位、配置与日志 |
| Web UI | React 19 + TypeScript 5.9 + Vite 7 + Tailwind CSS 4 + framer-motion + lucide-react | `src/Ui/`：设计稿由 **gpt-6-astra-max** 设计、按稿 1:1 实现；`vite-plugin-singlefile` 打成单文件 `ui/index.html` |
| 注入模块 | C++20（CMake）+ MinHook（BSD-2-Clause），CRT 静态链接（`/MT`） | `src/Stub/`：帧率解锁与反虚化的特征码自适配扫描 + Hook/Patch |
| 安装 / 卸载 / 更新器 | Kachina：Rust + Tauri 2（nightly + `-Z build-std`）+ Vue 3.5 + Rsbuild | `installer/kachina/`：上游源码快照（tag `0.5.1`），本地修改清单见 `installer/kachina/LOCAL_PATCHES.md` |
| exe 图标 / 版本资源写入 | vendored `rcedit-rs`（C++，MSVC 编译） | `installer/kachina/vendor/rcedit-rs/`，与上游差异见其 `LOCAL_PATCHES.md` |
| 构建 / 打包 / 自检 | PowerShell 7 | `build.ps1`、`installer/pack.ps1`、`installer/build-kachina.ps1`、`tools/devcheck/` |
| CI | GitHub Actions（`ubuntu-latest` + `windows-latest`） | `devcheck.yml`（push/PR 自动）、`build.yml`（仅手动） |

## 构建

默认构建主程序和安装器。安装器由项目内 Kachina 源码构建出的 `kachina-builder.exe`
生成，打包代码集中在 [`installer/`](installer/)。

### 常用命令

```powershell
# 仅编译 UI + Stub + 主程序（不打包）
.\build.ps1 -Configuration Release -SkipSetup

# 完整：编译 + 打包 Kachina 离线安装器（首次会从源码构建 kachina-builder）
.\build.ps1 -Configuration Release

# 只重新打包（dist\ 已存在）
.\installer\pack.ps1

# kachina-builder 已构建过 / 强制重建
.\build.ps1 -SkipKachinaBuild
.\build.ps1 -ForceKachinaBuild

# 可选：主程序也自包含
.\build.ps1 -Configuration Release -SelfContained
```

只修改前端时可在 `src/Ui` 执行 `npm ci` 后运行 `npm run build`；只修改安装器前端时，
在 `installer/kachina` 执行 `pnpm install --frozen-lockfile` 后运行 `pnpm exec rsbuild build`。

完整的安装器依赖、目录和打包流程见 [`installer/README.md`](installer/README.md)。

产物：

```text
dist\GenshinFpsUnlocker.exe
dist\FpsUnlockerStub.dll
dist\ui\index.html

artifacts\GenshinFpsUnlocker.Install.{ver}.exe        # Kachina 离线安装器
artifacts\GenshinFpsUnlocker-portable-win-x64.zip    # 便携包（含 .update.exe）
artifacts\GenshinFpsUnlocker_v{ver}.7z               # 便携 7z（本机有 7-Zip 时）
```

Kachina 配置见 [`installer/kachina.config.json`](installer/kachina.config.json)
（默认安装目录 `Program Files\GenshinFpsUnlocker`、GitHub 在线源、运行库列表）。
上游源码快照的来源、版本与构建前置见 [`installer/kachina/UPSTREAM.md`](installer/kachina/UPSTREAM.md)。

## 开发自检（devcheck）

`installer/kachina` 是 Tauri + Windows 专用子项目，完整构建一次要几分钟；
`tools/devcheck` 把**我们真正改过的那部分**放进最小依赖的检查环境，热跑 6–12 秒。

```powershell
pwsh tools/devcheck/devcheck.ps1                 # all
pwsh tools/devcheck/devcheck.ps1 -Layer rust,logic
pwsh tools/devcheck/devcheck.ps1 -SelfTest       # 注入错误，确认每层真的会报错
```

每次 push 由 **Devcheck** 工作流在 ubuntu + windows 双 runner 上自动跑一遍（含 `-SelfTest`）。
分层清单、各层查什么、覆盖范围与抓不到的东西、维护约定，全部见
[`tools/devcheck/README.md`](tools/devcheck/README.md)。

## 安装、更新与卸载

安装与卸载**只有 Kachina 一种实现**。主程序自身不做任何安装/卸载动作：文件与注册表
都由 Kachina 的 `uninst.exe` 处理，程序内的「卸载本软件」按钮只负责**拉起**它
（入口：设置页「高级设置 → 卸载」、关于页底部）。

```powershell
.\artifacts\GenshinFpsUnlocker.Install.1.0.0.exe
```

- 可选安装目录（默认 `C:\Program Files\GenshinFpsUnlocker\`）
- 安装时按 UAC 策略提权；装完后日常运行与开机自启不再弹 UAC
  （勾上「启动时自动以管理员权限运行」时，登录自启改由任务计划程序登记的最高权限任务拉起）
- 可自动处理 .NET Desktop Runtime 9 / VCRedist（见配置 `runtimes`）
- 安装目录生成 **`GenshinFpsUnlocker.uninst.exe`**、**`GenshinFpsUnlocker.update.exe`**
- 安装界面「我已阅读并同意 **用户协议**」可点击，弹窗显示协议全文
  （正文由 `kachina.config.json` 的 `agreementFile` 指向仓库根 `USER_AGREEMENT.txt`，
  打包时内联进 exe，支持 `text` / `markdown` / `html`）；配了协议就**必须勾选同意**
  才能点安装，正文里的外链交给系统浏览器打开，不会把安装器窗口导航走

卸载（四个入口，最终都是同一个 Kachina 卸载器）：

- 程序内：设置页「高级设置 → 卸载 → 卸载本软件」，或关于页底部的「卸载本软件」
  （弹窗确认后拉起 `uninst.exe` 并退出主程序；便携版没有 `uninst.exe`，会提示直接删目录）
- 安装目录下的 **`GenshinFpsUnlocker.uninst.exe`**
- 开始菜单「原神帧率解锁」文件夹里的「卸载 原神帧率解锁」（指向上面那个 exe；便携目录没有 uninst 时不再创建）
- Windows「设置 → 应用 → 安装的应用」/ 控制面板「应用和功能」（Kachina 写的 ARP 卸载项）

卸载时会一并处理：

- **勾选「同时删除用户数据」** → 删 `%LocalAppData%\GenshinFpsUnlocker\`（配置、日志、
  `EBWebView` 界面缓存）与 `%AppData%\`、`文档\` 下的同名目录，覆盖本机**所有登录过的
  用户**，外加 `%TEMP%` 里安装期的残留（按固定文件名白名单）。**不勾选则一个数据目录都不碰**，
  只删快捷方式与开始菜单里的产品文件夹；重装后可沿用原设置。
- 主程序还在运行时（常驻托盘很常见）会先询问并结束进程，否则文件被占用删不掉。
- 开机自启项与快捷方式（含改名后的 `原神帧率解锁.lnk`）按配置一并删除，**不需要先手动关自启动**：
  普通权限自启是 `HKCU\...\Run` 下的值，管理员自启是任务计划程序里的
  `GenshinFpsUnlocker.AutoStart`（配置项 `extraUninstallScheduledTasks`）。
- 所有「按配置删除」的通道都有安全阀（共享注册表容器不整棵删、路径必须绝对且不在系统目录内、
  不碰盘符根与 `Program Files` 这类受保护目录），命中的只记日志并跳过，不会让卸载失败。

已知边界：被 OneDrive 重定向过的 `文档` / `AppData` 只能命中当前用户那一份。
逐条实现与断言见 [`installer/kachina/LOCAL_PATCHES.md`](installer/kachina/LOCAL_PATCHES.md)
第 3、6 节与 [`installer/README.md`](installer/README.md)。

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

## 运行行为

这些行为容易被误解，写清楚省得猜：

- **单实例**：优先用 `Global\` 互斥体（跨会话更稳），拿不到就回退 `Local\`。
  同一用户在两个会话里（例如控制台 + 远程桌面）只会跑一个实例，第二次启动
  会唤醒已有实例的主窗口，而不是再开一个；不同用户互不影响。
  二次点击快捷方式同样是「唤醒并打开主界面」。
- **系统睡眠**：只有在游戏被解锁期间才请求「系统不要自动睡眠」；程序常驻托盘
  本身**不会**阻止睡眠。手动睡眠任何时候都可用。
- **关闭解锁**：关掉「帧率解锁」或总开关时，注入模块会把帧率**写回游戏自身的
  档位**（它 Hook 了游戏的 setter 来记录这个值），不是单纯停止写入。
- **日志**：文件按天切分，跨日自动写到新的 `app-yyyyMMdd.log`；保留天数由
  `logRetainDays` 控制。界面「日志」页实时显示宿主日志（增量推送），
  关闭调试日志后只保留 Info 及以上。
- **提权**：日常运行 `asInvoker`，不弹 UAC；只有主动点「以管理员重新启动」才弹一次，
  在 UAC 上点「否」会原样恢复单实例状态（不会留下「还能再开一个」的窗口期）。
  **已提权且程序目录不在 `Program Files` 下时，会拒绝注入、也拒绝拉起卸载器** ——
  用户可写目录里的同名文件可能借管理员令牌执行。处置办法：装到 `Program Files`，
  或退出管理员实例后按普通权限运行。
- **开机自启动**：两种登记方式**二选一，不会同时存在**（同时存在会在登录时拉起两个实例）。
  只开「开机自启动」→ 写 `HKCU\...\Run`，登录后以标准权限运行；
  同时开「启动时自动以管理员权限运行」→ 改登记任务计划程序里的
  `GenshinFpsUnlocker.AutoStart`（`RunLevel=HighestAvailable`，触发器绑定当前用户 SID），
  登录即以管理员权限启动且**不弹 UAC**。登记计划任务要求程序装在 `Program Files` 下、
  可执行文件通过信任校验、且当前进程是管理员；任一条件不满足就退回 `HKCU\...\Run`，
  并把原因显示在设置页（不静默失败）。关闭开关与卸载时两条通道都会回收。

## 布局

```text
{安装目录}\GenshinFpsUnlocker\     # 默认 Program Files 下
  GenshinFpsUnlocker.exe
  FpsUnlockerStub.dll
  ui\index.html                   # Web UI（WebView2 加载）
  GenshinFpsUnlocker.uninst.exe    # Kachina 卸载
  GenshinFpsUnlocker.update.exe    # Kachina 更新（可选）

%LocalAppData%\GenshinFpsUnlocker\
  config.json
  logs\
```

## CI

两个工作流：

| 工作流 | 触发 | 内容 | 耗时 |
| --- | --- | --- | --- |
| **Devcheck**（`.github/workflows/devcheck.yml`） | push 到 main / PR / 手动，**自动执行** | `tools/devcheck` 全部检查层 + 自检 + Web UI 构建，ubuntu 与 windows 双 runner | 几分钟 |
| **Build**（`.github/workflows/build.yml`） | **仅手动**（Actions → Build → Run workflow） | kachina-builder + 应用本体 + 打包安装器 | 十几分钟起 |

Kachina **只从本仓库的 `installer/kachina/` 源码快照构建**：CI 与打包脚本都不从上游拉源码
或下载二进制，唯一带 C++ 的依赖 `rcedit-rs` 也已 vendored 进仓库。这条约束由 devcheck 的
`vendor` 层自动断言（同一层还断言遥测没有被加回来），细节见
[`installer/kachina/UPSTREAM.md`](installer/kachina/UPSTREAM.md) 与
[`tools/devcheck/README.md`](tools/devcheck/README.md)。

**Build** 依次跑 `build-kachina`（源码 → `kachina-builder.exe`，源码未变时命中缓存，
输入 `rebuild_kachina=true` 可强制重建，重建后的产物照常写回缓存）→ `build-app` → `pack`，可选
`host_mode=self-contained` 打全量自包含主程序；产物与本地构建一致，挂在 Release 上。
把 `Install` 包发布到 Release 且 tag 为 `v{version}` 后，配置里的 GitHub 在线源即可用于更新器。

## 隐私与遥测

**本软件不含任何遥测**：不收集使用统计、不上报崩溃与错误、不生成设备标识。

安装器基于上游 [YuehaiTeam/kachina-installer](https://github.com/YuehaiTeam/kachina-installer)，
上游自带两条外发通道。本项目已把它们**连依赖一起物理移除**——不是运行时关开关，
也不是把地址置空，而是编译产物里连上报地址字符串都不残留：

| 上游通道 | 原来的行为 | 本项目 |
| --- | --- | --- |
| Sentry 错误上报 | Rust 侧把 anyhow 错误连同环境信息（用户名 / 主机名 / 系统版本）上报到 `steambird.cocogoat.cn` | `sentry` / `sentry-tracing` / `whoami` 依赖与全部调用点删除，`utils/sentry.rs` 整份删除；错误只进本地日志文件 |
| 使用统计 | 前端 `sendInsight()` 往 `77.cocogoat.cn/ev` POST 安装 / 完成 / 升级 / 卸载 / 启动 / 出错事件（含屏幕分辨率、系统语言、安装源 id） | 函数与 6 处调用全部删除 |
| 构建期 | `@sentry/cli`（装包时 postinstall 下载 sentry-cli 二进制，用于上传 sourcemap） | 依赖与 `pnpm-workspace.yaml` 白名单一并删除 |

仍然会发生的网络请求（都是功能本身，且由您触发）：

- **安装 / 更新**：从 GitHub Releases 下载安装包（地址见
  `installer/kachina.config.json` 的 `source`）。
- **缺失的运行库**：`builds.dotnet.microsoft.com`（.NET Desktop Runtime 9）、
  `aka.ms/vs/17/release/vc_redist.x64.exe`（VC++ 运行库）、
  `go.microsoft.com/fwlink/p/`（WebView2 引导器）。
- **主程序运行期不联网**：帧率解锁与画面注入全部在本地完成；检测到缺运行库时只弹
  提示，经确认后用系统浏览器打开微软官方下载页（`src/Host/RuntimePrerequisite.cs`）。
- 本地日志与配置写在 `%LocalAppData%\GenshinFpsUnlocker\`，不上传。

删除清单、保留项（`InfoFilter`、本地耗时统计 `networkInsights.ts`）与 lock 重新生成的
细节见 [`installer/kachina/LOCAL_PATCHES.md`](installer/kachina/LOCAL_PATCHES.md) 第 7 节；
`tools/devcheck` 的 `vendor` 层第 8 组断言会在遥测被加回来时直接失败（含 3 个自检注入）。

## 用户协议与安全说明

安装或使用本软件前，请阅读：

- 仓库根目录 [`USER_AGREEMENT.txt`](USER_AGREEMENT.txt)（完整用户协议）
- 程序内「用户协议与安全声明」（首次运行 / 设置中可再次打开）

本工具属于第三方注入类软件，适用《米哈游用户协议》第十条第二款相关表述，**使用风险由您自行承担**。
请关闭游戏 V-Sync 后使用自定义帧率。

安装界面的「用户协议」链接可点击，**弹窗内展示协议全文**（正文由 `agreementFile`
在打包时内联进 exe，支持 `text` / `markdown` / `html`）；配置了协议就必须勾选
「我已阅读并同意」才能点安装。完整协议见仓库 [`USER_AGREEMENT.txt`](USER_AGREEMENT.txt)，
安装后亦释放到安装目录，并在首次运行的程序内「用户协议与安全声明」中展示。

### 安装 / 卸载会动到哪些东西

- **安装写入**：安装目录、桌面与开始菜单的快捷方式、开机自启动项（`HKCU\...\Run` 或
  最高权限计划任务）、「应用和功能」里的卸载信息，以及 .NET / VC++ / WebView2
  运行环境（缺失时才装）。
- **卸载删除**：上面这些，外加你在卸载时勾选的用户数据目录。系统目录、其他软件、
  以及与其他软件共用的注册表容器（开机启动项、卸载信息等）**只删本软件自己的那一份**，
  不会整棵删掉；碰到可疑路径一律跳过并写进日志，不会因为安全检查让卸载失败。
- **下载的运行环境安装包**：落地在只有管理员可写的目录、文件名随机不可预测、运行前
  验证微软签名 —— 签名无效或签名者不是微软就删掉文件并提示手动下载。
- **安装器与提权进程之间的通道**：只允许本用户、SYSTEM 与管理员访问，其他本地程序
  （包括沙箱进程）连不进来。
- 逐条实现记录（面向开发者）见 [`installer/kachina/LOCAL_PATCHES.md`](installer/kachina/LOCAL_PATCHES.md)。

## 参考、素材与版权

### 思路参考的项目

- 帧率解锁与画面效果注入（反角色虚化 / 移除水下马赛克 / 隐藏 UID）的特征码与
  Hook / 隐藏思路参考
  [DGP Studio 的 Snap.Hutao.Remastered.UnlockerIsland](https://github.com/SnapHutaoRemasteringProject/Snap.Hutao.Remastered.UnlockerIsland)（MIT），
  已改编为特征码自适配扫描并整合进 `src/Stub/AntiBlur.cpp` 与 `src/Stub/HideUid.cpp`。
- 安装 / 卸载 / 更新器整体方案来自 [YuehaiTeam/kachina-installer](https://github.com/YuehaiTeam/kachina-installer)
  （源码快照见 `installer/kachina/`，版本与来源见 `installer/kachina/UPSTREAM.md`）。
- 应用图标等图片素材取自 [babalae/better-genshin-impact](https://github.com/babalae/better-genshin-impact)
  （GPL-3.0），逐文件路径见下文「图片与素材来源」。
- 其余实现层面的参考（上游 issue、MSVC/Windows 行为变更等）一律记在对应目录的
  `LOCAL_PATCHES.md` / `tools/devcheck/README.md` 里，代码注释只留一句指针。

### 图片与素材来源

| 文件 | 内容 | 来源（`md5sum` 逐字节核对） | 版权归属 |
| --- | --- | --- | --- |
| `src/Ui/public/images/game-icon.webp` | 《原神》官方应用图标（派蒙头像 + miHoYo 字标） | 米哈游官方素材 | © 米哈游 / HoYoverse |
| `src/Host/Assets/app.png`、`src/Host/Assets/app.ico`、`src/Ui/public/favicon.ico` | 应用图标：绮良良抱纸箱 | [babalae/better-genshin-impact](https://github.com/babalae/better-genshin-impact) 的 `BetterGenshinImpact/Resources/Images/logo.png` / `logo.ico` | 素材随 BetterGI（**GPL-3.0**）；角色形象 © 米哈游 |
| `src/Host/Assets/favicon.ico`、`src/Ui/public/favicon.png` | 同一形象的安装包 / 网页图标变体 | BetterGI 的 `Build/micasetup/Favicon.ico` / `Favicon.png` | 同上 |
| `installer/kachina/src-tauri/icons/icon.ico` | 安装器 / 卸载器 exe 图标 | 上游 kachina-installer 自带（与 tag `0.5.1` 一致）；该文件本身又与 BetterGI `Build/micasetup/FaviconSetup.ico` 同字节 | 同上 |
| `installer/kachina/src/left.webp` | 安装器左侧立绘：绮良良同款立绘 | 上游 kachina-installer 自带（与 tag `0.5.1` 逐字节一致，未改动） | 上游仓库素材（上游未提供 LICENSE） |
| `src/Ui/public/images/teyvat-landscape.jpg` | 概览页 / 指南页的璃月风格山水横幅 | **gpt-6-astra-max 生成的原神风格插画**（个人自用前提下生成，非官方素材） | 风格致敬《原神》；场景本身非米哈游素材 |
| `src/Ui/public/favicon.svg` | 星芒形单色 logo（纯几何路径，304 字节） | 本项目手写 SVG | 本项目（MIT） |

> 注 1：除 `favicon.svg`（本项目手写）与 gpt-6-astra-max 生成的横幅外，仓库内所有图片都与
> 上游 kachina 快照或 BetterGI 仓库中的某个文件**逐字节相同**（核对方式：`md5sum`，
> 路径见上表「来源」列）。BetterGI 以 **GPL-3.0** 发布，这些素材**不随本项目的
> MIT 许可再授权**；升级上游 kachina 时按 `installer/kachina/UPSTREAM.md` 一起更新。
> 注 2：凡涉及《原神》角色、官方图标或美术风格的素材，角色与形象的版权均归米哈游。
> 若任何一张图的权利人提出异议，将从仓库中移除并替换。

### 商标与作品归属（米哈游）

「原神」「Genshin Impact」「派蒙」「绮良良」等名称、角色形象、游戏内场景与官方图标，
版权均归**上海米哈游网络科技股份有限公司 / miHoYo / HoYoverse（COGNOSPHERE PTE. LTD.）**所有。
本项目是独立第三方工具，与米哈游**无任何关联**，未获得其授权、赞助或背书；
上述素材仅随本自用项目保存与展示，不用于任何商业目的。

## License

MIT · MinHook：BSD-2-Clause · 安装包构建工具 Kachina（[kachina-installer](https://github.com/YuehaiTeam/kachina-installer)，源码快照见 `installer/kachina/`）按其上游许可使用 —— **注意：上游仓库未提供 LICENSE 文件**，详见 `installer/kachina/UPSTREAM.md`。
代码与文档主要由 AI 生成并按上述 MIT 许可发布；**图片素材不随 MIT 许可授权** ——
它们分别属于米哈游、BetterGI（GPL-3.0）与上游 kachina 快照（见「图片与素材来源」）。

# 原神帧率解锁 (GenshinFpsUnlocker)

> **项目性质（请先读这一段）**
>
> - **个人自用**：本项目只为作者自己玩游戏服务，不提供任何形式的对外服务、
>   不做商业化、不接受付费；仓库公开仅作为个人备份与学习记录。
> - **AI 生成**：代码与文档**主要由 AI 生成**（Arena.ai Agent Mode 的多模型协作会话；
>   历史提交里的 `arena-agent` 即 AI 会话，后已统一改写为项目所有者署名）。
>   **UI 使用 gpt-6-astra-max 设计**：界面设计稿由该模型出稿，代码按稿 1:1 实现。
>   作者负责提需求、验收结果与承担风险。**它没有经过任何安全审计，
>   请不要把它当作生产级软件对待。**
> - **与米哈游无关**：「原神 / Genshin Impact」及其角色、图标、场景素材的版权归
>   米哈游 / HoYoverse 所有，本项目未获授权或背书，详见下文「素材与版权」。

自定义目标 FPS · 反角色虚化 / 移除水下马赛克（反虚化注入） · 检测游戏启动后后台注入 · 托盘设置 · 开机自启（无 UAC）

界面为 **gpt-6-astra-max** 设计的设计稿一比一实现的 Web UI（WebView2 嵌入）：概览 / 设置 / 日志 / 指南 / 关于；深浅色切换。

关闭或最小化窗口会**驻留系统托盘**（与「启动后最小化」一致）；托盘菜单文案与快捷设置对齐，左键恢复主窗口。
右键菜单为自绘：文字在整行内水平居中、√ 固定在左侧勾选槽内，弹出窗口四角为圆边
（Win11 用 DWM 原生圆角，Win10 用窗口 Region 裁角并把 1px 边框画成同样的圆角）。
进托盘的同时界面页签会复位到「游戏概览」，下次打开不会还停在上次浏览的页面。

## 要求

- **操作系统**：64 位 **Windows 10**（1607 / 版本 14393 及以上）或 **Windows 11**
- **架构**：x64（与游戏客户端一致）
- 构建：.NET 9 SDK、CMake、MSVC（或 VS Build Tools）、**Node.js 20+**（界面）
- 打包安装器：源码已随仓库提供（[`installer/kachina/`](installer/kachina/)），另需 **Rust nightly + `rust-src`** 与 **pnpm 10**；CI 会自动装
- 运行界面：系统需安装 **Microsoft Edge WebView2 Runtime**（Win10/11 通常已自带）
- 运行依赖策略：
  - **应用自身**（托管 DLL、`FpsUnlockerStub.dll`、MinHook 静态编入 Stub 等）随安装包**自包含**
  - **无** Node / Python 等其它编程语言运行时依赖
  - **.NET Desktop Runtime 9**、**VC++ 2015+ x64**：由 **Kachina** 安装器按配置检测/安装（亦可首次运行时由主程序提示下载）
  - 可选 `build.ps1 -SelfContained` 将 .NET 也打进主程序（离线、包体更大）

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

运行侧只依赖 **WebView2 Runtime** 与（默认构建下的）**.NET Desktop Runtime 9 / VC++ 运行库**，
后两者由安装器按配置检测安装；**不需要** Node / Python 等任何开发运行时。

## 构建

默认：**主程序框架依赖（包体小）**。安装器由项目内的 **Kachina** 源码构建出的
`kachina-builder.exe` 生成，全部打包代码集中在 [`installer/`](installer/)：

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

`installer/kachina` 是 Tauri + Windows 专用子项目，完整构建一次要几分钟（nightly +
自定义 target + `-Z build-std` + pnpm），改一行代码只能靠构建来发现写错了。
`tools/devcheck` 把**我们真正改过的那部分**放进最小依赖的检查环境，热跑 6–12 秒：

```powershell
pwsh tools/devcheck/devcheck.ps1                # all：vendored 源 / ps1 语法 / Rust 类型检查 / 行为断言 / vendored C++(仅 Windows) / TS+.vue / Host 构建
pwsh tools/devcheck/devcheck.ps1 -Layer rust,logic
pwsh tools/devcheck/devcheck.ps1 -SelfTest      # 自检：注入 8 个错误，确认每层真的会报错
```

Rust 那两层是关键：

- `rust` —— 整份 `installer/uninstall.rs` + `utils/error.rs` 塞进一个只有 11 个依赖的
  crate，`cargo check --target x86_64-pc-windows-msvc`。不需要 tauri、不需要 Windows 机器，
  却能抓到类型/借用/API 误用（含 `std::os::windows`、`windows-registry`）。
- `logic` —— mock 版 windows-registry 上跑 53 条行为断言：三个删除安全阀、
  `value` 为空的处理、提权时遍历 `HKEY_USERS`；协议那条是拿仓库**真实的**
  `installer/kachina.config.json` + `USER_AGREEMENT.txt` 跑 `resolve_agreement`，
  逐字节比对内联结果。

`vendor` 层把「kachina 只从本仓库拉」变成可执行断言：不是 submodule、快照完整、
工作流与打包脚本里没有任何从上游拉源码/下二进制的动作、CI 确实走源码构建、
git 依赖在 `Cargo.lock` 里锁到 commit、npm 依赖全来自 registry。

`-SelfTest` 会注入 8 个错误（`.gitmodules`、含 `Invoke-WebRequest` 的假工作流、
`rescle.cc` 用回 `locale::empty()`、PowerShell 语法、Rust 类型、安全阀被放宽、
TS 类型、`.vue` 模板），确认每一层都会报错 —— 避免「检查跑通了但其实什么都没查」。
覆盖范围、抓不到的东西与维护约定见 [`tools/devcheck/README.md`](tools/devcheck/README.md)。

## 安装 / 卸载

安装与卸载**只有 Kachina 一种实现**。主程序自身不做任何安装/卸载动作：
`--install`、`--uninstall`、`Uninstall.cmd` 垫片、自写 ARP 卸载注册表项、
内置白名单删目录均已移除。程序内的「卸载本软件」按钮只负责**拉起** Kachina 的
`uninst.exe`，不碰任何文件与注册表（入口：设置页「高级设置 → 卸载」、关于页底部）。

```powershell
.\artifacts\GenshinFpsUnlocker.Install.1.0.0.exe
```

- 可选安装目录（默认 `C:\Program Files\GenshinFpsUnlocker\`）
- 安装时按 UAC 策略提权；装完后日常运行与开机自启不再弹 UAC
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

卸载向导中勾选「同时删除用户数据（配置、日志与界面缓存）」会按
`kachina.config.json` 的 `userDataPath` 清掉 `%LocalAppData%\GenshinFpsUnlocker\`
（配置、日志、WebView2 的 `EBWebView` 界面缓存），并同时尝试 `%AppData%\` 与
`文档\` 下的同名目录——宿主的 `AppPaths.DataDirectory` 就是按
「LocalAppData → Roaming → 文档」取第一个可写的目录，数据不一定落在第一处。
清理覆盖**本机所有登录过的用户**：卸载器一般以管理员身份运行，只清管理员自己的
`%LocalAppData%` 会漏掉当初装软件的那个账户。`%TEMP%` 里安装 / 卸载过程产生的
文件（`KachinaInstaller.log`、运行时安装包、WebView2 引导器、卸载器临时副本）
也按固定文件名白名单删掉，不认识的条目不碰。卸载开始前若检测到主程序还在运行
（常驻托盘时很常见），会先询问并结束进程——否则它自己的 exe、日志与界面缓存
都被占用，删不掉就是残留。
**不勾选则一个数据目录都不会碰**：前端未勾选时传给卸载器的 `userDataPath` 是空数组，
跨用户清理吃的又是同一份列表，所以「勾选才删数据」这条不需要第二套开关；此时只会删
快捷方式与开始菜单里的产品文件夹（其它用户桌面上指向已删除 exe 的死图标也一并清掉，
那不属于用户数据）。重装后可沿用原设置。被 OneDrive 重定向过的
`文档` / `AppData` 只能命中当前用户那一份，这是已知边界。
开机自启项（`HKCU\...\Run` 下的 `GenshinFpsUnlocker` 值）由主程序按配置写入，
卸载器会按配置项 `extraUninstallRegistry` 一并删除（提权卸载时会遍历
`HKEY_USERS` 保证删到登录用户那一份），**不需要先手动关闭自启动**。
卸载器同时会删除快捷方式与 ARP 卸载登记项：除了它自己建的
`GenshinFpsUnlocker.lnk` 与开始菜单文件夹，还会按 `extraUninstallLnkNames`
补删宿主改名后的中文快捷方式 `原神帧率解锁.lnk`（公共桌面 / 用户桌面 /
两侧开始菜单都试，OneDrive 重定向的桌面也能命中；删不掉只记日志，不影响卸载）。

卸载器以管理员身份运行，因此所有「按配置删除」的通道都加了安全阀：注册表只删
`extraUninstallRegistry` 明确指到的值/子键（共享容器如 `Run`、`Uninstall`、`Policies`
不允许整棵删，`value` 留空视为配置错误直接跳过）；快捷方式与数据目录必须是绝对路径、
不含 `..`、不是符号链接 / junction、不在 `%SystemRoot%` 内，且不能是盘符根或
`Program Files` / `%LocalAppData%` 这类受保护目录本身。命中的路径只记日志并跳过，
不会让卸载失败。详见 `installer/README.md` 与 `installer/kachina/LOCAL_PATCHES.md`。

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

Kachina **只从本仓库的 `installer/kachina/` 源码快照构建**，CI 与打包脚本都不从上游
仓库拉源码或下载二进制 —— kachina 唯一带 C++ 的依赖 `rcedit-rs` 也已 vendored 进
`installer/kachina/vendor/rcedit-rs/`（上游用了 MSVC 14.51 已移除的 `std::locale::empty()`，
在现在的 `windows-latest` 上编不过，详见该目录的 `LOCAL_PATCHES.md`）。
这条「只用仓库内源码」的约束由 devcheck 的 `vendor` 层自动断言（不是 submodule、
快照完整、工作流与脚本里没有任何外部拉取动作、git 依赖锁到 commit），每次 push 都会验。

Build 的三个 job 并行/串行协作，**不再从上游 Release 下载 `kachina-builder.exe`**：

1. `build-kachina` —— 用 `installer/kachina/` 源码构建 `kachina-builder.exe`。
   源码未变时命中 `actions/cache`（key = `hashFiles('installer/kachina/**')`）直接复用；
   输入 `rebuild_kachina=true` 可强制重建。
2. `build-app` —— `build.ps1 -SkipSetup` 产出 `dist\`。
3. `pack` —— `installer\pack.ps1 -SkipKachinaBuild` 产出最终安装包。

产物：

- `GenshinFpsUnlocker-portable-win-x64.zip`
- `GenshinFpsUnlocker_v*.7z`（若 runner 有 7z）
- `GenshinFpsUnlocker.Install.*.exe`（Kachina）

工作流可选 `host_mode=self-contained` 打全量自包含主程序。

将 `Install` 包发布到 Release 且 tag 为 `v{version}` 后，配置中的 GitHub 在线源即可用于更新器。

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

## 参考、素材与版权

### 思路参考的项目

- 帧率解锁与反虚化（反角色虚化 / 移除水下马赛克）的特征码与 Hook/Patch 思路参考
  [DGP Studio 的 Snap.Hutao.Remastered.UnlockerIsland](https://github.com/SnapHutaoRemasteringProject/Snap.Hutao.Remastered.UnlockerIsland)（MIT），
  已改编为特征码自适配扫描并整合进 `src/Stub/AntiBlur.cpp`。
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

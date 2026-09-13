# installer/ — 安装器与发布包

本项目的安装 / 卸载 / 更新**只有 Kachina 一种实现**。宿主程序（`src/Host`）里已经
没有任何自带的安装卸载路径（`--install`、`--uninstall`、`Uninstall.cmd` 垫片、
自写 ARP 卸载注册表项、内置白名单删目录都已删除）。

面向使用者只需要下载根目录 `artifacts\` 中的安装包；本目录供开发者构建、配置和排查安装器。
所有与打包安装器相关的代码都集中在这里：

```
installer/
├── README.md               本文件
├── kachina.config.json     Kachina 配置（安装目录、ARP 名称、运行库、协议正文、卸载时清理的数据目录与注册表）
├── build-kachina.ps1       从 kachina/ 源码构建 kachina-builder.exe
├── pack.ps1                打包总入口：dist\ → Install.exe / 便携 zip / 便携 7z
├── tools/                  构建产物 kachina-builder.exe（.gitignore，不进版本库）
└── kachina/                上游 kachina-installer 源码快照 + 本地修改（tag 0.5.1，
    │                       见 kachina/UPSTREAM.md 与 kachina/LOCAL_PATCHES.md）
    └── vendor/rcedit-rs/   vendored 的 cargo 依赖（上游 rcedit-rs@1bfa3ee + 1 行 C++
                            修复：MSVC 14.51 移除了 std::locale::empty()，
                            见该目录的 LOCAL_PATCHES.md）
```

## 快速开始

在仓库根目录执行：

```powershell
# 仓库根目录：编译 UI + Stub + Host，再打包安装器
.\build.ps1

# 已经编译过、只想重新打包
.\installer\pack.ps1

# 只编译，不打包
.\build.ps1 -SkipSetup
```

首次构建需要 Rust nightly、`rust-src`、Node.js 20+、pnpm 10、PowerShell 7 和 Windows
MSVC/VS Build Tools。Windows 目标的 Tauri/C++ 编译阶段需要 Windows MSVC 工具链。
安装器前端依赖安装命令如下：

```bash
cd installer/kachina
pnpm install --frozen-lockfile
```

首次执行会从源码构建 `kachina-builder.exe`（需要 Rust nightly + Node/pnpm + MSVC，
详见 `kachina/UPSTREAM.md`）。之后 `installer\tools\kachina-builder.exe` 存在**且不比
`installer\kachina\` 里的源码旧**才跳过——改了 kachina 源码（例如本仓库对上游的
本地修改）会自动触发重建，不会拿着旧 builder 打包：

```powershell
.\build.ps1 -SkipKachinaBuild    # 要求 tools\kachina-builder.exe 已存在，且完全不做检查
.\build.ps1 -ForceKachinaBuild   # 强制重建
```

> 判据是文件修改时间（跳过 `node_modules` / `dist` / `target` / `gen` / `.cache`），
> 所以 `git checkout` 触碰过的文件可能触发一次多余的重建；确定不需要时用
> `-SkipKachinaBuild`。CI 里 `build-kachina` 命中缓存时根本不会调用该脚本，
> 不受影响。

## 产物

统一落在仓库根的 `artifacts\`：

| 文件 | 说明 |
| --- | --- |
| `GenshinFpsUnlocker.Install.<ver>.exe` | 离线安装器。装完的安装目录里含 `GenshinFpsUnlocker.uninst.exe`（卸载）与 `GenshinFpsUnlocker.update.exe`（在线更新） |
| `GenshinFpsUnlocker-portable-win-x64.zip` | 便携包（内含 `update.exe`，可直接升级） |
| `GenshinFpsUnlocker_v<ver>.7z` | 便携 7z（本机检测到 7-Zip 时才生成） |

## 打包步骤（`pack.ps1` 内部做的事）

`pack.ps1` 按以下三步生成更新器、索引和离线安装器：

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

Kachina 是本项目唯一的安装、卸载和在线更新实现。宿主程序只负责启动卸载器，不直接
删除安装目录或注册表。用户侧入口和数据保留规则请先看根目录 [`README.md`](../README.md)
的「安装、更新与卸载」；本文件下面的内容主要用于维护配置和审查删除范围。

**负责**：铺文件到 `Program Files\GenshinFpsUnlocker`、写「应用和功能」卸载项、
生成 `uninst.exe` / `update.exe`、按 `runtimes` 装 .NET Desktop Runtime 9 与 VCRedist、
按 `uacStrategy` 提权、安装时创建桌面 + 开始菜单快捷方式（安装界面有勾选项，默认勾上）、
卸载时删除这些快捷方式、删除 ARP 注册表项，并按 `userDataPath` 清用户数据
（配置 / 日志 / WebView2 数据，见下）。

**不负责**，由宿主自己维护：

| 事项 | 归属 | 说明 |
| --- | --- | --- |
| 快捷方式的**中文显示名** | `src/Host/ShortcutHelper.cs` | Kachina 建的是 `GenshinFpsUnlocker.lnk`（英文 `appName`），宿主每次启动把它规范成 `原神帧率解锁.lnk` 并清掉英文重复项；改名后上游卸载器认不出这个文件，靠 `extraUninstallLnkNames` 补删（见下） |
| 开机自启（`HKCU\...\Run`） | `src/Host/Autostart.cs` | 按配置项「开机自启动」同步写入/删除；卸载时由 `kachina.config.json` 的 `extraUninstallRegistry` 交给卸载器回收（见下），不需要用户先手动关闭 |

## 本项目给 Kachina 加 / 改的配置项

下面几项上游都没有（`userDataPath` 上游有字段但行为有坑），改动都在 `kachina/` 里，
逐处说明见 [`kachina/LOCAL_PATCHES.md`](kachina/LOCAL_PATCHES.md)。

### `extraUninstallRegistry` — 卸载时清理安装期写入的注册表

```json
"extraUninstallRegistry": [
  { "hive": "HKCU", "key": "Software\\Microsoft\\Windows\\CurrentVersion\\Run", "value": "GenshinFpsUnlocker" }
]
```

| 字段 | 说明 |
| --- | --- |
| `hive` | `HKCU` / `HKLM` / `HKCR` / `HKU`（大小写不敏感，也接受全称） |
| `key` | 子键路径 |
| `value` | 给了就只删这一个值；省略则**递归删除整个子键**（`remove_tree`），慎用 |

本项目宿主的开机自启写在 `HKCU\...\Run` 的 `GenshinFpsUnlocker` 值上
（`src/Host/Autostart.cs`），所以卸载必须回收它。注意卸载器一般以管理员身份运行，
此时 `HKCU` 指向的是管理员账户；因此 `hive: HKCU` 会**额外遍历 `HKEY_USERS`**
下已加载的用户配置单元（跳过 `*_Classes`、`.DEFAULT`、`S-1-5-18`），
确保删掉的是登录用户装的那一份。ARP 卸载项仍由上游逻辑按 `regName` 删除，
不要在这里重复声明。清理失败只记日志，不会中断卸载。

### `extraUninstallLnkNames` — 卸载时清理宿主自建/改名的快捷方式

```json
"extraUninstallLnkNames": [
  "原神帧率解锁.lnk",
  "GenshinFpsUnlocker.lnk",
  "GenshinFpsUnlocker.exe.lnk",
  "Genshin FPS Unlocker.lnk"
]
```

只写**文件名**，目录由卸载器用 shell API 解析后拼出来，四侧都试：
公共桌面 / 用户桌面、公共开始菜单 / 用户开始菜单下的 `{appName}\` 文件夹。
这样即使用户桌面被 OneDrive 重定向、或宿主当初写在了另一侧，也能删干净。

这些路径走的是**尽力删除**：删不掉（无权限、被占用）只写日志，
不会把卸载判为失败——上游 `extraUninstallPath` 的语义是删不掉就报错中断，
不适合放这种「清理不干净但不致命」的路径。

### `userDataPath` — 卸载时清理用户数据（勾选后才生效）

```json
"userDataPath": [
  "%LOCALAPPDATA%/GenshinFpsUnlocker",
  "%APPDATA%/GenshinFpsUnlocker",
  "%USERPROFILE%/Documents/GenshinFpsUnlocker"
]
```

这三个目录**不是**历史遗留兜底，而是宿主当前就在用的可写性回退链：
`src/Host/AppPaths.cs` 的 `DataDirectory` 依次尝试
`LocalApplicationData` → `ApplicationData`(Roaming) → `MyDocuments`，
用**第一个能创建并通过写探测的**目录（`%LOCALAPPDATA%` 被组策略 / ACL /
漫游配置挡住时就会落到后两个）。所以卸载必须三处都试，否则换了落盘位置的
用户数据就清不掉。不存在的目录自动跳过。

上游卸载器在这里有两个坑，本地补丁都填了（详见
[`kachina/LOCAL_PATCHES.md`](kachina/LOCAL_PATCHES.md) 第 6 节）：

| 坑 | 后果 | 补丁 |
| --- | --- | --- |
| 配置里的 `%VAR%` **从不展开**（前端只认 `${INSTALL_PATH}` / `${APP_NAME}`） | 字面量 `%LOCALAPPDATA%/...` 不是绝对路径，被删除安全阀当成「不安全路径」静默跳过——**勾了也不会删** | 卸载器在安全检查之前先展开 `%VAR%`（手写实现，未知变量原样保留，不猜） |
| 只清理**当前进程**的用户目录，而卸载器通常以管理员身份运行 | 当初装软件的普通用户那份数据、以及该用户桌面 / 开始菜单里的快捷方式全部残留 | 把路径剥成「相对用户目录的尾巴」，重放到 `ProfileList` 里所有已加载的用户目录上 |

**没勾选就一个数据目录都不会碰**：前端未勾选时 `user_data_path` 传的是空数组，
而跨用户清理吃的正是同一份 `to_be_delete`，所以「勾选才删数据」不需要第二套开关。
此时仍会删的只有快捷方式与开始菜单里的产品文件夹（含其它用户桌面上指向已删除 exe
的死图标）—— 那不属于用户数据。`%TEMP%` 里清的是 Kachina 自己的安装期文件
（日志、运行时安装包、引导器、卸载器临时副本），同样不是用户数据。

跨用户重放对「数据目录」和「快捷方式 / 开始菜单文件夹」分别跟随各自的语义：
数据目录只在勾选后才会跨用户删（前端没勾就传空数组），而快捷方式与开始菜单文件夹
不受勾选影响 —— 其它用户桌面上指向已删除 exe 的死图标总归要清掉。

另外两类残留也一并处理：

- `%TEMP%` 里 Kachina 自己留下的文件（`KachinaInstaller.log` 日志、
  `Kachina.RuntimePackage.*.exe` 运行时安装包、`kachina.MicrosoftEdgeWebview2Setup.exe`
  引导器、`kachina.uninst.*.exe` 卸载器临时副本）按**固定文件名白名单**删，
  只删文件、不递归、跳过正在运行的卸载器自身；
- 卸载开始前会检测主程序是否在运行（常驻托盘时很常见），询问后结束进程再删 ——
  否则它自己的 exe、`logs\` 与 WebView2 的 `EBWebView` 缓存都被占用，删不掉就是残留。
  拒绝结束进程则整个卸载不执行，回到卸载界面。`silent` / `non_interactive` 直接结束。

**已知不覆盖**：被 OneDrive 重定向过的 `Documents` / `AppData`
（重定向后的真实位置不在 `ProfileList` 的 `ProfileImagePath` 里）只能命中当前进程
用户那一份；WebView2 的 `EBWebView` 目录与凭据管理器条目本项目不产生，未处理。

### `agreementFile` / `agreementFormat` / `agreementTitle` — 可配置的用户协议

```json
"agreementFile": "../USER_AGREEMENT.txt",
"agreementFormat": "text",
"agreementTitle": "用户协议"
```

- `agreementFile` 相对**配置文件所在目录**解析，这里指向仓库根的 `USER_AGREEMENT.txt`；
  与 `pack.ps1` 的工作目录无关。
- `agreementFormat` 支持 `text`（原样保留换行缩进）/ `markdown` / `html`，
  三者渲染结果统一过 DOMPurify 再 `v-html`。
- 打包（`pack`）时正文被**内联进 exe**（`agreement: { title, format, content }`），
  所以离线安装器、`update.exe`、`uninst.exe` 共用同一份协议，运行期不读文件、不联网。
- 安装界面的「我已阅读并同意 **用户协议**」里，链接可点击，弹窗显示全文；
  弹窗底部「我已阅读并同意」会顺手勾上同意框（「关闭」只关弹窗，不改勾选状态）。
  正文可滚动、按钮固定在下方不遮正文 —— 安装窗口只有 520×250，弹窗骨架因此改成
  纵向 flex，细节见 `kachina/LOCAL_PATCHES.md` 第 5 节。
- **内联了协议正文就必须主动勾选**才能点「安装」（`acceptEula` 初始为 `false`）。
  没有协议内容时保持上游默认（视为已同意）；`silent` / `non_interactive` 安装、
  更新、卸载都不受影响。
- 正文统一过 DOMPurify，且策略比上游更严：禁 `style/form/input/iframe/object/embed/
  link/meta/base/svg/math` 等标签与 `style/srcdoc/formaction/data/background` 等属性，
  URI 只放行 `http(s)` / `mailto` / 页内锚点。正文里的链接点击一律被拦截
  （安装器窗口不能被导航走），`http(s)` 外链交给系统浏览器打开。
- 读文件失败只打印 warning 并继续打包，此时链接退化为不可点击的纯文字
  （与上游行为一致）。

## 卸载器的删除安全阀（防误删 / 防被利用提权）

卸载器通常以管理员身份运行（`uacStrategy: "prefer-admin"`），而「删什么」来自
打包配置。为了不让配置笔误或被篡改的安装目录被管理员权限放大，本地补丁加了
几道安全阀（详见 `kachina/LOCAL_PATCHES.md` 第 3 节）：

| 通道 | 规则 |
| --- | --- |
| `extraUninstallRegistry` 删值 | 子键至少两级，不碰任何根键的直属项 |
| `extraUninstallRegistry` 删整棵子键 | 至少三级，且末级不能是共享容器（`Run`/`RunOnce`/`Uninstall`/`Policies`/`Explorer`/`Classes`/`Windows`/`Services`…）；`value` 写成空字符串视为配置错误，整条跳过 |
| `extraUninstallLnkNames` 快捷方式 | 绝对路径、无 `..`、自身与所有父级都不是符号链接 / junction、不在 `%SystemRoot%` 内；目录只放行 `Programs\<产品名>`，文件只放行 `Desktop\*.lnk` 或 `Programs\<产品名>\*.lnk` |
| `userDataPath` / `extraUninstallPath` | 同样的形状校验 + 至少两级 + 不能是受保护根目录本身（盘符根、`%SystemRoot%`、`%ProgramFiles%`、`%ProgramData%`、`%USERPROFILE%`、`%APPDATA%`、`%LOCALAPPDATA%`、`%PUBLIC%`、`%TEMP%`），也不能是配置目录下面一层的 **Shell 容器**（`Desktop`、`Documents`、`Downloads`、`AppData[\Local\|\Roaming]`、`…\Start Menu\Programs[\Startup]`、`%PUBLIC%\Desktop` 等）——产品目录一定在容器下面至少一层，所以正常清理不受影响 |
| 同上路径重放到**其他用户**目录 | 尾巴第一段必须是 `AppData` / `Documents` / `Desktop`、至少两级（`AppData` 下至少三级）、无 `..`；`Desktop` 下只放行 `.lnk`；尾巴的**叶子名不能是 Shell 容器**（`PER_USER_DENY_LEAVES`：`Programs`、`Start Menu`、`Microsoft`、`Local`、`Documents`、`Desktop`、`Cache`、`OneDrive` 等 30 余个）；重放结果再过一遍上面的 `is_safe_delete_target`，且必须是真实存在的目录或 `.lnk` |
| `%TEMP%` 下的安装期临时文件 | 只认四个固定文件名形状（`KachinaInstaller.log`、`Kachina.RuntimePackage.*.exe`、`kachina.MicrosoftEdgeWebview2Setup.exe`、`kachina.uninst.*.exe`）；只删文件不删目录、不递归、跳过正在运行的卸载器自身 |

被拒绝的路径只记 `warn` 日志，卸载继续。本项目现有配置全部落在放行范围内。

宿主侧（`src/Host/`）另有一道：程序内「卸载本软件」启动 `uninst.exe` 前会校验
路径在自身目录内、文件名符合约定、不是符号链接、目录不是系统/配置根目录；
**宿主已提权时还要求安装目录位于 `Program Files` 下**，否则拒绝启动
（避免普通用户在可写目录放同名 exe 借管理员令牌执行）。

## CI

两个工作流：**Devcheck**（`devcheck.yml`，push/PR 自动执行，跑 `tools/devcheck` 的
全部检查层 + 自检，几分钟）与 **Build**（`build.yml`，仅手动触发，出安装包）。

Devcheck 的 windows job 里有一层 `native`：用 runner 上的 MSVC 真编一遍
`kachina/vendor/rcedit-rs/rcedit-sys` 的 C++。这样「工具链升级把 vendored C++ 编坏」
这类问题（MSVC 14.51 移除 `std::locale::empty()` 就是这么炸的）在**自动**工作流里
就会暴露，不用等手动触发 Build 跑 6 分钟。

`build.yml` 三个 job：

1. `build-kachina` —— 用 `installer/kachina` 源码构建 `kachina-builder.exe`；
   源码未变时命中 `actions/cache`（key = `hashFiles('installer/kachina/**')`）直接复用，
   也可用 `rebuild_kachina` 输入强制重建。
2. `build-app` —— `build.ps1 -SkipSetup` 产出 `dist\`。
3. `pack` —— 下载前两者，执行 `installer\pack.ps1 -SkipKachinaBuild`。

**不再从上游 Release 下载 `kachina-builder.exe`**，也不会从上游仓库拉源码：
kachina 只来自本仓库的 `installer/kachina/` 快照。这条约束由
`pwsh tools/devcheck/devcheck.ps1 -Layer vendor` 自动断言（不是 submodule、
快照完整、工作流与打包脚本里没有任何 `git clone` / `releases/download` /
`Invoke-WebRequest` 之类的外部拉取动作、git 依赖在 `Cargo.lock` 里锁到 commit、
npm 依赖全部来自 registry），并且每次 push 都会在 Devcheck 工作流里跑一遍。

CI 仍会联网获取 crates.io / npm registry / rustup 工具链 / marketplace action ——
这是任何构建都免不了的；被禁止的是「kachina 本体来自本仓库之外」。

### workflow 里那些看着多余的设置

工作流与脚本的注释只留一句指针，完整理由记在这里（约定：**思路、踩坑过程、
参考的项目/issue 一律写文档，不写进代码注释**）。

| 设置 | 为什么 |
| --- | --- |
| `RUST_TOOLCHAIN: nightly` + `-Z build-std` | 上游 kachina 的构建方式，stable 工具链编不过 |
| `CMAKE_GENERATOR: Ninja`（`build-kachina` job） | `seera-msquic` 的静态构建会在 `target\<三元组>\release\build\seera-msquic\<hash>\out\build\CMakeFiles\CMakeScratch\TryCompile-*\...` 这种极深路径下写 `.tlog`，超过 Windows 260 字符上限时 MSBuild 的 FileTracker 报 `error FTK1011: could not create the new file tracking log file`。Ninja 不写 `.tlog`，从根上绕开；同 job 里的 `Enable Windows long paths`（`LongPathsEnabled=1`）是第二道防线。**副作用**：Ninja 不会像 MSBuild 那样自己去 VS 安装目录找 `cl.exe`，所以必须先跑 `ilammy/msvc-dev-cmd@v1` 把 `PATH` / `INCLUDE` / `LIB` 注入进去 |
| `NODE_NO_WARNINGS: "1"` | `actions/setup-node` 自己（含它的 post-job 缓存步骤）会打 `[DEP0040] punycode` / `[DEP0169] url.parse()` 弃用告警，是 action 内部依赖的事，跟本仓库无关。`env` 对所有步骤生效，在这里统一静音 |
| `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24: "true"` | 2025-10 起「声明 node20 的 action 一律强制跑 node24」已是 runner 默认行为，这个变量留着只是显式声明。日志里那条 `##[warning]Node.js 20 is deprecated` 来自第三方 action 自己的 `action.yml`（清单见下表），删不删这个变量都会打 |
| kachina 的 `rcedit = { path = "../vendor/rcedit-rs" }` | 原本是 git 依赖，但上游 C++ 用了新版 MSVC 已移除的非标准扩展，编不过。完整出处（含上游仓库、快照 commit、对应的 MSVC STL 变更）见 `kachina/vendor/rcedit-rs/LOCAL_PATCHES.md` |
| 仓库根的 `.gitattributes`（`* text=auto eol=lf`） | windows-latest 的 git 默认 `core.autocrlf=true`，检出成 CRLF 后 `prettier --check` 在 Windows 上必挂。详见 `../tools/devcheck/README.md`「跨平台的坑」 |
| `git config --global init.defaultBranch main`（放在 checkout 之前） | `actions/checkout` 会先 `git init`，ubuntu 镜像上默认分支名还是 `master`，每次打 8 行 hint。同上 |

### 已经在 CI 上跑通

2026-09-12，commit `3f7c770`，手动触发（勾了 `rebuild_kachina`），**三个 job 全绿，
用时 17 分 40 秒**，产物：

| 产物 | 大小 |
| --- | --- |
| `GenshinFpsUnlocker.Install.1.0.0.exe`（离线安装器） | 14.39 MB |
| `GenshinFpsUnlocker_v1.0.0.7z`（便携版） | 7.60 MB |
| `kachina-builder.exe`（从 `installer/kachina/` 源码构建） | 11.59 MB |

### Build 日志里这些告警是正常的（都不是本项目的代码）

| 字样 | 来源 |
| --- | --- |
| `warning: suspicious definition of the runtime memcmp/memcpy/memmove/memset/strlen symbol`（各 5 条 ×2） | kachina 自带的 C 库 `hdiff-sys` / `hpatch-sys` 自己实现了这些符号 |
| `warning: field \`0\` is never read` → `kachina-installer (bin "kachina-builder") generated 1 warning` | 上游 `src/cli/arg.rs:42` 的 `Command::Other(Vec<String>)`，不是我们改过的文件；替上游改会给以后升级添乱 |
| `warning: the following packages contain code that will be rejected by a future version of Rust: russh v0.54.5` | 第三方依赖的 future-incompat 提示 |
| `Could Not Find ...\target\x86_64-win7-windows-msvc\release\kachina-builder...` | tauri CLI 自己探测产物路径的输出；实际产物落在不带三元组的 `target\release\`，`build-kachina.ps1` 的兜底分支会接住它 |
| `##[warning]Node.js 20 is deprecated ... forced to run on Node.js 24` | `ilammy/msvc-dev-cmd@v1`、`pnpm/action-setup@v4`、`actions/download-artifact@v6` 的 action.yml 仍声明 node20；msvc-dev-cmd 已是最新 v1.13.0，只能等上游 |

## 升级上游 Kachina

见 `kachina/UPSTREAM.md` 的「升级上游版本」小节。注意本目录有本地修改，
覆盖上游后必须按 `kachina/LOCAL_PATCHES.md` 的「升级上游时的套用顺序」重新套用；
升级后也要同步检查 `kachina.config.json`（若上游新增了配置项）与本目录的说明。

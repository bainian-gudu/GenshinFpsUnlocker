# installer/ — 唯一的安装器打包目录

本项目的安装 / 卸载 / 更新**只有 Kachina 一种实现**。宿主程序（`src/Host`）里已经
没有任何自带的安装卸载路径（`--install`、`--uninstall`、`Uninstall.cmd` 垫片、
自写 ARP 卸载注册表项、内置白名单删目录都已删除）。

所有与「打包安装器」相关的代码都集中在本目录：

```
installer/
├── README.md               本文件
├── kachina.config.json     Kachina 配置（安装目录、ARP 名称、运行库、协议正文、卸载时清理的数据目录与注册表）
├── build-kachina.ps1       从 kachina/ 源码构建 kachina-builder.exe
├── pack.ps1                打包总入口：dist\ → Install.exe / 便携 zip / 便携 7z
├── tools/                  构建产物 kachina-builder.exe（.gitignore，不进版本库）
└── kachina/                上游 kachina-installer 源码快照 + 本地修改（tag 0.5.1，
                            见 kachina/UPSTREAM.md 与 kachina/LOCAL_PATCHES.md）
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
| 快捷方式的**中文显示名** | `src/Host/ShortcutHelper.cs` | Kachina 建的是 `GenshinFpsUnlocker.lnk`（英文 `appName`），宿主每次启动把它规范成 `原神帧率解锁.lnk` 并清掉英文重复项；改名后上游卸载器认不出这个文件，靠 `extraUninstallLnkNames` 补删（见下） |
| 开机自启（`HKCU\...\Run`） | `src/Host/Autostart.cs` | 按配置项「开机自启动」同步写入/删除；卸载时由 `kachina.config.json` 的 `extraUninstallRegistry` 交给卸载器回收（见下），不需要用户先手动关闭 |

## 本项目给 Kachina 加的三个配置项

上游没有这两项能力，改动都在 `kachina/` 里，逐处说明见
[`kachina/LOCAL_PATCHES.md`](kachina/LOCAL_PATCHES.md)。

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
  弹窗底部「我已阅读并同意」会顺手勾上同意框。
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
| `userDataPath` / `extraUninstallPath` | 同样的形状校验 + 至少两级 + 不能是受保护根目录本身（盘符根、`%SystemRoot%`、`%ProgramFiles%`、`%ProgramData%`、`%USERPROFILE%`、`%APPDATA%`、`%LOCALAPPDATA%`、`%PUBLIC%`、`%TEMP%`） |

被拒绝的路径只记 `warn` 日志，卸载继续。本项目现有配置全部落在放行范围内。

宿主侧（`src/Host/`）另有一道：程序内「卸载本软件」启动 `uninst.exe` 前会校验
路径在自身目录内、文件名符合约定、不是符号链接、目录不是系统/配置根目录；
**宿主已提权时还要求安装目录位于 `Program Files` 下**，否则拒绝启动
（避免普通用户在可写目录放同名 exe 借管理员令牌执行）。

## CI

两个工作流：**Devcheck**（`devcheck.yml`，push/PR 自动执行，跑 `tools/devcheck` 的
全部检查层 + 自检，几分钟）与 **Build**（`build.yml`，仅手动触发，出安装包）。

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

## 升级上游 Kachina

见 `kachina/UPSTREAM.md` 的「升级上游版本」小节。注意本目录有本地修改，
覆盖上游后必须按 `kachina/LOCAL_PATCHES.md` 的「升级上游时的套用顺序」重新套用；
升级后也要同步检查 `kachina.config.json`（若上游新增了配置项）与本目录的说明。

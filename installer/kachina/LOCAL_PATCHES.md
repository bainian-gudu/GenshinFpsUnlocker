# 本目录对上游 kachina-installer 的本地修改

上游快照：tag `0.5.1` / commit `ae461aa9ddd5a8f5e445459e14f5d611921f9938`（见 `UPSTREAM.md`）。

本目录**不是纯净快照**：为了本项目的需求，在 8 个上游文件 + 2 处新增
（`src/utils/agreement.ts`、`vendor/rcedit-rs/`）上做了最小化改动。升级上游版本时
必须按本清单重新套用（都是「加字段 / 加分支 / 加样式覆盖」，不改动上游既有逻辑，
冲突概率低）。

| # | 需求 | 涉及文件 |
| --- | --- | --- |
| 1 | 卸载时清理安装期写入的注册表（开机自启动等） | `src-tauri/src/installer/uninstall.rs`、`src/App.vue`、`src/types.ts`、`src/api/ipc.ts` |
| 1b | 卸载时清理安装期由宿主自建/改名的快捷方式 | 同上 4 个文件 |
| 2 | 用户协议可配置、多格式、点击弹窗看全文 | `src-tauri/src/builder/pack.rs`、`src/App.vue`、`src/types.ts`、`src/utils/agreement.ts`（新增） |
| 3 | 安全加固：收敛卸载器的删除范围与提权面 | `src-tauri/src/installer/uninstall.rs`、`src/utils/agreement.ts`、`src/App.vue`（另有宿主侧 `src/Host/UninstallLauncher.cs`、`src/Host/RuntimePrerequisite.cs`，不属于本目录） |
| 4 | 让 kachina 在 MSVC 14.51（VS 2026 / windows-latest）上还能编过 | `src-tauri/Cargo.toml`、`src-tauri/Cargo.lock`、`vendor/rcedit-rs/`（新增，vendored 依赖 + 1 行 C++ 修复） |
| 5 | 弹窗里的按钮不再遮住正文（协议全文能完整看到） | `src/Dialog.vue`、`src/App.vue` |
| 6 | 卸载后不再残留文件（`%VAR%` 展开、所有登录用户、`%TEMP%`、先结束运行中的主程序） | `src-tauri/src/installer/uninstall.rs`、`src/App.vue` |

---

## 1. 卸载器：额外注册表清理（`extraUninstallRegistry`）

上游卸载器只做三件事：删文件、删 `extraUninstallPath` / `userDataPath` 目录、
删 ARP 卸载项（`...\Uninstall\{regName}`，HKLM + HKCU）。它**不知道**宿主自己写过
哪些注册表——本项目宿主的开机自启（`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
下的 `GenshinFpsUnlocker` 值，见 `src/Host/Autostart.cs`）就会残留。

### `src-tauri/src/installer/uninstall.rs`

- 新增 `pub struct RegistryCleanupItem { hive, key, value? }`（serde，`value` 带 `#[serde(default)]`）。
- `RunUninstallArgs` 新增字段 `#[serde(default)] extra_uninstall_registry: Vec<RegistryCleanupItem>`
  ——旧版前端不传也能反序列化，向后兼容。
- `run_uninstall(...)` 新增同名参数（只有 `run_uninstall_with_args` 一个调用点；
  该函数**没有**注册进 `invoke_handler`，所以不存在 JS 侧参数不匹配问题）。
- 新增 `pub fn clean_extra_registry(&[RegistryCleanupItem])` 与两个私有辅助
  `apply_registry_cleanup` / `apply_registry_cleanup_for_all_users`，
  在 `clear_empty_dirs(source)` 之后、删 ARP 项之前调用。
- 语义：
  - `hive` 支持 `HKCU` / `HKLM` / `HKCR` / `HKU`（大小写不敏感，也认全称）；
  - 给了 `value` → 只删该键下的这个值；没给 → `remove_tree` 递归删整个子键；
  - `HKCU` 额外遍历 `HKEY_USERS` 下已加载的用户配置单元。**原因**：卸载器通常以
    管理员身份运行，此时 `HKCU` 指向管理员账户，而自启动是登录用户装的，只删
    `HKCU` 会漏。跳过 `*_Classes`、`.DEFAULT`、`S-1-5-18`；
  - 所有错误一律忽略（仅 `tracing` 记日志），卸载不因某个键不存在/无权限而失败。
- 依赖：只用已有的 `windows-registry 0.5`（`USERS` / `CURRENT_USER` / `LOCAL_MACHINE`
  / `CLASSES_ROOT`、`Key::open` / `remove_value` / `remove_tree` / `keys()`）。
  注意 `keys()` 返回借用迭代器，必须先 `let users_root = windows_registry::USERS;`
  再调用，否则借用临时值编译不过。

### `src/App.vue`

卸载分支构造 `ipcRunUninstall({...})` 时新增一行：

```ts
extra_uninstall_registry: PROJECT_CONFIG.extraUninstallRegistry ?? [],
```

### `src/types.ts` / `src/api/ipc.ts`

- `ProjectConfig` 新增可选字段 `extraUninstallRegistry?: RegistryCleanupItem[]`，
  并导出 `RegistryCleanupItem` 类型；
- `IpcRunUninstall` 新增可选字段 `extra_uninstall_registry?: RegistryCleanupItem[]`。

### 本项目配置

`installer/kachina.config.json`：

```json
"extraUninstallRegistry": [
  { "hive": "HKCU", "key": "Software\\Microsoft\\Windows\\CurrentVersion\\Run", "value": "GenshinFpsUnlocker" }
]
```

---

## 1b. 卸载器：清理宿主自建/改名的快捷方式（`extraUninstallLnkNames`）

上游卸载器只删自己建的两个快捷方式：`<桌面>\{appName}.lnk` 与整个
`<开始菜单>\{appName}\` 文件夹，且「桌面 / 开始菜单」按 `needElevate` 二选一
（公共桌面 or 用户桌面）。本项目宿主会把桌面快捷方式**改名成中文显示名**
`原神帧率解锁.lnk`（`src/Host/ShortcutHelper.cs`，还会删掉英文名那份），
于是卸载后桌面会留下一个指向已删除 exe 的死图标。

### `src-tauri/src/installer/uninstall.rs`

- 新增 `async fn rm_best_effort(paths: &[String])`：不存在跳过、文件用
  `remove_file`、目录用 `remove_dir_all`，**失败只 `tracing` 记日志**。
  在 `run_uninstall` 里于严格的 `user_data_path` / `extra_uninstall_path`
  循环**之前**调用。
- `RunUninstallArgs` / `run_uninstall` 新增
  `#[serde(default)] extra_uninstall_shortcuts: Vec<String>`。

> 为什么不直接塞进上游的 `extra_uninstall_path`：那条路径删不掉会
> `? ` 上抛（`RM_USERDATA_ERR`），把整个卸载判为失败。快捷方式残留属于
> 「清理不干净」，不该升级成「卸载失败」，所以单独走尽力删除。

### `src/App.vue`

新增 `async function getExtraUninstallShortcutPaths(): Promise<string[]>`：
把配置里的**文件名**拼到 shell API 解析出的真实目录上，
`get_dirs(true)` 与 `get_dirs(false)` 各调一次，覆盖

- 公共桌面 `FOLDERID_PublicDesktop` 与用户桌面 `FOLDERID_Desktop`
  （用户桌面可能被 OneDrive 重定向，**不能**用 `%USERPROFILE%\Desktop` 拼）；
- 公共开始菜单 `FOLDERID_CommonPrograms` 与用户开始菜单 `FOLDERID_Programs`
  下的 `{appName}\` 产品文件夹（含里面的同名 .lnk）。

卸载分支里 `extra_uninstall_shortcuts: extraShortcuts`。

### `src/types.ts` / `src/api/ipc.ts`

`ProjectConfig.extraUninstallLnkNames?: string[]`；
`IpcRunUninstall.extra_uninstall_shortcuts?: string[]`。

### 本项目配置

```json
"extraUninstallLnkNames": [
  "原神帧率解锁.lnk",
  "GenshinFpsUnlocker.lnk",
  "GenshinFpsUnlocker.exe.lnk",
  "Genshin FPS Unlocker.lnk"
]
```

与 `ShortcutHelper.ShortcutNameAliases()` 曾经列举的历史别名一致
（那个方法和 `RemoveCreatedShortcuts()` 已随「宿主不做卸载」一并删除）。

---

## 2. 用户协议：可配置 + 多格式 + 弹窗全文

上游安装界面的「用户协议」是一个**没有 `href`、没有点击处理**的死链接
（`<a> 用户协议 </a>`），也没有任何协议正文来源。

### `src-tauri/src/builder/pack.rs`（打包期内联）

新增 `fn resolve_agreement(config: &mut serde_json::Value, config_path: &Path)`，
在 `pack_cli` 解析完配置 JSON 后立刻调用，把

| 配置项 | 含义 | 默认 |
| --- | --- | --- |
| `agreementFile` | 协议文件路径，**相对于配置文件所在目录** | 无（不写 `agreement`） |
| `agreementFormat` | `text` / `markdown`（`md`）/ `html` | `text` |
| `agreementTitle` | 链接文字与弹窗标题 | `用户协议` |

内联成 `agreement: { title, format, content }` 并删掉这三个源字段。
读文件失败只打印 warning，不中断打包（此时链接退化为纯文字）。
文本按 UTF-8 lossy 读取，容忍 BOM，CRLF 归一为 LF。

> 走「打包期内联」而不是「运行时读文件」，是因为安装器/卸载器/更新器都是
> 单文件 exe，运行期没有仓库上下文；内联后离线安装器、`update.exe`、
> `uninst.exe` 三者共用同一份协议正文。

### `src/utils/agreement.ts`（新增文件）

- `escapeHtml` / `renderInline` / `renderMarkdown`：极简 Markdown 子集渲染
  （标题 `#`→`h2` 起、段落、有序/无序列表、引用、分隔线、围栏代码块、
  行内 code/粗体/斜体/删除线/http(s) 链接），**不引入新依赖**；
- `hasAgreementContent(agreement)`：有无正文；
- `renderAgreement(agreement)`：按 `format` 渲染，`text` → `<div class="agreement-plain">`
  （CSS `white-space: pre-wrap` 保留手工换行），`markdown` → 上面的渲染器，
  `html` → 原样，三者**统一再过一遍 `DOMPurify`**（已是上游依赖）后交给 `v-html`。

### `src/App.vue`

- `dialog` ref 类型加 `'agreement'`；
- 协议链接改为 `@click="openAgreement"`，文字取 `agreementTitle`，
  有正文时加下划线样式（`.agreement-link`），没有则退化为纯文字
  （`.agreement-link-off`，`cursor: default`）；
- 新增一个 `<Dialog v-show="dialog === 'agreement'">`：标题 / 说明 /
  `<div class="agreement-body" v-html="agreementHtml">` / 页脚两个按钮
  （「关闭」与「我已阅读并同意」，后者顺手勾上 `acceptEula`）；
- 新增 `agreementTitle` / `hasAgreement` / `agreementHtml` 三个 computed
  与 `openAgreement()` / `closeAgreement(accepted)` 两个函数；
- scoped 样式新增 `.agreement-body`（高度由弹窗骨架的 flex 分配 + `overflow-y: auto`，
  见第 5 节）及其 `:deep()` 子元素样式。选择器都以 `.agreement-body[data-v-*]` 开头，
  因此不会被 `rsbuild.config.ts` 里 PurgeCSS 的 `safelist: [/^(?!h[1-6]).*$/]` 清掉。

### `src/types.ts`

导出 `AgreementFormat` / `AgreementConfig`，`ProjectConfig` 新增可选 `agreement?`。

### 本项目配置

`installer/kachina.config.json` 指向仓库根的 `USER_AGREEMENT.txt`：

```json
"agreementFile": "../USER_AGREEMENT.txt",
"agreementFormat": "text",
"agreementTitle": "用户协议"
```

`pack.ps1` 传给 builder 的是 `installer\kachina.config.json` 的绝对路径，
所以 `../USER_AGREEMENT.txt` 稳定解析到仓库根，与工作目录无关。

---

## 3. 安全加固：收敛卸载器的删除范围与提权面

上游把「删什么」完全交给打包配置，而卸载器通常以管理员身份运行
（本项目 `uacStrategy: "prefer-admin"`）。配置里一个笔误、或被人动过的安装目录 /
注册表项，都会被管理员权限放大。本项目在**不改变上游既有语义**的前提下加了几道
安全阀：命中即「拒绝 + 记日志」，绝不让卸载因此失败。

### `src-tauri/src/installer/uninstall.rs`

| 通道 | 上游行为 | 加固后 |
| --- | --- | --- |
| `extraUninstallRegistry` 删值 | 直接 `remove_value` | `is_safe_registry_target`：子键至少两级，不碰任何根键的直属项 |
| 同上，删整棵子键 | 直接 `remove_tree` | 至少三级，且末级不在 `REG_TREE_DENY_LEAVES`（`Run` / `RunOnce` / `Uninstall` / `Policies` / `Explorer` / `Winlogon` / `Environment` / `Classes` / `Software` / `Microsoft` / `Windows` / `CurrentVersion` / `System` / `Services` / `Session Manager` / `IFEO` 等共享容器） |
| `value` 写成空字符串 | 等价于「删整棵子键」 | 视为配置错误，整条跳过并告警（真要删子键必须显式省略 `value`） |
| 提权卸载时清 HKCU | 只清管理员自己的 hive | 额外遍历 `HKEY_USERS` 下已加载的 SID（跳过 `.DEFAULT` / `S-1-5-18` / `*_Classes`）逐个清，避免漏掉发起卸载的登录用户 |
| `rm_best_effort`（快捷方式） | 前端/配置给什么删什么，目录走 `remove_dir_all` | `is_safe_shortcut_target`：绝对路径、不含 `..`、路径与**所有父级**都不是符号链接 / junction、不落在 `%SystemRoot%` 内；目录只放行 `开始菜单\Programs\<产品名>`，文件只放行 `.lnk` 且位于某个 `Desktop\` 下或 `Programs\<产品名>\` 内。`allowed_names` = `regName` + 安装目录名 |
| `userDataPath` / `extraUninstallPath` | 直接 `remove_file` / `remove_dir_all` | `is_safe_delete_target`：上述形状校验 + 至少两级目录 + 不等于任何受保护根目录本身（盘符根、`%SystemRoot%`、`%ProgramFiles%`、`%ProgramFiles(x86)%`、`%ProgramData%`、`%USERPROFILE%`、`%APPDATA%`、`%LOCALAPPDATA%`、`%PUBLIC%`、`%TEMP%` / `%TMP%`）。允许删这些目录**下面**的产品子目录，不允许删它们自己 |

被拒绝的路径统一 `tracing::warn!("跳过不安全的…")` 后继续，卸载流程不中断。
本项目的真实配置（`extraUninstallRegistry` 删 `HKCU\...\Run` 下的
`GenshinFpsUnlocker` 值、`userDataPath` = `%LOCALAPPDATA%/GenshinFpsUnlocker`、
`extraUninstallLnkNames` 4 个名字）全部落在放行范围内，功能不受影响。

### `src/utils/agreement.ts` + `src/App.vue`（协议正文的注入面）

协议正文来自打包配置，最终 `v-html` 进一个**能调用提权 IPC 的 WebView**，
所以按「白名单排版 + 禁掉一切可执行 / 可提交 / 可外链内容」收紧：

- DOMPurify：`FORBID_TAGS` 增加 `style / form / input / button / select / textarea /
  iframe / object / embed / link / meta / base / svg / math`；`FORBID_ATTR` 增加
  `style / srcdoc / formaction / data / background`；`ALLOWED_URI_REGEXP` 收窄为
  `^(?:https?:|mailto:|#)`（上游默认还放行 `tel:` / `callto:` / `cid:` / `xmpp:` 等）。
- 正文里的 `<a>` 点击一律 `preventDefault`：安装器窗口被导航走 = 安装 / 卸载流程直接断掉。
  `http(s)` 外链交给 `invoke('launch')` 用系统浏览器打开（与上游「获取 CDK」同一个命令），
  页内锚点与万一漏网的 `javascript:` 之类什么都不做。
- 内联了协议正文时把 `acceptEula` 初始化为 `false`：必须勾「我已阅读并同意」才能点安装。
  未内联协议时保持上游默认（视为已同意），不额外增加交互；`silent` / `non_interactive`
  安装走 `onMounted` 末尾的 `install()`，不受勾选影响；卸载界面用的是另一个按钮，
  同样不受影响。
- 协议正文容器补 `user-select: text`（`.content` 全局是 `user-select: none`），
  法律文本要能选中复制。

### 宿主侧（`src/Host/`，不属于本目录，列在这里便于对照）

- `UninstallLauncher.IsTrustworthyUninstaller`：宿主里的「卸载本软件」只负责启动
  `<安装目录>\GenshinFpsUnlocker.uninst.exe`，启动前校验：路径仍在自身目录内、
  文件名符合约定、非空文件、自身与所在目录都不是符号链接 / junction、目录不是
  盘符根 / 系统目录 / 用户配置目录；**且宿主已提权时要求安装目录位于 `Program Files` 下**
  —— 否则普通用户可以在可写目录里放一个同名 exe，借宿主的管理员令牌执行任意代码
  （典型 EoP），这种情况直接拒绝并提示改用「设置 → 应用」卸载。
  Web UI 的 `uninstall` 消息不接受任何参数，路径全部由宿主自己算，前端无法指定。
- `RuntimePrerequisite.ResolveDotNetCli`：检测 .NET 桌面运行时不再用裸命令名 `dotnet`
  （那会按 PATH 搜索），优先 `%ProgramFiles%\dotnet\dotnet.exe`，避免提权进程被 PATH 劫持。

---

## 4. 依赖：`rcedit` 从 git 依赖改为仓库内 vendored 副本

上游 kachina 的 `src-tauri/Cargo.toml` 里写的是：

```toml
rcedit = { version = "0.1.0", git = "https://github.com/Devolutions/rcedit-rs.git" }
```

这个依赖带 C++（`rcedit-sys` 的 `rescle.cc` / `librcedit.cpp`，由 `build.rs` 经 `cc` 调 MSVC 编），
其中 `rescle.cc:87` 用了 MSVC 的非标准扩展 `std::locale::empty()`：VS 2022 17.14 起弃用，
**MSVC 14.51 起移除**（microsoft/STL#5834，现在 `<xlocale>` 里那句声明只在 `#ifdef _CRTBLD`
下存在，没有开关能打开）。`windows-latest` runner 已经是 VS 2026 / MSVC 14.51.36231，
于是 `build-kachina` 必然失败：

```
rescle.cc(87): error C2039: 'empty': is not a member of 'std::locale'
error: failed to run custom build command for `rcedit-sys v0.1.0 (https://github.com/Devolutions/rcedit-rs.git#1bfa3ee6)`
```

上游最新提交（2025-10-29）没修，等不来；本项目又要求 CI 只从仓库内构建，
所以把 `rcedit-rs@1bfa3ee6` vendor 到 `vendor/rcedit-rs/` 并改掉那一行。

### 本目录内的改动

- `src-tauri/Cargo.toml`：`rcedit` 依赖改为 `path = "../vendor/rcedit-rs"`（原 git 行以注释保留）。
- `src-tauri/Cargo.lock`：`rcedit` / `rcedit-sys` 两个包去掉 `source = "git+..."` 行
  （path 依赖不写 source），版本与依赖列表不变；已用 `cargo metadata --locked` 验证一致。
- `vendor/rcedit-rs/`：新增，含上游两份 LICENSE、10 个源文件与 `LOCAL_PATCHES.md`
  （详细记录改了哪两处、为什么、怎么升级）。

### 自动断言

- `pwsh tools/devcheck/devcheck.ps1 -Layer vendor`：副本 10 个文件齐全、`rescle.cc` 里没有
  `locale::empty(`、`rcedit` 依赖是 path 形式、`Cargo.lock` 里不再出现该 git 源。
- `pwsh tools/devcheck/devcheck.ps1 -Layer native`：在有 `cl.exe` 的机器上（CI 的 windows job）
  真编一遍 `rcedit-sys`，让这类「工具链 vs vendored C++」的破坏在**自动**工作流里就暴露，
  不必等手动触发 Build 跑 6 分钟。

---

## 5. 弹窗布局：footer 按钮回到文档流

上游的 `.btn-install` 是给**主界面右下角**设计的：`position: absolute; bottom: 20px;
right: 8px`（次要按钮 `.btn-install-2rd` 再往左挪 150px）。三个弹窗的 footer 里
复用了同一个类，于是按钮脱离文档流、浮在 `.dialog-body` 上面。主界面没事（正文短），
协议弹窗就露馅了：

- 安装窗口只有 **520 × 250** 逻辑像素（`src-tauri/src/main.rs` 的 `base_width` /
  `base_height` × 系统文字缩放），`.dialog` 撑满后约 488 × 246；
- 协议正文原先写死 `max-height: 46vh`（≈115px）+ 内边距/边框/外边距 ≈ 149px，
  加上标题（25px 字）与说明文字，文档流走到约 210px；
- 而两个按钮占 190~230px 这一段 —— **正好压住正文最后一两行**；
  按钮本身 140×40 / 100×40，在 488px 宽的弹窗里也偏大。

### 改法

| 文件 | 改动 |
| --- | --- |
| `src/Dialog.vue` | `.dialog` 改纵向 flex（`overflow: hidden`）；新增 `.dialog-body { flex: 1 1 auto; min-height: 0 }`（自己也是 flex column）与 `.dialog-footer { flex: 0 0 auto; display: flex; justify-content: flex-end; gap: 8px; padding: 6px 8px 10px }` |
| `src/App.vue` | 新增 `.dialog-footer .btn-install`（含 `.btn-install-2rd`）覆盖：`position: static; height: 28px; width: auto; min-width: 72px; padding: 0 14px; font-size: 12.5px`；`.agreement-body` 去掉 `max-height: 46vh`，改 `flex: 1 1 auto; min-height: 0` |

footer 回到文档流、正文用 flex 吃剩余高度之后，**两者在结构上不可能重叠**，
窗口按系统文字缩放放大缩小都成立。正文可见区域也从「115px 里被按钮盖掉约 20px」
变成完整的约 110px（`.agreement-body` 是 content-box，115px 只是内容高度，
外头还有 20px 内边距 + 2px 边框 + 12px 外边距）。

> 两条样式必须分别写在 `Dialog.vue` 和 `App.vue`：Vue 的 scoped CSS 里，
> slot 内容带的是**父组件**（App.vue）的 scope id，子组件（Dialog.vue）选择不到
> `.dialog-footer .btn-install`；反过来 `.dialog-footer` 这个元素属于 Dialog.vue，
> App.vue 也只能靠后代选择器命中它。

### 影响面

三个弹窗（`source` / `mirrorc` / `agreement`）的 footer 都变成流内右对齐，
按钮略小、略低（原先底边距 20px，现在 10px），视觉位置基本不变；
主界面的 6 个 `.btn-install` 不在 `.dialog-footer` 里，绝对定位保持原样。

---

## 6. 卸载残留清理：`%VAR%` 展开、所有登录用户、`%TEMP%`、运行中的进程

上游卸载器只删「配置里写的那几条路径」，有四个洞会让文件在卸载后仍然残留。
其中第 1 个洞在本项目是**必然**发生的，不是边缘情况：

| # | 洞 | 现象 |
| --- | --- | --- |
| 1 | `%VAR%` 形式的路径**从不展开** | 前端 `replacePathEnvirables` 只认 `${INSTALL_PATH}` / `${APP_NAME}` 两种写法，配置里的 `%LOCALAPPDATA%/GenshinFpsUnlocker` 原样传进 Rust；`is_safe_delete_target` 又要求绝对路径，于是这条被当成「不安全路径」**静默跳过**——勾了「同时删除用户数据」也一个字节都不会删 |
| 2 | 只清理**当前进程**的用户目录 | 卸载器一般以管理员身份运行，`%LOCALAPPDATA%` 指向管理员账户；当初装软件的普通用户那份数据（连同该用户桌面上的 `.lnk`、开始菜单文件夹）全部留在原地 |
| 3 | 安装 / 卸载过程写进 `%TEMP%` 的文件没人管 | 运行时安装包（几十 MB）、`KachinaInstaller.log`、WebView2 引导器、卸载器自己的临时副本，失败时全留在 `%TEMP%` 里 |
| 4 | 卸载流程**不结束正在运行的主程序** | 上游只在安装流程 `installPrepare` 里做「检测 → 询问 → 结束进程」；从「设置 → 应用」/ 开始菜单发起卸载时主程序还常驻托盘，它的 exe、`logs\`、WebView2 的 `EBWebView` 缓存全被占用，删不掉 → 残留 |

### `src-tauri/src/installer/uninstall.rs`

- `fn expand_env_vars(input: &str) -> String`：用
  `windows::Win32::System::Environment::ExpandEnvironmentStringsW` 展开 `%VAR%`
  （Windows 原生的子串语法 `%VAR:~a,b%` 也一并支持）。展开失败、结果为空或
  不是绝对路径时**原样返回**，交给后面的安全阀拒绝，不做任何猜测。
- `fn expand_path_list(paths: &[String]) -> Vec<String>`：逐项展开 + 大小写不敏感去重。
  在 `run_uninstall` 里对 `to_be_delete`（`userDataPath` + `extraUninstallPath`）
  与 `extra_uninstall_shortcuts` 各调一次，位置**必须在 `is_safe_delete_target` 之前**——
  顺序反了就等于洞 1 没修。
- 多用户清理（洞 2），四个函数串成一条流水线：
  - `const PER_USER_CLEANUP_ROOTS: &[&str] = &["AppData", "Documents", "Desktop"]`；
  - `fn profile_relative_tail(path: &Path) -> Option<PathBuf>`：按 `%USERPROFILE%`
    `strip_prefix` 出「相对用户配置目录的尾巴」。`..`（`ParentDir`）直接拒绝；
    第一段必须命中 `PER_USER_CLEANUP_ROOTS`（不区分大小写）；至少两级
    （不接受直接挂在配置目录下的东西）；`Desktop` 下只放行 `.lnk`
    （别人桌面上的文档一概不碰）。返回 `None` 表示「这条路径与哪个用户无关」
    （公共开始菜单、安装目录本身），本来就已经被上游逻辑处理了；
  - `fn loaded_profile_roots() -> Vec<PathBuf>`：枚举
    `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList` 下的 SID，
    读 `ProfileImagePath` 后再 `expand_env_vars` 一次，跳过 `*_Classes` / `.DEFAULT` /
    `S-1-5-18`，只保留绝对路径，用上游已有的 `path_eq` 去重。打不开的配置单元
    （未加载的用户）静默跳过；
  - `fn collect_all_users_cleanup_targets(paths: &[String]) -> Vec<PathBuf>`：把尾巴
    重放到每个用户目录上，逐个过 `is_safe_delete_target`，然后要求**是目录，
    或者是 `.lnk` 文件**（数据目录里可能有 WebView2 缓存等任意内容，所以目录不限；
    文件只认快捷方式），再去重；
  - `async fn clean_per_user_leftovers(paths: &[String])`：对上面的候选做
    `remove_dir_all` / `remove_file`，**成功 `info`、失败 `warn`，绝不上抛**。
    在 `run_uninstall` 里于删完当前用户数据之后、`clear_empty_dirs` 之前调用。
    **勾选语义自动跟随**：传进去的就是 `to_be_delete`（= `user_data_path` +
    `extra_uninstall_path`），而前端只在勾了「同时删除用户数据」时才把
    `userDataPath` 填进 `user_data_path`，没勾就是空数组，所以数据目录天然不会被
    跨用户删除，不需要额外开关。`extra_uninstall_path` 不受勾选影响（里面是开始菜单
    的 `{appName}\` 文件夹与桌面 `.lnk`），因此**其它用户桌面上指向已删除 exe 的
    死图标、开始菜单里的死文件夹，即使用户选择保留数据也会被清掉** —— 这正是想要的。
- `%TEMP%` 清理（洞 3）：`fn is_installer_temp_artifact(name: &str) -> bool`
  + `async fn clean_installer_temp_files(skip: Option<&str>)`。白名单是四个**固定形状**
  的文件名（全部小写比较），不按「含 kachina 就删」这种模糊规则：

  | 文件名 | 来源 |
  | --- | --- |
  | `KachinaInstaller.log` | 安装 / 卸载日志，一直追加，从来没人删 |
  | `Kachina.RuntimePackage.<tag>.exe` | .NET / VCRedist 运行时安装包（几十 MB；安装成功会删，中途 `return Err` 就留下） |
  | `kachina.MicrosoftEdgeWebview2Setup.exe` | WebView2 引导安装器 |
  | `kachina.uninst.<时间戳>.exe` | 卸载器把自己挪到临时目录后的副本（正常由 `delete_self_on_exit` 删，失败时留下） |

  扫描目录是 `std::env::temp_dir()`；`skip` 传正在运行的卸载器自身路径
  （`DELETE_SELF_ON_EXIT_PATH`，删不掉也不该删）；**只删文件不删目录、不递归**，
  失败只 `warn`。注意这些名字是所有 Kachina 打包的产品共用的，但正在被别的安装器
  使用的文件本身删不掉（占用），只会留下一条日志。

> 为什么 `expand_env_vars` 放在 Rust 而不是去修前端：卸载器提权后，前端所在进程的
> 环境变量指向的是**发起卸载的用户**，而 `%LOCALAPPDATA%` 在提权进程里是管理员的；
> 展开必须发生在「真正要删的那一刻、那个进程里」。另外 `uninst.exe` 是打包时把配置
> 内联进去的单文件，前端改 `replacePathEnvirables` 也覆盖不到它。

### `src/App.vue`

- **卸载前结束正在运行的主程序**（洞 4）：新增
  `async function killRunningAppForUninstall(): Promise<boolean>`，在 `uninstall()`
  开头（`step = 5` 之后、读卸载元数据之前）调用。逻辑照搬上游 `installPrepare`
  里的那一段：`ipcFindProcessByName(exeName)` → 有则询问
  「检测到…正在运行。不结束进程的话，程序文件与用户数据（配置、日志、界面缓存）
  会因为被占用而删不掉，卸载后会留下残留。是否结束进程并继续卸载？」→
  `ipcKillProcess`（先按 `needElevate`，失败再按管理员重试）。
  `silent` / `non_interactive` 不询问直接结束；**用户拒绝则回到卸载界面
  （`step = 1`）不执行卸载**；结束进程失败只 `warn` 后继续（后面的删除都是尽力而为）。
  结束后 `setTimeout` 等 1 秒再删，因为 WebView2 的缓存文件在进程退出后仍会被
  短暂占用。
- 卸载勾选框文案从「同时删除用户数据」改成
  「同时删除用户数据（配置、日志与界面缓存）」，并加 `title` 悬浮说明
  （删的是哪几样、其它账户的同名目录也会一并清、不勾选则保留便于重装）。

### `src/App.vue`

只改文案，不涉及逻辑：卸载勾选框从「同时删除用户数据」改为
「同时删除用户数据（配置、日志与界面缓存）」，并加 `title` 悬浮说明
（包含本机所有已登录用户的数据目录与安装期临时文件；不勾选则保留，便于重装后沿用设置）。

### 本项目配置（`installer/kachina.config.json`，不在本目录内）

`userDataPath` 从 1 项扩到 3 项，覆盖历史版本可能用过的落盘位置：
`%LOCALAPPDATA%`、`%APPDATA%`、`%USERPROFILE%/Documents` 下各一个
`GenshinFpsUnlocker`。三项都走同一套安全阀，并被多用户重放覆盖。

### 已知仍不覆盖

- **OneDrive 重定向**过的 `Documents` / `AppData`：ProfileList 里的
  `ProfileImagePath` 是真实用户目录，重定向后的实际位置不在其中；这类目录只能靠
  `%VAR%` 展开命中当前进程用户那一份；
- 凭据管理器条目（本项目不写凭据）。

> WebView2 的 `EBWebView` 目录**是**覆盖到的：宿主用
> `CoreWebView2Environment.CreateAsync(userDataFolder: dataDir)` 把它建在数据目录里面
> （`src/Host/MainForm.Web.cs`），随 `remove_dir_all` 一起删；前提是主程序已退出，
> 所以才有上面 `killRunningAppForUninstall` 那一步。

---

## 升级上游时的套用顺序

1. 按 `UPSTREAM.md` 覆盖整个目录；
2. 恢复本文件（`LOCAL_PATCHES.md`）与 `UPSTREAM.md`；
3. 依次套用上面的改动：`uninstall.rs`（注册表清理 + `rm_best_effort` + 第 3 节的
   全部安全阀 + 第 6 节的 `%VAR%` 展开 / 多用户清理 / `%TEMP%` 白名单）→ `pack.rs`
   → `types.ts` → `api/ipc.ts` → `utils/agreement.ts`（整份新增，含 DOMPurify 收紧策略）
   → `App.vue`（协议弹窗 4 处 + 快捷方式清理 2 处 + 链接点击拦截 + `acceptEula`
   初始化 + 第 5 节的两处样式 + 第 6 节的 `killRunningAppForUninstall` 与勾选框文案）
   → `Dialog.vue`（第 5 节的 flex 骨架）；
4. `npx tsc --noEmit -p tsconfig.json`（上游本身有 3 个 `noUnusedLocals` 报错，
   只要没有新增报错即可）+ 用 `@vue/compiler-sfc` 编译 `src/App.vue` 自检；
5. Windows 上 `pnpm build` 出 `kachina-builder.exe`，跑一次
   `installer\pack.ps1`，确认：安装界面能弹出协议全文；卸载后
   `HKCU\...\Run` 里的 `GenshinFpsUnlocker` 值消失；桌面上的
   `原神帧率解锁.lnk` 与开始菜单文件夹一并消失；勾选「同时删除用户数据」后
   **每一个**登录过的用户账户下的 `%LocalAppData%\GenshinFpsUnlocker` 都消失
   （以管理员身份从普通用户装的副本上卸载时尤其要验这一条，即洞 2）；
   主程序在托盘里运行时发起卸载，会先弹「是否结束进程」的询问，结束后
   `EBWebView` 缓存与 `logs\` 也一并删掉（洞 4）。

> 上述 Rust 逻辑（`clean_extra_registry` / `rm_best_effort` / `is_safe_registry_target` /
> `is_safe_shortcut_target` / `is_safe_delete_target` / `resolve_agreement`，以及第 6 节的
> `expand_env_vars` / `expand_path_list` / `profile_relative_tail` / `loaded_profile_roots` /
> `collect_all_users_cleanup_targets` / `is_installer_temp_artifact`）
> 已在 Linux 上用 mock 版 `windows-registry` + 真实 `serde_json` / `tokio` 逐条跑过
> **93 个断言**（含提权卸载遍历 `HKEY_USERS`、`value` 为空、共享容器键、符号链接 /
> 系统目录 / 路径穿越 / 受保护根目录、协议 BOM/CRLF 与文件缺失、用户目录尾巴的
> 形状与 `Desktop` 白名单、多用户重放、`%TEMP%` 条目的命中与放行边界），其中
> `resolve_agreement` 是拿仓库里真实的 `installer/kachina.config.json` +
> `USER_AGREEMENT.txt` 跑的；Windows 专有 API（重解析点属性、`%SystemRoot%`、
> `ExpandEnvironmentStringsW`、ProfileList）在 harness 里用桩替代；
> `clean_installer_temp_files` / `clean_per_user_leftovers` 是纯 IO 包装，只断言其
> 判定函数（`is_installer_temp_artifact` / `profile_relative_tail`）。
> 前端侧的 `killRunningAppForUninstall` 只有 `tsc --strict` + SFC 编译 + prettier 把关。
> 整套逻辑**没有**在 Windows 上实机验证过。

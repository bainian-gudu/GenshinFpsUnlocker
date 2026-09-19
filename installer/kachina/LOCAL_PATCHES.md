# kachina-installer 本地修改清单

本文档记录 `installer/kachina` 相对上游快照的全部改动、原因和复核方式。它服务于两类
场景：审查当前副本，以及升级上游后按顺序重新套用修改。代码行为以仓库中的实现和
`tools/devcheck` 检查结果为准；本文档中的路径、配置键和命令均保持原样，便于搜索。

上游快照：tag `0.5.1` / commit `ae461aa9ddd5a8f5e445459e14f5d611921f9938`（见 `UPSTREAM.md`）。

本目录**不是纯净快照**：本地修改包括新增协议渲染、临时文件安全处理、DFS 会话模块和
`vendor/rcedit-rs/` 副本，以及删除 `src-tauri/src/utils/sentry.rs` 等遥测实现。
升级时应逐项复核本清单，不能只覆盖上游源码。特别是第 7 节的遥测移除，需要同时检查
源代码、依赖声明和两个锁文件。

## 目录

- [1. 卸载器注册表清理](#1-卸载器额外注册表清理-extrauninstallregistry)
- [1b. 卸载器快捷方式清理](#1b-卸载器清理宿主自建改名的快捷方式-extrauninstalllnknames)
- [1c. 卸载器计划任务清理](#1c-卸载器清理安装期登记的登录计划任务-extrauninstallscheduledtasks)
- [2. 用户协议](#2-用户协议可配置--多格式--弹窗全文)
- [3. 安全加固](#3-安全加固收敛卸载器的删除范围与提权面)
- [4. rcedit 本地副本](#4-依赖rcedit-从-git-依赖改为仓库内副本)
- [5. 弹窗布局](#5-弹窗布局footer-按钮回到文档流)
- [6. 卸载残留清理](#6-卸载残留清理var-展开所有登录用户temp运行中的进程)
- [7. 遥测移除](#7-遥测sentry-错误上报与使用统计已物理移除)
- [8. 后续安全加固](#8-第二轮安全加固卸载收尾路径比较提权管道arp-卸载入口)
- [9. 下载与提权链路加固](#9-第三轮安全加固把下载后执行和提权管道两条链路一次收干净)
- [10. 前端模块拆分与注释中文化](#10-前端模块拆分与注释语言统一)
- [11. 构建日志告警收敛](#11-构建日志告警收敛)
- [升级上游时的套用顺序](#升级上游时的套用顺序)

| # | 需求 | 涉及文件 |
| --- | --- | --- |
| 1 | 卸载时清理安装期写入的注册表（开机自启动等） | `src-tauri/src/installer/uninstall.rs`、`src/App.vue`、`src/types.ts`、`src/api/ipc.ts` |
| 1b | 卸载时清理安装期由宿主自建/改名的快捷方式 | 同上 4 个文件 |
| 1c | 卸载时清理安装期登记的登录计划任务（开机自启 + 自动管理员） | 同上 4 个文件 |
| 2 | 用户协议可配置、多格式、点击弹窗看全文 | `src-tauri/src/builder/pack.rs`、`src/App.vue`、`src/types.ts`、`src/utils/agreement.ts`（新增） |
| 3 | 安全加固：收敛卸载器的删除范围与提权面 | `src-tauri/src/installer/uninstall.rs`、`src/utils/agreement.ts`、`src/App.vue`（另有宿主侧 `src/Host/UninstallLauncher.cs`、`src/Host/RuntimePrerequisite.cs`，不属于本目录） |
| 4 | 让 kachina 在 MSVC 14.51（VS 2026 / windows-latest）上还能编过 | `src-tauri/Cargo.toml`、`src-tauri/Cargo.lock`、`vendor/rcedit-rs/`（新增的仓库内依赖 + 1 行 C++ 修复） |
| 5 | 弹窗里的按钮不再遮住正文（协议全文能完整看到） | `src/Dialog.vue`、`src/App.vue` |
| 6 | 卸载后不再残留文件（`%VAR%` 展开、所有登录用户、`%TEMP%`、先结束运行中的主程序） | `src-tauri/src/installer/uninstall.rs`、`src/App.vue` |
| 7 | **移除全部遥测**：Sentry 错误上报 + `77.cocogoat.cn` 使用统计（连依赖一起删） | `src-tauri/Cargo.toml`、`src-tauri/Cargo.lock`、`src-tauri/src/utils/sentry.rs`（删除）、`src-tauri/src/utils/mod.rs`、`src-tauri/src/utils/error.rs`、`src-tauri/src/main.rs`、`src-tauri/src/ipc/manager.rs`、`src-tauri/src/ipc/operation.rs`、`src-tauri/src/installer/config.rs`、`src/api/ipc.ts`、`src/App.vue`、`package.json`、`pnpm-lock.yaml`、`pnpm-workspace.yaml` |
| 8 | 卸载收尾、路径比较、提权状态与静默卸载入口 | `src-tauri/src/installer/uninstall.rs`、`src-tauri/src/ipc/manager.rs`、`src-tauri/src/installer/registry.rs`、`src/App.vue` |
| 9 | 下载文件验签、临时文件、提权管道及后续复查修复 | `src-tauri/src/utils/secure_temp.rs`、`src-tauri/src/utils/acl.rs` 及相关调用点，详见第 9 节 |
| 10 | DFS 会话模块拆分与注释中文化 | `src/dfs.ts`、`src/dfs/session.ts`；注释调整覆盖本目录项目源码和仓库内副本的功能注释 |
| 12 | 品牌改名后的旧主程序 / 安装目录识别、旧组件清理与快捷方式修复 | `src-tauri/src/installer/config.rs`、`src-tauri/src/installer/mod.rs`、`src/App.vue` |

---

## 1. 卸载器：额外注册表清理（`extraUninstallRegistry`）

上游卸载器只做三件事：删文件、删 `extraUninstallPath` / `userDataPath` 目录、
删 ARP 卸载项（`...\Uninstall\{regName}`，HKLM + HKCU）。它**不知道**宿主自己写过
哪些注册表——本项目宿主的开机自启（`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
下的历史兼容值，见 `src/Host/Autostart.cs`）就会残留。

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
  { "hive": "HKCU", "key": "Software\\Microsoft\\Windows\\CurrentVersion\\Run", "value": "<历史兼容值名>" }
]
```

---

## 1b. 卸载器：清理宿主自建/改名的快捷方式（`extraUninstallLnkNames`）

上游卸载器只删自己建的两个快捷方式：`<桌面>\{appName}.lnk` 与整个
`<开始菜单>\{appName}\` 文件夹，且「桌面 / 开始菜单」按 `needElevate` 二选一
（公共桌面 or 用户桌面）。本项目宿主还会把桌面上的历史命名
（`原神帧率解锁.lnk`、`GenshinFpsUnlocker.lnk` …）改名成英文品牌名
`HoYoEnhance.lnk`（`src/Host/ShortcutHelper.cs`），于是卸载后桌面会留下一个
指向已删除 exe 的死图标。

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
  "HoYoEnhance.lnk",
  "Uninstall HoYoEnhance.lnk",
  "<历史兼容快捷方式名>.lnk"
]
```

与 `ShortcutHelper.ShortcutNameAliases()` 曾经列举的历史别名一致
（那个方法和 `RemoveCreatedShortcuts()` 已随「宿主不做卸载」一并删除）。

---

## 1c. 卸载器：清理安装期登记的登录计划任务（`extraUninstallScheduledTasks`）

宿主把「开机自启动」分成两种登记方式，二选一（见 `src/Host/Autostart.cs`）：

| 配置组合 | 实际登记 | 卸载时怎么清 |
| --- | --- | --- |
| 只开「开机自启动」 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的历史兼容值 | `extraUninstallRegistry`（第 1 节） |
| 「开机自启动」+「启动时自动以管理员权限运行」 | 任务计划程序里的历史兼容任务（`RunLevel=HighestAvailable`） | 本节 |

计划任务既不是注册表项也不是文件，上游卸载器完全不知道它，只能靠
`schtasks /Delete /TN <名字> /F` 回收。**不清理的后果**：卸载后每次登录，
任务计划程序都会去拉起一个已经不存在的 exe。

### `src-tauri/src/installer/uninstall.rs`

- `RunUninstallArgs` 新增字段 `#[serde(default)] extra_uninstall_scheduled_tasks: Vec<String>`
  ——旧版前端不传也能反序列化。
- `run_uninstall(...)` 新增同名参数（只有 `run_uninstall_with_args` 一个调用点），
  在 `clean_extra_registry(&extra_uninstall_registry)` 之后调用
  `clean_extra_scheduled_tasks(&reg_name, &extra_uninstall_scheduled_tasks)`。
- 新增 `fn is_safe_task_name(product: &str, name: &str) -> bool`（纯函数，可跨平台断言）：
  任务名必须**以 `regName` 开头**、只含字母数字与 `._- `、长度 ≤ 100。
  这条安全阀是必需的：卸载器通常以管理员身份运行，配置里写个 `*` 就等于
  「删掉整台机器的计划任务」。
- 新增 `pub fn clean_extra_scheduled_tasks(...)`：用
  `std::process::Command::new("%SystemRoot%\\System32\\schtasks.exe")`
  + `.args(["/Delete", "/TN", name, "/F"])`（参数数组，不经 shell）
  + `CREATE_NO_WINDOW`。任务不存在 / 名字不合法 / 调用失败都只 `tracing` 记日志，
  绝不让卸载失败。

### `src/App.vue`

卸载分支构造 `ipcRunUninstall({...})` 时新增：

```ts
extra_uninstall_scheduled_tasks: PROJECT_CONFIG.extraUninstallScheduledTasks ?? [],
```

### `src/types.ts` / `src/api/ipc.ts`

- `ProjectConfig` 新增可选字段 `extraUninstallScheduledTasks?: string[]`；
- `IpcRunUninstall` 新增可选字段 `extra_uninstall_scheduled_tasks?: string[]`。

### 本项目配置

`installer/kachina.config.json`：

```json
"extraUninstallScheduledTasks": [
  "<历史兼容任务名>"
]
```

### 复核方式

- `tools/devcheck` 的 `logic` 层第 17 组断言 `is_safe_task_name`（通配符、目录形式、
  别人的任务名、空名、超长名、命令行注入形态全部要拦）；
- 第 18 组拿**真实仓库文件**核对接线：`src/Host/Autostart.cs` 里的任务名与
  `kachina.config.json` 一致、名字能过安全阀、前端与 Rust 两侧字段都接上了。

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

### 卸载器：只删属于本软件的东西（`src-tauri/src/installer/uninstall.rs`）

卸载器一般以管理员身份运行，所以「删什么」这件事必须有边界。加固后的行为，
按用户能看到的结果说：

| 场景 | 加固后的行为 |
| --- | --- |
| 清理额外注册表项（开机自启动等） | 只删本软件自己写的那一个值。系统和其他软件共用的容器（开机启动项、卸载信息、策略、文件关联等）**只删值、不整棵删** |
| 清理快捷方式 | 只删本产品名字的 `.lnk`，而且只在桌面与开始菜单的产品文件夹里找。桌面、开始菜单这些容器本身不会被删 |
| 删除用户数据 / 额外目录 | 只删配置里写明的产品数据目录。路径必须是绝对路径、不能带 `..`、不能是符号链接，也不能是盘符根、Windows 目录、`Program Files`、用户配置目录这类受保护位置**本身**（它们下面的产品子目录才可以删） |
| 删除安装目录内的文件清单 | 同上：清单里的每一项都必须真的落在安装目录内，绝对路径、越界的 `..`、根相对路径一律跳过（第 8 节） |
| 删除「旧版本残留文件」清单 | 同上，而且这条清单可能来自网络元数据，越界一样跳过（第 9 节） |
| 提权卸载时清 HKCU | 每一个登录过的用户账户都会清到，不只是执行卸载的那个管理员账户 |

命中安全阀一律「跳过 + 写日志」，**不会因为安全阀让卸载失败**；被跳过的路径会记在
`%TEMP%\KachinaInstaller.log` 里。本项目真实配置（`HKCU\...\Run` 下的
历史兼容值、用户数据目录、4 个快捷方式名字）
全部落在放行范围内，功能不受影响。

> 实现集中在 `uninstall.rs` 的四个判定函数里：`is_safe_registry_target` /
> `is_safe_shortcut_target` / `is_safe_delete_target` / `is_safe_relative_member`，
> 具体拒绝名单以代码为准（devcheck 的 `logic` 层对它们跑行为断言）。


### 用户协议正文：不允许夹带任何可执行内容（`src/utils/agreement.ts` + `src/App.vue`）

协议正文来自打包配置，最终会渲染进一个**能调用提权 IPC 的窗口**里，所以按
「只允许排版、不允许任何可执行 / 可提交 / 可外链内容」收紧：

- 白名单排版：脚本、表单与输入控件、`iframe` / 插件、内联样式、`svg` / 数学标记
  一律剥掉；链接只允许 `http(s)` 与 `mailto`（上游默认还放行 `tel:` / `cid:` 等）。
- 正文里的链接点击不会把安装器窗口导航走（那等于安装 / 卸载流程直接断掉）：
  外部链接交给系统浏览器打开，页内锚点照常，其余什么都不做。
- 内联了协议正文时，「我已阅读并同意」必须手动勾选才能点安装；静默 / 非交互安装
  与卸载界面不受影响（保持上游语义）。
- 协议正文可以选中复制（界面其余部分是不可选中的）。


### 宿主侧（`src/Host/`，不属于本目录，列在这里便于对照）

- `UninstallLauncher.IsTrustworthyUninstaller`：宿主里的「卸载本软件」只负责启动
  安装目录下的卸载程序，启动前校验：路径仍在自身目录内、
  文件名符合约定、非空文件、自身与所在目录都不是符号链接 / junction、目录不是
  盘符根 / 系统目录 / 用户配置目录；**且宿主已提权时要求安装目录位于 `Program Files` 下**
  —— 否则普通用户可以在可写目录里放一个同名 exe，借宿主的管理员令牌执行任意代码
  （典型 EoP），这种情况直接拒绝并提示改用「设置 → 应用」卸载。
  Web UI 的 `uninstall` 消息不接受任何参数，路径全部由宿主自己算，前端无法指定。
- `RuntimePrerequisite.ResolveDotNetCli`：检测 .NET 桌面运行时不再使用裸命令名 `dotnet`
  （那会按 PATH 搜索），优先 `%ProgramFiles%\dotnet\dotnet.exe`，避免提权进程被 PATH 劫持。

---

## 4. 依赖：`rcedit` 从 Git 依赖改为仓库内副本

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
所以将 `rcedit-rs@1bfa3ee6` 放入 `vendor/rcedit-rs/`，并改掉那一行。

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
  真编一遍 `rcedit-sys`，让这类「工具链与仓库内 C++ 副本不兼容」的问题在**自动**工作流里就暴露，
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
| 1 | `%VAR%` 形式的路径**从不展开** | 前端 `replacePathEnvirables` 只认 `${INSTALL_PATH}` / `${APP_NAME}` 两种写法，配置里的 `%LOCALAPPDATA%/<用户数据目录名>` 原样传进 Rust；`is_safe_delete_target` 又要求绝对路径，于是这条被当成「不安全路径」**静默跳过**——勾了「同时删除用户数据」也一个字节都不会删 |
| 2 | 只清理**当前进程**的用户目录 | 卸载器一般以管理员身份运行，`%LOCALAPPDATA%` 指向管理员账户；当初装软件的普通用户那份数据（连同该用户桌面上的 `.lnk`、开始菜单文件夹）全部留在原地 |
| 3 | 安装 / 卸载过程写进 `%TEMP%` 的文件没人管 | 运行时安装包（几十 MB）、`KachinaInstaller.log`、WebView2 引导器、卸载器自己的临时副本，失败时全留在 `%TEMP%` 里 |
| 4 | 卸载流程**不结束正在运行的主程序** | 上游只在安装流程 `installPrepare` 里做「检测 → 询问 → 结束进程」；从「设置 → 应用」/ 开始菜单发起卸载时主程序还常驻托盘，它的 exe、`logs\`、WebView2 的 `EBWebView` 缓存全被占用，删不掉 → 残留 |

### `src-tauri/src/installer/uninstall.rs`

- `fn expand_env_vars(input: &str) -> String`：手写展开 `%NAME%` → `std::env::var(NAME)`。
  **不调 Win32 API**，为的是同一份实现能在 devcheck 的 Linux harness 上真跑
  （`ExpandEnvironmentStringsW` 就得再加一个桩，测的就不再是真代码了）。规则：
  - 未知变量**原样保留** `%NAME%`（宁可少删，也不要拼出半个路径去删）；
  - `%%` 当一个字面 `%`；末尾落单的 `%` 原样输出（`"100% done"` 不变）；
  - 不支持 Windows 的子串语法 `%VAR:~a,b%`（会被当成未知变量名保留）；
  - Windows 上 `std::env::var` 本身大小写不敏感，`%localappdata%` 一样能展开；
  - 展开后**不做**绝对路径校验，交给后面的 `is_safe_delete_target` 判。
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
    读 `ProfileImagePath` 后再 `expand_env_vars` 一次（注册表里存的常是
    `%SystemDrive%\Users\xxx` 这种形式），跳过 `*_Classes` / `.DEFAULT` /
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
    这条契约由三处配合成立（前端只在勾选时传 `userDataPath`、配置里每条都是产品
    子目录、Rust 侧展开在安全阀之前且跨用户清理吃同一份列表），devcheck 的 logic 层
    第 [12] 组直接对真实仓库文件做静态断言把它钉住，改坏任何一处都会红。
- **误删除防线**（跨用户重放把爆炸半径放大了 N 倍，所以额外加两道）：
  - `const PER_USER_DENY_LEAVES`（30 余个名字，大写比较）：尾巴的**叶子名**不能是
    Shell 容器 —— `Programs` / `Start Menu` / `Microsoft` / `Windows` / `Local` /
    `Roaming` / `Documents` / `Desktop` / `Cache` / `Temp` / `OneDrive` / `Startup` …
    现实中的触发路径：`extra_uninstall_path` 里的开始菜单文件夹是前端拼的
    `Programs\{appName}`，`appName` 万一是空串，尾巴就退化成 `…\Start Menu\Programs`，
    重放到所有用户 = 把每个人的「程序」菜单整个端掉。产品自己的目录名
    与历史快捷方式名都不在表里；
  - `AppData` 下的尾巴至少**三级**（`AppData\Local\<用户数据目录名>`）：两级就
    意味着直接挂在 `AppData\Local` / `AppData\Roaming` 那一层，只可能是容器。
    `Documents` / `Desktop` 下两级是正常形状，不受这条限制；
  - 另外把 `is_protected_root` 也补全了：除了 `%USERPROFILE%` / `%APPDATA%` 这些
    根，**它们下面一层的 Shell 容器**（`Desktop`、`Documents`、`Downloads`、
    `Music`/`Pictures`/`Videos`、`AppData[\Local|\LocalLow|\Roaming]`、
    `%APPDATA%\Microsoft[\Windows[\Start Menu[\Programs[\Startup]]]]`、
    `%LOCALAPPDATA%\Microsoft`、`%LOCALAPPDATA%\Programs`、`%PUBLIC%\Desktop`）
    也一律不许删。这条管的是**上游那条按配置删除的通道**（配置里少写一段就可能
    命中），与跨用户重放无关；组件用切片 `&["AppData", "Local"]` 而不是拼好的
    字符串，否则 `Path::join("AppData\\Local")` 在非 Windows 上会变成单一组件，
    devcheck 的 logic 层跑不了。
  > 这两道只加在「容器」这一层，不影响正常清理：产品的数据目录、开始菜单文件夹、
  > 桌面 `.lnk` 全都在容器**下面至少一层**。
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

### 本项目配置（`installer/kachina.config.json`，不在本目录内）

`userDataPath` 从 1 项扩到 3 项，覆盖历史版本可能用过的落盘位置：
`%LOCALAPPDATA%`、`%APPDATA%`、`%USERPROFILE%/Documents` 下各一个
产品数据目录。三项都走同一套安全阀，并被多用户重放覆盖。

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

## 7. 遥测：Sentry 错误上报与使用统计已物理移除

上游安装器有两条外发通道。本项目是个人自用构建，不做任何统计，所以两条通道**连依赖
一起拔掉**（不是运行时关开关，也不是把 DSN 置空）：编译产物里不再残留 DSN 字符串，
`Cargo.lock` / `pnpm-lock.yaml` 里也不再有对应条目。

| 通道 | 上游行为 | 本项目 |
| --- | --- | --- |
| Rust / Sentry | `src-tauri/src/utils/sentry.rs` 里写死 DSN `http://…@steambird.cocogoat.cn/insight/kachina-installer/0`；`main.rs` 调 `sentry_init` 并挂 `sentry_tracing` layer；`ipc/manager.rs` 把 span 转成 breadcrumb、`ipc/operation.rs` 开 transaction、`installer/config.rs` 写 `configure_scope`、`utils/error.rs` 在序列化错误时 `capture_anyhow` 上报；设备标识由 `whoami` + `hostname` + `os_info` 拼出来 | 全部删除 |
| 前端 / 使用统计 | `src/api/ipc.ts` 的 `sendInsight()` 往 `https://77.cocogoat.cn/ev` POST 事件（固定 website id、当前 URL、事件名、`screen.width×height`、`navigator.language`），响应体存进 `localStorage.evCache` 当下一次的 `Authorization`；`src/App.vue` 在安装 / 完成 / 卸载 / 启动 / 两处出错共 6 处调用 | 函数、6 处调用、`getInsightBase` / `buildEventString` / `getSourceId` 三个辅助函数全部删除 |
| 构建期 | `package.json` 的 `@sentry/cli`（`pnpm-workspace.yaml` 还为它开了 `onlyBuiltDependencies`，装包时会跑 postinstall 下载 sentry-cli 二进制） | 依赖与白名单一起删除；本仓库没有任何脚本引用它 |

### 具体改动

- `src-tauri/Cargo.toml`：删 `sentry`（带 6 个 feature 的那一整块）、`sentry-tracing`、
  `whoami`（只有 `get_device_id()` 在用）。`Cargo.lock` 用 `cargo metadata` 重新生成，
  净减 15 个 crate（`sentry*` ×5、`whoami`、`hostname`、`os_info`、`debugid`、`uname`、
  `ureq`、`httpdate`、`wasite`、两个 `objc2-*`），**没有任何版本被顺带升级**。
- `src-tauri/src/utils/sentry.rs`：**整份删除**。
- `src-tauri/src/utils/mod.rs`：去掉 `pub mod sentry;` 与 `get_device_id()`；把
  `InfoFilter` **搬到这里**（见下）。
- `src-tauri/src/main.rs`：去掉 `sentry_init` / `sentry_set_info` / `sentry_layer` 与
  `_guard`，tracing registry 不再 `.with(sentry_layer)`；4 处 `sentry::add_breadcrumb`
  改成等价的 `tracing::info!`（日志本来就落本地文件，信息量不减）。
- `src-tauri/src/ipc/operation.rs`：去掉 transaction 上下文与 `transaction.finish()`，
  `run_opr` 少一个 `context: Vec<(String, String)>` 参数。
- `src-tauri/src/ipc/manager.rs`：去掉 `IpcInner.context` 字段、span→context 的转换、
  envelope/breadcrumb 那条 IPC 分支、`sentry_rx` 与 `AUTO_TRANSPORT` 的 select 分支；
  两处 `run_opr` 调用跟着改。
- `src-tauri/src/installer/config.rs`：`configure_scope` 换成一句本地 `tracing::info!`。
- `src-tauri/src/utils/error.rs`：删掉序列化里的 `super::sentry::capture_anyhow(...)`。
- `src/api/ipc.ts`：删 `sendInsight()`。
- `src/App.vue`：删 6 处调用 + 3 个辅助函数 + import；`installPrepare()` 的
  `useOnlineSource` 参数只被埋点用，一并删掉（两个调用点跟着改）。
- `package.json` / `pnpm-workspace.yaml`：删 `@sentry/cli`；`pnpm-lock.yaml` 用
  `pnpm install --lockfile-only` 重新生成，净减 17 个包（`@sentry/cli` + 8 个平台
  二进制 + `node-fetch` / `https-proxy-agent` / `agent-base` / `proxy-from-env` /
  `progress` / `whatwg-url` / `tr46` / `webidl-conversions`），**0 个版本变化**。

### 特意保留的东西

- **`InfoFilter`**：上游把它放在 `utils/sentry.rs` 里（和 Sentry 的 breadcrumb 过滤
  配套），但 `main.rs` 的**控制台 layer 与文件 layer 也在用它**做级别过滤。删文件前
  先把这个 struct + impl 搬到 `utils/mod.rs`，否则日志会退化成全量输出。
- **`src/utils/networkInsights.ts`**：名字像遥测，其实只是安装过程中的**本地**耗时数组
  （url / ttfb / size），渲染在安装界面里给用户看，不外发。保留。
- **`chksum_md5` / `twox-hash`**：与遥测无关（校验下载文件）。保留。
- **功能性网络请求**：GitHub Releases 下载与更新检查、`builds.dotnet.microsoft.com`
  （.NET Desktop Runtime）、`aka.ms/vs/17/release/vc_redist.*`（VC++ 运行库）、
  `go.microsoft.com/fwlink/p/`（WebView2 引导器）都原样保留 —— 这些是安装器要干的活。
  `mirrorchyan.com`（Mirror酱）只在配置了 CDK 时才会请求，本项目 `kachina.config.json`
  没配，代码路径不会走到。

### 自动断言

`tools/devcheck` 的 `vendor` 层第 8 组共 4 类断言挡住回归：`utils/sentry.rs` 不许再出现；
`Cargo.toml` / `package.json` / `pnpm-workspace.yaml` 与两个 lock 里不许再有
`sentry*` / `whoami` / `@sentry/*`；77 个 Rust+前端源文件剥掉行注释后不许出现
`sentry::` / `sentry_tracing` / `capture_anyhow` / `add_breadcrumb` / `start_transaction` /
`configure_scope` / `sendInsight` / `getInsightBase` / `evCache`；112 个文本文件里不许
再出现上报域名 `cocogoat`。`-SelfTest` 有 3 个对应注入（真代码行 `sentry::init`、
`Cargo.toml` 里的 `sentry = {…}`、一个带 DSN 域名的临时 `.ts`），确认这些断言不是空壳。

> Rust 侧的删除**没有**在本地整份编译过：kachina 本体不在 devcheck 的 `rust` 层范围内
> （那层只把 `uninstall.rs` + `utils/error.rs` 塞进最小 crate 做类型检查，本次也过了）。
> `main.rs` / `ipc/*` / `config.rs` 的改动要靠手动触发 `Build` 工作流验证。

---

## 8. 第二轮安全加固：卸载收尾、路径比较、提权管道、ARP 卸载入口

| # | 位置 | 加固后的行为 |
| --- | --- | --- |
| 1 | 卸载器：安装目录内的文件清单 | 清单里每一项都必须真的落在安装目录内；绝对路径、越界的 `..`、根相对路径、UNC 一律跳过。这是当时唯一一个没有安全阀的删除通道 |
| 2 | 卸载器：收尾顺序 | 用户数据删不掉时不再中途退出 —— 注册表清理和「应用和功能」里的卸载项**一定会清掉**，不会留下卸不掉的僵尸条目；错误留到最后一起报 |
| 3 | 卸载器：路径比较 | 大小写或斜杠方向不同的同一个路径现在认得出来（`C:\Program Files` 与 `c:\program files`），「卸载器不在安装目录里就别把它移动走」这类保护不再误判 |
| 4 | 提权管道 | 提权进程死掉后不会再被当成活的。原来会让界面无限转圈（既不报错也不结束，只能重启安装器），现在会复位，下次调用重新拉起 |
| 5 | 运行时下载（.NET / VC++） | 见第 9 节 —— 那条链路和 WebView2 的两处一起统一处理了 |
| 6 | ARP 卸载入口 + 凭据残留 | 为“应用和功能”中的卸载命令加引号（路径含空格时原来会被截断成 `C:\Program`），并补上静默卸载入口；卸载时顺带清理 Windows 凭据管理器中的 Mirror酱 CDK |

### 静默卸载只能用短选项（踩过的坑）

`QuietUninstallString` 第一版写的是 `--uninstall --silent --non-interactive`，但
`src/cli/arg.rs` 里这几个开关**只声明了短名**，clap 不会凭空生成长名：传长名的结果是
clap 直接以退出码 2 报「unexpected argument」，卸载一步都不跑，而 ARP 里那个值看起来
是「存在」的，比没写更难查。正确的是 `-U -S -I`。
devcheck 的 `vendor` 层为此加了一组静态断言（卸载命令必须整体加引号；用到的每个选项
都要在 `cli/arg.rs` 里真的声明过），并配了一条自检注入用例。

---

## 9. 第三轮安全加固：把「下载后执行」和「提权管道」两条链路一次收干净

第 8 节只修了 .NET / VC++ 运行时的下载。复查发现同一个反模式还有两处（WebView2），
另外「网络元数据驱动的删除」和日志文件也是同一类问题，这轮一起处理，公共实现抽到
`src-tauri/src/utils/secure_temp.rs`。

### 9.1 下载后执行的三个文件（.NET 运行时 / VC++ 运行库 / WebView2 引导器）

上游三处是同一个写法：`%TEMP%` 里的**固定文件名** + 「已存在就覆盖」的写入
（会**跟随符号链接**）+ 下完不验签直接运行。用户视角的后果：同一台机器上的普通权限
程序，既能让安装器把下载内容写进系统文件，也能在安装器运行它之前把文件换成自己的
程序 —— 而安装器通常是提权的。

加固后（三处共用 `utils/secure_temp.rs`）：

- 落地目录改成 `C:\Windows\Temp`（只有管理员和 SYSTEM 能写），拿不到才退回用户自己的 `%TEMP%`；
- 文件名带随机 UUID，不可预测；目标已存在就**失败**，绝不覆盖、也不跟随符号链接；
- 下载中断会清掉半个文件；
- **运行前验微软签名**：签名无效、或签名者不是 Microsoft Corporation，一律删掉文件并报错。
  运行时装不上时宿主会引导用户自己去官网下载，不会把安装流程卡死；
- 验签用 PowerShell 的 `Get-AuthenticodeSignature`（Win10+ 自带），**没有**引入 unsafe FFI：
  `WinVerifyTrust` 那套调用在本仓库无法实机验证，写错一个字段就是运行时崩溃。

### 9.2 「旧版本残留文件」清单（`rm_list`）

安装时前端会拿一份「旧版本残留文件」清单（在线安装时**来自网络元数据**）拼上安装目录，
交给提权进程逐个删除。上游对这份清单不做任何校验，一条 `..\..\..\Windows\System32\xxx`
就能以管理员权限删掉系统文件。现在它与用户数据目录共用同一套判定（必须绝对、不含
`..`、不是符号链接、不在 Windows 目录内、不是受保护根目录），越界一律跳过并记日志。

### 9.3 日志文件

`%TEMP%\KachinaInstaller.log` 是固定名字，而提权子进程也走同一段初始化 —— 普通权限
程序预先放一个符号链接，就能让管理员权限的进程往任意文件里追加内容。现在打开前先检查
路径（含所有父级）是不是符号链接 / junction，是就只写控制台日志、不写文件。

### 9.4 提权管道的访问权限

管道由安装界面（普通权限）创建、提权子进程连上来，中间 UAC 弹窗那段时间管道在等人连
（用户犹豫多久就有多久）。上游的访问控制允许**任何本地用户**连接，还包括沙箱 /
AppContainer 进程、远程会话，并把完整性级别压到 Low。于是别的程序可以抢在提权子进程
之前连上、冒充它：收下安装器发来的操作与文件流、回一份伪造的结果（界面会以为装 / 卸
成功），真正的提权进程随后连不上而失败。

现在收紧成「只有创建它的那个用户 + SYSTEM + 管理员」，完整性级别提到 Medium：沙箱进程、
低完整性进程、其他本地用户都进不来。正常流程两端是同一个用户，不受影响。

> **更正第 8 节之前的一个判断**：早先记成「普通用户可以借提权进程的手删文件（EoP）」，
> 那是错的 —— 操作只能由管道服务端（安装界面自己）写入，提权子进程是客户端，别人注入
> 不了操作。真实影响是会话劫持 / 结果伪造 / 安装失败，已按此定级。

### 9.5 有意保留的三处

- `RmList` / `KillProcess` 之外的其余 IPC 操作（写注册表、建快捷方式、装运行时等）
  同样只接受安装界面发来的参数：管道收紧后，能发操作的只有本用户自己的安装界面。
- 运行时版本仍跟 `latest.version`、不锁版本号：锁版本会让 .NET 的补丁更新失效，而验签
  已经覆盖了「拿到的是不是微软的东西」这个真正的风险点。
- `select_dir` 现在通过在目标目录创建随机临时文件来探测可写性；不会再把目录本身
  当作普通文件打开，因此已有目录的权限提示与实际状态一致。
- 卸载器把自己 `rename` 到 `%TEMP%\kachina.uninst.<时间戳>.exe` 再自删：时间戳可预测，
  但 `rename` 不跟随符号链接、随后的 `del` 删的也只是链接本身，最坏是让 rename 失败并
  回退到安装目录的父级（上游已有的分支）。
- `tauri_main` 把工作目录切到 `%TEMP%`：没找到依赖相对路径的写操作，先记着不动。

### 9.6 验证到哪一步

- devcheck 全绿；`logic` 层的行为断言覆盖到第 9.2 的判定（两个平台的 `..` 形状都测）。
- `utils/secure_temp.rs` 与两处 WebView2 调用点：另搭最小 crate 在
  `x86_64-pc-windows-msvc` 上类型检查通过、0 warning。
- **两处需要实机确认**（本地只能编译、跑不了安装）：
  1. 管道 ACL —— 若提权流程连不上管道（界面报 `ELEVATE_ERR` / `Elevate Fail`），
     把 `utils/acl.rs` 里的 SDDL 改回上游那串即可回退；
  2. 验签的证书 Subject 布局 —— 不匹配时会 fail-closed（删文件 + 报错 + 引导手动下载），
     错误信息里带着实际的 status 与 subject，照着调 `is_trusted_microsoft_signature` 即可。

### 9.7 本轮复查修复

- `get_userprofile` 改用 `FOLDERID_Profile`。旧实现把空 token 句柄传给
  `GetUserProfileDirectoryW`，Windows 会返回 `ERROR_INVALID_HANDLE`，使私有目录判断失效。
- 安装文件、快捷方式和 Mirrorc ZIP 条目都拒绝绝对路径、`..`、系统目录和重解析点；ZIP
  解包遍历完整条目数，不再无条件漏掉最后一项。
- 创建卸载器时校验文件名和输出目录，避免提权进程跟随旧的 junction / symlink；自更新
  比较改用 Windows 大小写不敏感的路径语义。
- WebView2 安装成功后的对话框关闭逻辑不再提前清空句柄，避免成功安装后必然 panic；
  下载尚未创建对话框时也能安全处理。

## 10. 前端模块拆分与注释语言统一

`src/dfs.ts` 中 DFS2 会话创建、挑战重试和会话清理已移到 `src/dfs/session.ts`，原文件
通过导出保持现有 `App.vue` 调用接口不变；下载编排仍留在 `dfs.ts`，模块职责更清晰。
本目录的 Rust、TypeScript、Vue、测试及仓库内 C/C++ 功能注释已统一为中文。
许可证和版权声明保持原文；API 名称、协议字段、错误码、路径、命令及 URL 等技术标识
保留原样，避免翻译改变其含义或影响复制使用。

---

## 11. 构建日志告警收敛

`pnpm build` 在 `-Z build-std` 下会带出两类与产物无关的告警，逐条从源头上消掉：

### `libs/hdiff-sys/src/lib.rs`、`libs/hpatch-sys/src/lib.rs`

```rust
#![allow(suspicious_runtime_symbol_definitions)]
```

bindgen 生成绑定时会顺手把 MSVC 的 CRT extern 声明也生成一遍（`memcmp` / `memcpy` /
`memmove` / `memset` / `strlen`），这些声明在 64 位下用 C 侧签名，rustc 1.9x 起会对
它们报 `suspicious definition of the runtime ... symbol`，每个 crate 5 条。绑定文件
（`binding.rs`）由 `include!` 引入 `src/lib.rs`，所以在 crate 根上关掉该 lint 即可，
不必改生成物。重新生成绑定时这条属性不受影响。

### `src-tauri/src/cli/arg.rs`

```rust
#[allow(dead_code)]
Other(Vec<String>),
```

`Command::Other` 由 clap 的 `external_subcommand` 在解析阶段写入，编译期看不到读取方，
于是 `field 0 is never read` 报一条。该字段不能删（删了外部子命令就接不住），
局部关闭 dead_code 是最小改法。

---

## 12. 品牌改名后的升级兼容

主程序从历史名 `GenshinFpsUnlocker.exe` 改为 `HoYoEnhance.exe` 后，同名哈希覆盖
不再能处理旧 exe、旧卸载器和旧更新器。安装器配置新增以下兼容字段：

- `legacyExeNames`：`config.rs` 在 `CURRENT_DIR` / `PARENT_DIR` / 注册表
  `InstallLocation` / 默认 `ProgramFiles` 路径下同时探测旧主程序名；
  `installer/mod.rs` 的 `select_dir` 也据此把旧目录标为可升级；
- `legacyProgramFilesPaths`：注册表项缺失时，在旧默认安装目录兜底查找；
- `legacyUninstallNames`：让新安装器仍能识别旧卸载器入口；

`src/App.vue` 的 `installPrepare` 会同时枚举新旧 exe 进程并结束仍在运行的旧实例；
`finishInstall` 在更新场景重建开始菜单项（旧 exe 名已不存在）；桌面图标不新建，
宿主 `ShortcutHelper.RefreshDesktopShortcuts()` 只把**已存在**的桌面图标
（规范名或历史命名）改指当前 exe，避免给当初没勾选桌面快捷方式的用户补一个。

`installer/pack.ps1` 在生成 metadata 后追加旧 exe / 卸载器 / 更新器及旧位图到
`deletes`，更新时由安装器统一清理；宿主启动时对同一批固定文件名再做一次兜底。
旧安装目录和旧开始菜单文件夹另外通过 `extraUninstallPath` 与 `extraUninstallLnkNames`
兜底。

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
3b. **重做第 7 节的遥测移除**（上游几乎一定会带着 Sentry 回来）：删
   `src-tauri/src/utils/sentry.rs`、按第 7 节清单改 `Cargo.toml` / `main.rs` /
   `ipc/manager.rs` / `ipc/operation.rs` / `installer/config.rs` / `utils/error.rs` /
   `api/ipc.ts` / `App.vue` / `package.json` / `pnpm-workspace.yaml`，**注意把
   `InfoFilter` 搬到 `utils/mod.rs`**，然后重新生成两个 lock（`cargo metadata` +
   `pnpm install --lockfile-only`）。跑 `pwsh tools/devcheck/devcheck.ps1 -Layer vendor`
   确认第 8 组断言全绿；
3c. **重做第 8 节的 6 处加固**：`uninstall.rs`（`normalize_path_for_compare` /
   `path_starts_with` / `is_safe_relative_member` 三个助手 + `files` 安全阀 +
   收尾错误聚合 + 三处比较改用新助手）→ `ipc/manager.rs`（三条失败路径清
   `process`）→ `installer/runtimes.rs`（落地目录 / 独占创建 / 验签，注意
   `use crate::fs::{…}` 里要去掉 `create_target_file`）→ `installer/registry.rs`
   （`UninstallString` 加引号 + `QuietUninstallString`）→ `src/App.vue`
   （`wincred_delete`）。跑 `pwsh tools/devcheck/devcheck.ps1 -Layer rust,logic`
   确认 [13] [14] 两组断言全绿；
3d. **重做第 9 节**：新增 `src-tauri/src/utils/secure_temp.rs`（整份）并在
   `utils/mod.rs` 里挂上 → `installer/runtimes.rs`、`module/wv2.rs`、`cli/mod.rs`
   三处改成调用它 → `installer/uninstall.rs` 的 `rm_list` 加安全阀、
   `has_reparse_point` 提为 `pub` → `main.rs` 的日志文件加重解析点检查 →
   `utils/acl.rs` 换 SDDL。跑 `pwsh tools/devcheck/devcheck.ps1 -Layer vendor,rust,logic`；
3e. **重做第 11 节的告警收敛**：两个 `libs/*-sys` 的 `src/lib.rs` 各加一行
   `#![allow(suspicious_runtime_symbol_definitions)]`，`cli/arg.rs` 的
   `Other(Vec<String>)` 上加 `#[allow(dead_code)]`；
3f. **重做第 12 节的改名兼容**：`installer/config.rs` 增加旧 exe / 旧安装目录探测，
   `installer/mod.rs` 的 `select_dir` 增加 `legacy_exe_names`，`App.vue` 同步结束旧进程
   并在更新时重建快捷方式；
4. `npx tsc --noEmit -p tsconfig.json`（上游本身有 3 个 `noUnusedLocals` 报错，
   只要没有新增报错即可）+ 用 `@vue/compiler-sfc` 编译 `src/App.vue` 自检；
5. Windows 上 `pnpm build` 出 `kachina-builder.exe`，跑一次
   `installer\pack.ps1`，确认：安装界面能弹出协议全文；卸载后
   `HKCU\...\Run` 里的历史兼容值消失；桌面上的当前与历史快捷方式、
   开始菜单文件夹一并消失；勾选「同时删除用户数据」后
   **每一个**登录过的用户账户下的 `%LocalAppData%\<用户数据目录名>` 都消失
   （以管理员身份从普通用户装的副本上卸载时尤其要验这一条，即洞 2）；
   主程序在托盘里运行时发起卸载，会先弹「是否结束进程」的询问，结束后
   `EBWebView` 缓存与 `logs\` 也一并删掉（洞 4）。

> 上述 Rust 逻辑（`clean_extra_registry` / `rm_best_effort` / `is_safe_registry_target` /
> `is_safe_shortcut_target` / `is_safe_delete_target` / `resolve_agreement`，以及第 6 节的
> `expand_env_vars` / `expand_path_list` / `profile_relative_tail` / `loaded_profile_roots` /
> `collect_all_users_cleanup_targets` / `is_installer_temp_artifact`）
> 已在 Linux 上用 mock 版 `windows-registry` + 真实 `serde_json` / `tokio` 逐条跑过
> **127 个断言**（含提权卸载遍历 `HKEY_USERS`、`value` 为空、共享容器键、符号链接 /
> 系统目录 / 路径穿越 / 受保护根目录、协议 BOM/CRLF 与文件缺失、用户目录尾巴的
> 形状与 `Desktop` 白名单、多用户重放、`%TEMP%` 条目的命中与放行边界、Shell 容器
> 黑名单与「容器本身不许删 / 容器下面一层的产品目录放行」、以及第 [12] 组对真实
> 仓库文件的勾选语义静态断言），其中
> `resolve_agreement` 是拿仓库里真实的 `installer/kachina.config.json` +
> `USER_AGREEMENT.txt` 跑的；Windows 专有 API（重解析点属性、`%SystemRoot%`、
> ProfileList）在 harness 里用桩替代；
> `clean_installer_temp_files` / `clean_per_user_leftovers` 是纯 IO 包装，只断言其
> 判定函数（`is_installer_temp_artifact` / `profile_relative_tail`）。
> 桩里的 `is_under_system_root` 从 `contains("/windows/")` 改成了
> `starts_with("/windows/")`（与真实实现的**前缀**语义一致）：否则开始菜单那种
> `<用户>\AppData\Roaming\Microsoft\Windows\Start Menu\…` 会被当成系统目录，
> 「产品开始菜单文件夹要跨用户清掉」这条正向对照在 Linux 上根本测不到。
> 前端侧的 `killRunningAppForUninstall` 只有 `tsc --strict` + SFC 编译 + prettier 把关。
>
> 写断言时的一个坑（CI 的 windows job 抓到过）：夹具路径必须用
> `std::env::temp_dir()` 拼，不能写死 `/tmp/...` —— 后者在 Windows 上**不是**绝对路径
> （没有盘符前缀），会被 `is_absolute()` / `is_safe_delete_target` 先拦掉，
> 于是断言测不到它本来想测的那条规则（当时表现为 `expand_path_list` 与
> 「展开前拦掉、展开后放行」两条在 Windows 上假失败，Linux 上却全过）。
> 整套逻辑**没有**在 Windows 上实机验证过。

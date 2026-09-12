# 本目录对上游 kachina-installer 的本地修改

上游快照：tag `0.5.1` / commit `ae461aa9ddd5a8f5e445459e14f5d611921f9938`（见 `UPSTREAM.md`）。

本目录**不是纯净快照**：为了本项目的两个需求，在 4 个上游文件 + 1 个新增文件上做了
最小化改动。升级上游版本时必须按本清单重新套用（都是「加字段 / 加分支」，
不改动上游既有逻辑，冲突概率低）。

| # | 需求 | 涉及文件 |
| --- | --- | --- |
| 1 | 卸载时清理安装期写入的注册表（开机自启动等） | `src-tauri/src/installer/uninstall.rs`、`src/App.vue`、`src/types.ts`、`src/api/ipc.ts` |
| 1b | 卸载时清理安装期由宿主自建/改名的快捷方式 | 同上 4 个文件 |
| 2 | 用户协议可配置、多格式、点击弹窗看全文 | `src-tauri/src/builder/pack.rs`、`src/App.vue`、`src/types.ts`、`src/utils/agreement.ts`（新增） |
| 3 | 安全加固：收敛卸载器的删除范围与提权面 | `src-tauri/src/installer/uninstall.rs`、`src/utils/agreement.ts`、`src/App.vue`（另有宿主侧 `src/Host/UninstallLauncher.cs`、`src/Host/RuntimePrerequisite.cs`，不属于本目录） |

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
- scoped 样式新增 `.agreement-body`（`max-height: 46vh` + `overflow-y: auto`）
  及其 `:deep()` 子元素样式。选择器都以 `.agreement-body[data-v-*]` 开头，
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

## 升级上游时的套用顺序

1. 按 `UPSTREAM.md` 覆盖整个目录；
2. 恢复本文件（`LOCAL_PATCHES.md`）与 `UPSTREAM.md`；
3. 依次套用上面的改动：`uninstall.rs`（注册表清理 + `rm_best_effort` + 第 3 节的
   全部安全阀）→ `pack.rs` → `types.ts` → `api/ipc.ts` → `utils/agreement.ts`
   （整份新增，含 DOMPurify 收紧策略）→ `App.vue`（协议弹窗 4 处 + 快捷方式清理 2 处
   + 链接点击拦截 + `acceptEula` 初始化）；
4. `npx tsc --noEmit -p tsconfig.json`（上游本身有 3 个 `noUnusedLocals` 报错，
   只要没有新增报错即可）+ 用 `@vue/compiler-sfc` 编译 `src/App.vue` 自检；
5. Windows 上 `pnpm build` 出 `kachina-builder.exe`，跑一次
   `installer\pack.ps1`，确认：安装界面能弹出协议全文；卸载后
   `HKCU\...\Run` 里的 `GenshinFpsUnlocker` 值消失；桌面上的
   `原神帧率解锁.lnk` 与开始菜单文件夹一并消失。

> 上述 Rust 逻辑（`clean_extra_registry` / `rm_best_effort` / `is_safe_registry_target` /
> `is_safe_shortcut_target` / `is_safe_delete_target` / `resolve_agreement`）已在 Linux 上
> 用 mock 版 `windows-registry` + 真实 `serde_json` / `tokio` 逐条跑过 53 个断言
> （含提权卸载遍历 `HKEY_USERS`、`value` 为空、共享容器键、符号链接 / 系统目录 /
> 路径穿越 / 受保护根目录、协议 BOM/CRLF 与文件缺失等边界），其中
> `resolve_agreement` 是拿仓库里真实的 `installer/kachina.config.json` +
> `USER_AGREEMENT.txt` 跑的；Windows 专有 API（重解析点属性、`%SystemRoot%`）在
> harness 里用桩替代。整套逻辑**没有**在 Windows 上实机验证过。

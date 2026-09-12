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

## 升级上游时的套用顺序

1. 按 `UPSTREAM.md` 覆盖整个目录；
2. 恢复本文件（`LOCAL_PATCHES.md`）与 `UPSTREAM.md`；
3. 依次套用上面的改动：`uninstall.rs`（注册表 + `rm_best_effort`）→ `pack.rs`
   → `types.ts` → `api/ipc.ts` → `utils/agreement.ts`（整份新增）
   → `App.vue`（协议弹窗 4 处 + 快捷方式清理 2 处）；
4. `npx tsc --noEmit -p tsconfig.json`（上游本身有 3 个 `noUnusedLocals` 报错，
   只要没有新增报错即可）+ 用 `@vue/compiler-sfc` 编译 `src/App.vue` 自检；
5. Windows 上 `pnpm build` 出 `kachina-builder.exe`，跑一次
   `installer\pack.ps1`，确认：安装界面能弹出协议全文；卸载后
   `HKCU\...\Run` 里的 `GenshinFpsUnlocker` 值消失；桌面上的
   `原神帧率解锁.lnk` 与开始菜单文件夹一并消失。

> 上述 Rust 逻辑（`clean_extra_registry` / `rm_best_effort` / `resolve_agreement`）
> 已在 Linux 上用 mock 版 `windows-registry` + 真实 `serde_json`/`tokio` 逐条跑过
> （含提权卸载遍历 `HKEY_USERS`、BOM/CRLF、文件缺失等边界），
> 但**没有**在 Windows 上实机验证过。

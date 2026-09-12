# tools/devcheck — 不跑完整构建的本地体检

`installer/kachina` 是一个 **Tauri + Windows 专用** 的子项目：完整构建要 nightly Rust、
`x86_64-win7-windows-msvc` 自定义 target、`-Z build-std`、pnpm 全家桶，本地跑一次
好几分钟，CI 更久。结果是「改一行 Rust / Vue，只能靠一次完整构建来发现写错了」。

`devcheck` 解决这个问题：把**我们真正改过的那部分代码**放进最小依赖的检查环境里，
用几秒到十几秒给出「会不会编译失败 / 逻辑有没有被改坏」的答案。

```powershell
# 仓库根目录
pwsh tools/devcheck/devcheck.ps1                 # 跑 all（ps1 gen rust logic front host）
pwsh tools/devcheck/devcheck.ps1 -Layer rust,logic
pwsh tools/devcheck/devcheck.ps1 -SelfTest       # 自检：注入 5 个错误，确认每层都会报错
pwsh tools/devcheck/devcheck.ps1 -Fix            # 只对我们维护的 .rs 跑 rustfmt
```

任何一层失败 → 退出码 1。缺工具链的层标记 `SKIP` 并给出提示，不算失败。

## 分层

| 层 | 检查什么 | 需要的工具 | 热跑耗时 |
| --- | --- | --- | --- |
| `ps1` | 仓库里全部 `.ps1` 的语法（PowerShell Parser） | pwsh 7 | <0.1s |
| `gen` | 从 `installer/kachina` 源码生成检查用的 Rust / TS 文件 | pwsh 7 | ~0.3s |
| `rust` | **整份** `installer/uninstall.rs` + `utils/error.rs` 的类型检查：塞进一个只有 11 个依赖的 crate，`cargo check --target x86_64-pc-windows-msvc`。不需要 tauri、不需要 Windows 机器 | cargo + `rustup target add x86_64-pc-windows-msvc` | 首次 ~30s，之后 ~0.2s |
| `logic` | 同一批函数的**行为断言**（53 条）：注册表安全阀、快捷方式安全阀、用户数据目录安全阀、`path_eq` 归一化、以及拿**仓库真实的** `installer/kachina.config.json` + `USER_AGREEMENT.txt` 跑 `resolve_agreement` | cargo | 首次 ~15s，之后 ~0.4s |
| `front` | `utils/agreement.ts` + `types.ts` 的 `tsc --strict`；`installer/kachina/src` 下**全部** `.vue` 的 `@vue/compiler-sfc` 编译；`agreement.ts` 的 prettier 风格 | node + npm | 首次 ~10s，之后 ~2s |
| `host` | `src/Host` 的 `dotnet build -c Release -p:EnableWindowsTargeting=true` | .NET 9 SDK | ~2–8s |
| `ui` | `src/Ui` 的 `vite build`（**不在 `all` 里**，要先 `cd src/Ui && npm install`） | node + npm | 视机器 |

全套热跑 ≈ 6–12 秒。

## `-SelfTest`：证明这套检查不是空壳

检查工具最大的风险是「跑通了但其实什么都没查」。`-SelfTest` 会先正常生成一次，
然后往**生成物**里注入 5 个错误（仓库源码一个字都不改），逐个确认对应层会失败：

| 注入 | 期望 |
| --- | --- |
| `tools/devcheck/_selftest/broken.ps1`（`if` 少了右括号） | `ps1` 层报错 |
| `gen/uninstall.rs` 末尾追加 `let _x: u32 = "不是数字";` | `rust` 层报错 |
| `gen/extracted.rs` 里把 `segments.len() >= 2` 改成 `>= 0` | `logic` 层断言失败 |
| `gen/src/utils/agreement.ts` 末尾追加 `const x: number = 'not a number';` | `front` 的 tsc 报错 |
| `front/_selftest/Broken.vue`（`<div>` 未闭合） | `front` 的 SFC 编译报错 |

跑完自动删掉临时目录并重新生成干净的检查源。任何一个「注入了却没报错」→ 退出码 1。

## 实现方式（为什么这样能代表真实构建）

```
tools/devcheck/
├── devcheck.ps1            入口：分层执行 / 汇总 / -SelfTest / -Fix
├── lib/RustSource.ps1      Rust 源码抽取：先把字符串与注释「挖空」，再做括号配对定位 item 边界
├── lib/Generate.ps1        生成两个 crate 的 src/gen 与 front/gen（含要抽取的 item 清单）
├── rust/typecheck/         整文件类型检查 crate（真实依赖，Windows target）
│   ├── Cargo.toml          依赖版本与 kachina src-tauri/Cargo.toml 对齐
│   └── src/lib.rs          把生成文件挂到上游的模块路径上 + 3 个最小桩
├── rust/logic/             行为断言 crate（mock windows-registry，跨平台）
│   └── src/main.rs         53 条断言 + mock
└── front/                  package.json / tsconfig.json / sfccheck.mjs
```

- **`typecheck`**：`gen/uninstall.rs` 是上游文件的**逐字节复制**，唯一改动是把
  `#[tauri::command]` 那一行换成注释（本 crate 不依赖 tauri）。`lib.rs` 只提供三个桩：
  `dfs::InsightItem`（字段与上游一致）、`local::get_base_with_config`（返回一个
  `AsyncRead`）、`sentry::capture_anyhow`（no-op，签名与上游一致）。
  **桩与上游签名不一致时会直接编译失败**，所以上游改了这些接口 devcheck 会立刻报警。
- **`logic`**：只 mock 两样东西 —— `windows_registry`（记录调用，用来断言
  「删了什么 / 没删什么」）和 `has_reparse_point` / `is_under_system_root`
  （Windows 专有 API，换成按路径名触发的桩：路径含 `REPARSE` 视为符号链接，
  含 `/windows/` 视为系统目录）。其余都是上游/本项目的真实代码。
- **抽取用括号配对而不是行号切片**：上游在文件里增删别的函数不会影响结果；
  但清单里的 item 一旦改名/删除，`Get-RustItem` 会**抛错**而不是静默少测。

## 覆盖范围（诚实地说）

**能抓到**：Rust 类型/借用/生命周期错误（含 `std::os::windows`、`windows`、
`windows-registry` 的 API 误用）、`uninstall.rs` 里安全阀逻辑被改坏、
协议内联（`resolve_agreement`）行为变化、TS 类型错误、`.vue` 模板/`<script setup>`
语法错误、C# 编译错误与警告、`.ps1` 语法错误、我们维护文件的格式漂移。

**抓不到**（这些还得靠真实构建 / 实机）：

- `#[tauri::command]` 宏展开、IPC 参数名与前端 `invoke` 的对齐
- `builder/pack.rs` 除 `resolve_agreement` 之外的部分（依赖 builder 的一大堆模块）
- kachina 其余 Rust 模块（`installer/lnk.rs`、`dfs.rs`、`local.rs` …）
- CI 用的 `x86_64-win7-windows-msvc` 自定义 target + `-Z build-std`（这里用标准
  `x86_64-pc-windows-msvc`，能覆盖绝大多数编译错误，但不是同一个 target）
- `src/Stub` 的 C++、任何**运行期**行为（注册表真的删没删、UAC、符号链接属性位）
- `.vue` 里的**类型**错误（SFC 编译只查语法；完整类型检查要 `vue-tsc` + kachina 全部依赖）

## 维护约定

- 在 `uninstall.rs` 里新增/重命名安全阀函数 → 同步 `lib/Generate.ps1` 的
  `$script:LogicItems` 清单，并在 `rust/logic/src/main.rs` 里补断言。
- kachina 升级依赖版本（`Cargo.toml`）→ 同步 `rust/typecheck/Cargo.toml`，
  否则类型检查结论不可信。
- 上游改了 `dfs::InsightItem` / `local::get_base_with_config` /
  `utils::sentry::capture_anyhow` 的签名 → `rust` 层会编译失败，按报错改
  `rust/typecheck/src/lib.rs` 里的桩即可。
- 生成物（`*/src/gen/`、`front/gen/`、`_selftest/`）不入库，见 `.gitignore`。

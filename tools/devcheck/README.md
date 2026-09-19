# tools/devcheck — 不跑完整构建的本地体检

`installer/kachina` 是一个 **Tauri + Windows 专用** 的子项目：完整构建要 nightly Rust、
`x86_64-win7-windows-msvc` 自定义 target、`-Z build-std`、pnpm 全家桶，本地跑一次
好几分钟，CI 更久。结果是「改一行 Rust / Vue，只能靠一次完整构建来发现写错了」。

`devcheck` 解决这个问题：把**我们真正改过的那部分代码**放进最小依赖的检查环境里，
用几秒到十几秒给出「会不会编译失败 / 逻辑有没有被改坏」的答案。

```powershell
# 仓库根目录
pwsh tools/devcheck/devcheck.ps1                 # 跑 all（vendor ps1 gen rust logic native front host hosttest ci）
pwsh tools/devcheck/devcheck.ps1 -Layer rust,logic
pwsh tools/devcheck/devcheck.ps1 -SelfTest       # 自检：注入 15 个错误，确认每层都会报错
pwsh tools/devcheck/devcheck.ps1 -Fix            # 只对我们维护的 .rs 跑 rustfmt
```

任何一层失败 → 退出码 1。缺工具链的层标记 `SKIP` 并给出提示，不算失败。

这套检查也在 CI 里**自动执行**：`.github/workflows/devcheck.yml`（push 到 main、
任何 PR、手动触发；ubuntu + windows 双 runner）依次跑 `-Layer vendor` → `all` →
`-SelfTest` → Web UI 构建。完整的打包工作流 `build.yml` 仍然只手动触发。

## 分层

| 层 | 检查什么 | 需要的工具 | 热跑耗时 |
| --- | --- | --- | --- |
| `vendor` | **kachina 只用仓库内源码**：不是 submodule、快照完整、工作流与打包脚本里没有任何从上游拉源码/下二进制的动作、CI 确实走源码构建、git 依赖锁到 commit、npm 依赖全来自 registry、**遥测（Sentry / cocogoat 统计）没被加回来** | pwsh 7 | ~0.8s |
| `ps1` | 仓库里全部 `.ps1` 的语法（PowerShell Parser） | pwsh 7 | <0.1s |
| `gen` | 从 `installer/kachina` 源码生成检查用的 Rust / TS 文件 | pwsh 7 | ~0.3s |
| `rust` | **整份** `installer/uninstall.rs` + `utils/error.rs` 的类型检查：塞进一个只有 11 个依赖的 crate，`cargo check --target x86_64-pc-windows-msvc`。不需要 tauri、不需要 Windows 机器 | cargo + `rustup target add x86_64-pc-windows-msvc` | 首次 ~30s，之后 ~0.2s |
| `native` | vendored `rcedit-sys` 的 C++（`rescle.cc` / `librcedit.cpp`）真用 MSVC 编一遍。没有 `cl.exe` 的机器（Linux / 未进 VS 开发环境的 Windows）自动 SKIP | cargo + MSVC（`cl.exe` 在 PATH） | 首次 ~30s，之后 ~2s |
| `logic` | 同一批函数的**行为断言**（197 条，含循环用例）：注册表 / 快捷方式 / 计划任务名 / 用户数据目录 / 安装目录内文件清单 / 旧版本残留清单安全阀、路径归一化与比较、微软签名判定，以及拿**仓库真实的** `installer/kachina.config.json` + `USER_AGREEMENT.txt` 跑 `resolve_agreement`（含「配置里的计划任务名与宿主 `Autostart.cs` 一致」这类接线断言） | cargo | 首次 ~15s，之后 ~0.4s |
| `front` | `utils/agreement.ts` + `types.ts` 的 `tsc --strict`；`installer/kachina/src` 下**全部** `.vue` 的 `@vue/compiler-sfc` 编译；`agreement.ts` 的 prettier 风格 | node + npm | 首次 ~10s，之后 ~2s |
| `host` | `src/Host` 的 `dotnet build -c Release -p:EnableWindowsTargeting=true` | .NET 9 SDK | ~2–8s |
| `hosttest` | Host 的**行为断言**（自包含测试台，不依赖 xunit/MSTest）：`ProcessRunner` 的正常退出 / 非 0 退出码 / 超时杀进程树 / 启动失败 / 双管道并发读 / **孙进程继承管道写端时不干等**；`GameLocator` 的自定义目录快扫命中、系统目录与 installer 剪枝、`_Data` 在上一层的布局、同名假 exe 排除、主程序名判定、快捷方式目标反推。Linux/macOS 只验证测试台能编译，断言在 windows-latest 上真跑 | .NET 9 SDK | 首次 ~8s，之后 ~4s |
| `ci` | `tools/ci/Import-DevCmd.ps1` 的行为：用假 vcvarsall 输出跑一遍「生成 .cmd → 解析输出 → 注入环境 → 写 `GITHUB_ENV`」，并断言工作流里的 action 版本不低于 `installer/README.md` 登记的下限 | pwsh 7 | ~1s |
| `ui` | `src/Ui` 的 `vite build`（**不在 `all` 里**，要先 `cd src/Ui && npm install`） | node + npm | 视机器 |

全套热跑 ≈ 8–15 秒（`native` / `hosttest` 的断言依赖对应平台，缺平台时只做编译或直接 SKIP）。

## `-SelfTest` 的并发与清场

`-SelfTest` 会临时改写**仓库里的真实文件**（`.gitmodules`、工作流、`registry.rs`、
`ProcessRunner.cs` …）来验证各层真的会报错，因此它有两个保护：

1. **仓库改动锁**（`tools/devcheck/.repo-lock`）：同一工作区里同时只允许一个自检进程。
   第二个进程会明确报「另一个 devcheck 正持有仓库改动锁（PID …）」而不是互相污染出
   一堆假失败。锁是原子改名创建的，拿锁进程被杀也不会留下永久锁（下次运行会看到 PID
   不在了，删锁继续）。
2. **磁盘备份 + 启动清场**（`tools/devcheck/.selftest-backup/`）：每次注入前把原始内容
   落盘。进程被杀（Ctrl+C、CI 取消）后，下次运行会先按备份恢复被改的文件再开工；
   正常结束时备份目录会被删掉。`ci` 探测脚本的临时文件也带进程唯一后缀，两个 devcheck
   并行跑普通检查（非自检）时不再互抢文件。

两个目录都在 `.gitignore` 里。

## `vendor` 层：kachina 只从本仓库拉

`installer/kachina/` 是上游 kachina-installer 的**源码快照**，构建必须完全基于它。
这一层把这条约束变成可执行的断言（九项，任何一项不满足就失败）：

1. 仓库根不存在 `.gitmodules`（kachina 不是 submodule）
2. 快照完整：`package.json` / `pnpm-lock.yaml` / `src-tauri/Cargo.toml` /
   `src-tauri/Cargo.lock` / `uninstall.rs` / `pack.rs` / `App.vue` /
   `installer/build-kachina.ps1` / `installer/pack.ps1` 都在
3. `.github/workflows/*.yml`、`installer/*.ps1`、`build.ps1` 里**没有任何**从外部拉取的
   动作：`YuehaiTeam`、`kachina-installer.git`、`releases/download`、`release-downloader`、
   `git clone`、`git submodule`、`Invoke-WebRequest`、`Invoke-RestMethod`、`DownloadFile`、
   `curl`、`wget`（只扫可执行内容，`#` 注释行与 `<# #>` 块跳过）
4. `build.yml` 确实调用 `installer/build-kachina.ps1`，且 kachina 缓存 key 基于
   `hashFiles('installer/kachina/**')`
5. kachina 的每个 `git = "..."` cargo 依赖都在 `Cargo.lock` 里锁到 40 位 commit
   （否则 CI 可能拉到漂移的分支），且没有一个指向上游仓库
6. kachina 的 npm 依赖全部是 registry 版本，没有 `git:` / `http:` / `github:` /
   `file:` / `link:` 形式
7. `rcedit-rs` 的 vendored 副本完整（10 个文件 + 两份 LICENSE），`rescle.cc` 里
   没有 `std::locale::empty(`（MSVC 14.51 / VS 2026 已移除该非标准扩展，
   `windows-latest` 上必然 `error C2039`），kachina 的 `rcedit` 依赖是 `path` 形式，
   `Cargo.lock` 里不再出现 `git+https://github.com/Devolutions/rcedit-rs`
   —— 详见 `installer/kachina/vendor/rcedit-rs/LOCAL_PATCHES.md`
8. **遥测已物理移除，不许回归**（上游把安装器错误上报到 Sentry，前端还往
   `77.cocogoat.cn` POST 使用事件；本项目两条通道都拔了，见
   `installer/kachina/LOCAL_PATCHES.md` 第 7 节）。四组断言：
   - `src-tauri/src/utils/sentry.rs` 不许再出现；
   - `Cargo.toml` 不许再声明 `sentry` / `sentry-tracing` / `whoami`，`Cargo.lock` 里
     不许再锁 `sentry*` / `whoami` / `hostname` / `os_info` / `debugid`（依赖删了但
     lock 没重新生成时这条会响）；`package.json` 不许有 `@sentry/*`，
     `pnpm-lock.yaml` / `pnpm-workspace.yaml` 里不许有 `@sentry/` 条目；
   - 77 个 Rust + 前端源文件**剥掉行注释**后不许出现 `sentry::` / `sentry_tracing` /
     `capture_anyhow` / `add_breadcrumb` / `start_transaction` / `configure_scope` /
     `sendInsight` / `getInsightBase` / `evCache`；
   - 兜底：112 个文本文件（`.rs/.ts/.vue/.js/.json/.toml/.yaml/.html/.css/.lock/...`，
     不含 `.md`）里不许出现上报域名 `cocogoat`（Sentry DSN 与统计端点都带它）。
9. **写进 ARP 的卸载命令行必须能跑**：kachina 本体不在 devcheck 的编译范围内，
   `clap` 的选项名写错没有任何一层会发现，而传一个不存在的选项 = clap 以退出码 2
   报「unexpected argument」，卸载一步都不跑。两组断言：
   - `UninstallString` 的值必须整体被引号包住（默认装在 `Program Files\` 下，
     不加引号时「应用和功能」会按第一个空格把命令截断成 `C:\Program`）；
   - `QuietUninstallString` 里出现的每个 `-x` / `--xxx` 都要在 `src/cli/arg.rs` 里
     真的声明过（短名取 `short = 'X'`，长名取 `long = "xxx"` 与「只写 `long`、
     由字段名推导」两种），且至少要有一个选项 —— 否则它跟 `UninstallString` 没区别，
     静默卸载会弹界面。这条是真踩出来的：第一版写了 `--uninstall --silent
     --non-interactive`，而 `arg.rs` 里这几个 flag 只声明了 `short`（`-U` / `-S` / `-I`）。

第 5、7、8 项扫 `Cargo.toml` / `rescle.cc` / 源码时都会**先剥掉注释**：这些文件里的注释
本身就会写出「原为 `git = "...rcedit-rs.git"`」「原为 `std::locale::empty()`」
「上游挂在 `utils/sentry.rs` 里」这类说明文字，不剥掉就会自己误报自己。第 8 项的域名
兜底还额外**跳过 `.md`** —— `LOCAL_PATCHES.md` 与本文件需要能把被删掉的 DSN 写清楚。

> CI 仍然会联网取 crates.io / npm registry / rustup 工具链 / marketplace action ——
> 那是任何构建都免不了的；这一层保证的是**kachina 本身**只来自本仓库。

## `-SelfTest`：证明这套检查不是空壳

检查工具最大的风险是「跑通了但其实什么都没查」。`-SelfTest` 会先正常生成一次，
然后注入 15 个错误，逐个确认对应层会失败。其中 5 个只动**生成物**，10 个会临时创建/改写
仓库内的文件（`.gitmodules`、一个假工作流、`registry.rs` 的 `QuietUninstallString`、
`rescle.cc` 末尾一行、`utils/mod.rs` 末尾一行 `sentry::init`、`Cargo.toml` 末尾一行
`sentry = {…}`、一个带 DSN 域名的临时 `.ts`、`tools/ci/Import-DevCmd.ps1` 的解析正则、
`ProcessRunner.cs` 的超时分支、`GameLocator.Helpers.cs` 的剪枝表），每个用例跑完立即还原，
收尾再兜底删一次：

| 注入 | 期望 |
| --- | --- |
| 临时创建 `.gitmodules` | `vendor` 层报错（kachina 不能是 submodule） |
| 临时创建 `.github/workflows/zz-devcheck-selftest.yml`（内含 `Invoke-WebRequest` 下载 builder） | `vendor` 层报错（工作流不许从外部拉） |
| `registry.rs` 的 `QuietUninstallString` 改成 `--uninstall --silent --non-interactive`（`arg.rs` 里没有这些长名） | `vendor` 层报错（静默卸载会以退出码 2 失败） |
| `rescle.cc` 末尾追加一行真代码 `std::locale(std::locale::empty())` | `vendor` 层报错（MSVC 14.51 编不过） |
| `src-tauri/src/utils/mod.rs` 末尾追加 `fn _devcheck_selftest_telemetry() { sentry::init(…) }` | `vendor` 层报错（遥测不许回来） |
| `src-tauri/Cargo.toml` 末尾追加 `sentry = { version = "0.37", … }` | `vendor` 层报错（遥测依赖不许回来） |
| 新建 `src/devcheck-selftest-telemetry.ts`，内含 `steambird.cocogoat.cn` 的 DSN | `vendor` 层报错（上报域名不许回来） |
| `tools/devcheck/_selftest/broken.ps1`（`if` 少了右括号） | `ps1` 层报错 |
| `gen/uninstall.rs` 末尾追加 `let _x: u32 = "不是数字";` | `rust` 层报错 |
| `gen/extracted.rs` 里把 `segments.len() >= 2` 改成 `>= 1`（不是 `>= 0`，见下） | `logic` 层断言失败 |
| `gen/src/utils/agreement.ts` 末尾追加 `const x: number = 'not a number';` | `front` 的 tsc 报错 |
| `front/_selftest/Broken.vue`（`<div>` 未闭合） | `front` 的 SFC 编译报错 |
| `tools/ci/Import-DevCmd.ps1` 的解析正则改成永不匹配 | `ci` 层报错（解析不出任何环境变量，MSVC 注入失效） |
| `ProcessRunner.cs` 超时分支里的 `KillTree` 拿掉 | `hosttest` 层报错（超时的子进程会活到写出标记文件），windows-latest 上真跑 |
| `GameLocator.Helpers.cs` 剪枝表里删掉系统目录那一行 | `hosttest` 层报错（`Windows` / `System32` 不再被剪枝），windows-latest 上真跑 |

跑完自动删掉临时目录/临时文件并重新生成干净的检查源（用 `git status` 可验证零残留）。
任何一个「注入了却没报错」→ 退出码 1。

## 实现方式（为什么这样能代表真实构建）

```
tools/devcheck/
├── devcheck.ps1            入口：参数 / 常量 / -Fix / 分层执行 / 汇总
├── lib/Common.ps1          基础设施：Get-Tool / Write-* / Invoke-Layer / Invoke-Native
├── lib/Layers.ps1          各层实现（Test-VendoredSource、Test-Ps1Syntax、New-GenSources、Test-Rust*/Native/Frontend/Host/Ui）
├── lib/SelfTest.ps1        -SelfTest：往生成物注入错误，验证每层真的会报错
├── lib/RustSource.ps1      Rust 源码抽取：先把字符串与注释「挖空」，再做括号配对定位 item 边界
├── lib/Generate.ps1        生成两个 crate 的 src/gen 与 front/gen（含要抽取的 item 清单）
├── rust/typecheck/         整文件类型检查 crate（真实依赖，Windows target）
│   ├── Cargo.toml          依赖版本与 kachina src-tauri/Cargo.toml 对齐
│   └── src/lib.rs          把生成文件挂到上游的模块路径上 + 2 个最小桩
├── rust/logic/             行为断言 crate（mock windows-registry，跨平台）
│   └── src/main.rs         197 条断言 + mock
├── front/                  package.json / tsconfig.json / sfccheck.mjs
├── hosttest/               宿主行为断言：自包含测试台（无 xunit/MSTest）+ 引用 src/Host
└── rust/native/target/     native 层的 CARGO_TARGET_DIR（运行时生成，已 gitignore）
```

- **`typecheck`**：`gen/uninstall.rs` 是上游文件的**逐字节复制**，唯一改动是把
  `#[tauri::command]` 那一行换成注释（本 crate 不依赖 tauri）。`lib.rs` 只提供两个桩：
  `dfs::InsightItem`（字段与上游一致）、`local::get_base_with_config`（返回一个
  `AsyncRead`）。**桩与上游签名不一致时会直接编译失败**，所以上游改了这些接口
  devcheck 会立刻报警。
  （这里原来还有第三个桩 `sentry::capture_anyhow`：上游 `error.rs` 序列化错误时会顺手
  上报 Sentry，`super::sentry` 指向 crate 根。本项目已把遥测连依赖一起删掉，
  `error.rs` 里那句调用也没了，桩随之删除，`uuid` 依赖也一并去掉。）
- **`logic`**：只 mock 两样东西 —— `windows_registry`（记录调用，用来断言
  「删了什么 / 没删什么」）和 `has_reparse_point` / `is_under_system_root`
  （Windows 专有 API，换成按路径名触发的桩：路径含 `REPARSE` 视为符号链接，
  含 `/windows/` 视为系统目录）。其余都是上游/本项目的真实代码。
- **抽取用括号配对而不是行号切片**：上游在文件里增删别的函数不会影响结果；
  但清单里的 item 一旦改名/删除，`Get-RustItem` 会**抛错**而不是静默少测。
- **`hosttest` 为什么不用 xunit / MSTest**：`dotnet test` 要还原 nuget 包，而 devcheck
  的定位是「几分钟内出结论的快速体检」，不该为几个断言多一个外网依赖。测试台自己
  实现汇总与退出码；每个可能挂起的用例都用 `Task.Wait(上限)` 兜住，保证「被测的
  挂起操作」不会把测试进程一起拖死。`InternalsVisibleTo` 只在 Debug 配置下挂到宿主
  工程，Release 产物不带测试后门。

## 覆盖范围（诚实地说）

**能抓到**：kachina 被改成从上游拉取（submodule / 下载二进制 / git clone）、
快照文件缺失、git 依赖没锁 commit、vendored `rcedit-rs` 被改回 `locale::empty()`、
**遥测被加回来**（Sentry 依赖 / 调用、`@sentry/cli`、上报域名，含 lock 没跟着重新生成）、
vendored C++ 在当前 MSVC 下编不过（`native` 层，仅 Windows）、
Rust 类型/借用/生命周期错误（含 `std::os::windows`、`windows`、
`windows-registry` 的 API 误用）、`uninstall.rs` 里安全阀逻辑被改坏、
协议内联（`resolve_agreement`）行为变化、TS 类型错误、`.vue` 模板/`<script setup>`
语法错误、C# 编译错误与警告、**`ProcessRunner` 的退出码 / 超时杀进程树 / 管道排空
上限**与 **`GameLocator` 的扫描剪枝与路径推导**（`hosttest` 层，仅在 Windows 上真跑
断言）、`.ps1` 语法错误、
我们维护文件的格式漂移。

**抓不到**（这些还得靠真实构建 / 实机）：

- `#[tauri::command]` 宏展开、IPC 参数名与前端 `invoke` 的对齐
- 上游 npm/cargo 依赖自身的供应链问题（只检查「来源形式」与「是否锁版本」）
- `builder/pack.rs` 除 `resolve_agreement` 之外的部分（依赖 builder 的一大堆模块）
- `utils/secure_temp.rs` 除签名判定之外的部分（落地目录、独占创建、PowerShell 验签
  都是 IO）。本轮另搭最小 crate 在 msvc 上类型检查过，**没有实机跑过**：验签的证书
  Subject 布局要实机确认
- `utils/acl.rs` 的 SDDL 改动是否真的还能让提权流程连上管道 —— 只有实机安装能验证
- kachina 其余 Rust 模块（`installer/lnk.rs`、`dfs.rs`、`local.rs`、`module/wv2.rs`、
  `cli/mod.rs`、`main.rs` …）
- CI 用的 `x86_64-win7-windows-msvc` 自定义 target + `-Z build-std`（这里用标准
  `x86_64-pc-windows-msvc`，能覆盖绝大多数编译错误，但不是同一个 target）
- `src/Stub` / `src/StubStarRail` 的 C++（`native` 层只编 vendored `rcedit-sys` 的 C++）、
  任何**运行期**行为（注册表真的删没删、UAC、符号链接属性位）
- `.vue` 里的**类型**错误（SFC 编译只查语法；完整类型检查要 `vue-tsc` + kachina 全部依赖）

## 跨平台的坑（都在 CI 上真实踩过，别再踩一遍）

- **`Invoke-Native` 必须并发读 stdout 和 stderr。** 先 `ReadToEnd()` stdout、再读 stderr
  的串行写法在 Windows 上会**死锁**：Windows 命名管道缓冲区只有约 4 KB，而
  `cargo` / `dotnet` 把进度和诊断都写进 stderr，写满后子进程阻塞在 `write(stderr)`、
  父进程阻塞在 `read(stdout)`，两边永远互等（CI 表现为某一步卡住直到 job 超时）。
  Linux 管道缓冲是 64 KB，所以同一段代码在本地 Ubuntu 上「碰巧」跑得过 —— 本地复现：
  让子进程往 stderr 灌 300 KB 再写 stdout，串行版必挂。现在用两个
  `ReadToEndAsync()` 任务并发读，并带 `-TimeoutSec`（默认 600 秒）兜底：超时就
  `Kill($true)` 杀整个进程树并抛错，而不是无声地等到 CI 超时。
- **换行差异会让 `prettier --check` 只在 Windows 上失败。** windows-latest 的 git 默认
  `core.autocrlf=true`，检出时把 LF 换成 CRLF，而 prettier 2.x 起默认 `endOfLine: "lf"`，
  于是同一个提交 ubuntu 绿、windows 红，报的是
  `[warn] Code style issues found in the above file`（本地复现：把文件转成 CRLF 再
  `prettier --check`，加 `--end-of-line auto` 就通过）。真正的修法是仓库根的
  `.gitattributes`（`* text=auto eol=lf`，让所有平台都检出 LF，顺带让
  `hashFiles('installer/kachina/**')` 这类按工作区算的缓存 key 跨平台一致）；
  devcheck 里再传 `--end-of-line auto` 兜底，避免在没重新规范化的旧工作区上误报。
- **新版 MSVC 会删掉非标准扩展，vendored C++ 因此会突然编不过。** `native` 层就是为
  这件事存在的：它用 runner 上的 `cl.exe` 真编一遍 `installer/kachina/vendor/rcedit-rs`
  的 C++。触发这条坑的具体变更（哪个 MSVC 版本、上游哪个 PR、我们改的那一行）记在
  `installer/kachina/vendor/rcedit-rs/LOCAL_PATCHES.md`，代码里只留一句指针。
- **`Get-Tool` 在 Windows 上要避开 `.ps1` shim。** npm/npx 会同时装 `npm.cmd` 和
  `npm.ps1`，而 `ProcessStartInfo`（`UseShellExecute=false`）执行不了 `.ps1`，
  执行策略也可能拦；所以同名时优先 `.cmd`。
- `$IsWindows` 是 PowerShell 7 才有的自动变量，脚本要兼容 5.1 就用 `$env:OS -eq 'Windows_NT'`。
- `pwsh -File devcheck.ps1 -Layer a,b` 传进来的是**一个**字符串 `"a,b"`，
  不能靠 `[string[]]` + `ValidateSet` 拆开；参数声明成 `[string]` 再按 `[,\s]+` 手动 split。
- Rust 侧 `#[path]` 挂载点：把生成的文件挂到 crate 根（`#[path = "gen/x.rs"]`）再
  `pub use` 到目标命名空间。挂在内联 `pub mod` 里面时 rustc 会去找
  `src/<mod>/../gen/x.rs`，中间目录不存在就 ENOENT；而且挂在 crate 根意味着
  生成文件里的 `super::` 指向 crate 根，不是它「逻辑上」的上游模块路径
  （上游 `error.rs` 里那句 `super::sentry::capture_anyhow` 的桩当年就得放在 crate 根；
  遥测移除后这个例子没了，但挂载点的规律不变）。

## 日志里哪些 `Warning` / `error` 是正常的

全绿的一遍跑完，日志里仍然会出现下面这些字样，它们**不是**故障：

| 出现位置 | 字样 | 为什么正常 |
| --- | --- | --- |
| `logic` 层末尾 | `Warning: failed to read agreementFile ".../NO_SUCH_FILE.txt"` | 反例用例：协议文件缺失时 `resolve_agreement` 必须告警且不写出 `content`（前端链接保持不可点）。紧邻上一行有「（预期告警 ↓ …）」标注 |
| `-SelfTest` | `error[E0308]` / `error TS2322` / `Element is missing end tag` / `Missing closing ')'` | 每个用例故意注入的错误，被抓到才说明这层没被架空。每个用例前有「注入 N/15：…」横幅 |
| `rust` / `logic` 层 | `Agreement embedded: ".../USER_AGREEMENT.txt"` | 正常路径的信息输出，说明协议真的被读进来并内联了 |

已经消掉的噪音（别再把它们加回来）：

- node 自身的 `[DEP0040] punycode` / `[DEP0169] url.parse()`：npm、npx 内部用的，
  跟本仓库无关 → `devcheck.ps1` 开头设 `NODE_NO_WARNINGS=1`，子进程继承。
- `git init` 的 8 行 `hint: Using 'master' as the name for the initial branch...`：
  `actions/checkout` 自己 `git init` 打的 → 工作流在 checkout 之前先
  `git config --global init.defaultBranch main`。
- `-SelfTest` 注入 `segments.len() >= 0` 时编译器额外打的
  `warning: comparison is useless due to type limits`：`usize >= 0` 恒真才会有这条，
  改成注入 `>= 1`（同样是真放宽，`"Software"` 这种单段键会被放过，断言照样抓到）。
- stdout / stderr 错位：`Invoke-Native` 并发读两个流（否则 Windows 上死锁），
  读完再拼接，所以 stderr 一律排在 stdout 后面。两边都非空时插一行
  `──── 以上 stdout / 以下 stderr（顺序不代表先后） ────`，避免把末尾那段
  stderr 误读成「跑完之后又出事了」。

## 维护约定

- 在 `uninstall.rs` 里新增/重命名安全阀函数 → 同步 `lib/Generate.ps1` 的
  `$script:LogicItems` 清单，并在 `rust/logic/src/main.rs` 里补断言。
- kachina 升级依赖版本（`Cargo.toml`）→ 同步 `rust/typecheck/Cargo.toml`，
  否则类型检查结论不可信。
- 上游改了 `dfs::InsightItem` / `local::get_base_with_config` 的签名 → `rust` 层会
  编译失败，按报错改 `rust/typecheck/src/lib.rs` 里的桩即可。
- kachina 的 `Cargo.toml` / `package.json` 改了依赖 → **必须重新生成对应的 lock**
  （`cargo metadata` / `pnpm install --lockfile-only`），否则 CI 的 `--locked` /
  `--frozen-lockfile` 会直接失败；`vendor` 层第 8 项也会盯着 lock 里的遥测条目。
- 生成物（`*/src/gen/`、`front/gen/`、`_selftest/`）不入库，见 `.gitignore`。

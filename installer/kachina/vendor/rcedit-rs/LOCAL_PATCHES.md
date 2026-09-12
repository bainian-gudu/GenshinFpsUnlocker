# vendored：Devolutions/rcedit-rs

| | |
| --- | --- |
| 上游 | <https://github.com/Devolutions/rcedit-rs> |
| 快照 commit | `1bfa3ee6da2092b9b0c93148ce1455c5d45c538c`（2025-10-29，当时的最新提交） |
| 许可 | `LICENSE`（rcedit-rs，MIT）+ `LICENSE.rcedit`（原始 rcedit / rescle，Copyright (c) 2013 GitHub Inc.，MIT）——两份都原样保留 |
| 谁在用 | kachina 的 `src-tauri/Cargo.toml`：`rcedit = { version = "0.1.0", path = "../vendor/rcedit-rs" }`，用来给生成的 exe 写图标 / 版本资源 |

## 为什么要 vendor（原来是 git 依赖）

`rcedit-sys` 里有两个 C++ 源文件（`src/rescle.cc`、`src/librcedit.cpp`），
由 `build.rs` 通过 `cc` 调 MSVC 编译。`rescle.cc:87` 原本写的是：

```cpp
wif.imbue(std::locale(std::locale::empty(), new std::codecvt_utf8<wchar_t>));
```

`std::locale::empty()` 是 **MSVC 的非标准扩展**（不在 C++ 标准里，GCC/Clang 都没有）：

- VS 2022 17.14（2025-05）起弃用 —— [microsoft/STL#5197]
- **MSVC 14.51 起彻底移除** —— [microsoft/STL#5834]（2025-11-12 合并），
  现在 `<xlocale>` 里那句声明只在 `#ifdef _CRTBLD`（构建 CRT 自身）时才存在，
  **没有任何开关能把它打开**

而 `windows-latest` runner 现在装的是 VS 2026 Enterprise / MSVC 14.51.36231，于是：

```
rcedit-sys/src/rescle.cc(87): error C2039: 'empty': is not a member of 'std::locale'
rcedit-sys/src/rescle.cc(87): error C3861: 'empty': identifier not found
error: failed to run custom build command for `rcedit-sys v0.1.0 (https://github.com/Devolutions/rcedit-rs.git#1bfa3ee6)`
```

上游最新提交就是 2025-10-29 那次 CODEOWNERS 变更，**没有修这个问题**，
等上游不现实；而本项目对 CI 的要求是「只从本仓库构建」，所以直接 vendor 进来自己修。

[microsoft/STL#5197]: https://github.com/microsoft/STL/pull/5197
[microsoft/STL#5834]: https://github.com/microsoft/STL/pull/5834

## 与上游的差异（一共两处）

1. **`rcedit-sys/src/rescle.cc`**：`std::locale::empty()` → `std::locale::classic()`（1 行 + 注释）。
   `ReadFileToString()` 的意图只是给 `wifstream` 装一个 `codecvt_utf8` facet
   （在 `rescle.cc:758` 用来读 application manifest 文本），基准 locale 用哪个都不影响转换结果；
   `classic()` 是标准里的 "C" locale，且不受 `locale::global()` 影响，行为确定。
   该函数只在设置 manifest 时才会被调用，本项目不走那条路径，但代码必须能编过。
2. **`Cargo.toml`（根 = `rcedit` 包）**：删掉 `[dev-dependencies] tempfile = "3.1"`。
   上游的 `tests/`（含 `fake_resources_binary.exe` 等二进制 fixture）没有一起 vendor，
   留着这行只会让 `tempfile` 被解析进 kachina 的 `Cargo.lock`。

其余文件（`src/lib.rs`、`rcedit-sys/{Cargo.toml,build.rs}`、
`rcedit-sys/src/{lib.rs,librcedit.cpp,rescle.h}`、两份 LICENSE、上游 README）与上游逐字节一致。
上游的 `Cargo.lock`、`.gitignore`、`tests/`、`mock_resources_binary/`、`.github/` 没有 vendor。

## 升级 / 校验

```powershell
# 单独编这个 -sys crate（需要 Windows + MSVC 开发环境，即 cl.exe 在 PATH 上）
cargo build --manifest-path installer/kachina/vendor/rcedit-rs/rcedit-sys/Cargo.toml

# 或者直接跑 devcheck 的 native 层（没有 cl.exe 时自动 SKIP）
pwsh tools/devcheck/devcheck.ps1 -Layer native
```

若要跟进上游新版本：重新拉取上游对应 commit，覆盖这些文件，
然后**重新套用上面第 1 处修改**（只要上游还在用 `std::locale::empty()` 就必须改），
并同步 `src-tauri/Cargo.lock`（path 依赖不应再有 `source = "git+..."` 行）。

`pwsh tools/devcheck/devcheck.ps1 -Layer vendor` 会自动断言：
副本的 10 个文件都在、`rescle.cc` 里没有 `locale::empty(`、
kachina 的 `rcedit` 依赖是 path 形式、`Cargo.lock` 里不再出现该 git 源。

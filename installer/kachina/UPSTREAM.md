# Vendored: kachina-installer

本目录是 **第三方上游源码的快照 + 本项目的最小化本地修改**（见
[`LOCAL_PATCHES.md`](LOCAL_PATCHES.md)）。

| 项目 | 值 |
| --- | --- |
| 上游仓库 | https://github.com/YuehaiTeam/kachina-installer |
| 固定版本 | tag `0.5.1` |
| 上游 commit | `ae461aa9ddd5a8f5e445459e14f5d611921f9938` |
| 快照日期 | 2026-09-12 |
| 用途 | 构建 `kachina-builder.exe`，用于打包本项目的安装器 / 更新器 / 卸载器 |

## 为什么把源码放进仓库

安装器只允许 Kachina 一种实现（不再有 `--install` / `--uninstall` / `Uninstall.cmd`
等宿主自带的安装卸载路径）。把打包工具源码放进仓库后：

- CI 不再依赖上游 Release 的可下载性（`robinraju/release-downloader` 已移除）；
- 打包工具版本与本项目一起进版本控制，可复现、可审计；
- 本地一条命令即可从零构建：`pwsh installer/build-kachina.ps1`。

## 本地修改（重要）

本目录**不是纯净快照**。为满足本项目需求做了几处改动（编号与
[`LOCAL_PATCHES.md`](LOCAL_PATCHES.md) 一致），另有 3 处新增
（`src/utils/agreement.ts`、`vendor/rcedit-rs/`）与 1 处删除
（`src-tauri/src/utils/sentry.rs`）：

1. **卸载器额外注册表清理**：新增配置项 `extraUninstallRegistry`，
   卸载时删除安装期写入的注册表（本项目的开机自启
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`）；`HKCU` 会额外遍历
   `HKEY_USERS` 下已加载的用户配置单元，避免提权卸载时删错账户。
1b. **卸载器清理宿主自建/改名的快捷方式**：新增配置项 `extraUninstallLnkNames`。
2. **用户协议可配置 + 多格式 + 弹窗**：新增配置项
   `agreementFile` / `agreementFormat`（`text` / `markdown` / `html`）/
   `agreementTitle`，打包时把协议正文内联进 exe；安装界面原本的死链接
   「用户协议」改为点击弹窗显示全文（正文统一经 DOMPurify 净化）。
3. **安全加固**：收敛卸载器的删除范围与提权面（`is_safe_*` 系列安全阀 +
   受保护根目录 / Shell 容器黑名单）。
4. **MSVC 14.51（VS 2026 / `windows-latest`）兼容**：`rcedit` 从 git 依赖改为仓库内
   vendored 副本，并修掉 `rescle.cc` 里的 `std::locale::empty()`。
5. **弹窗布局**：footer 按钮回到文档流，协议全文不会被遮住。
6. **卸载残留清理**：`%VAR%` 展开、遍历所有登录过的用户、`%TEMP%` 安装期产物白名单、
   卸载前先结束运行中的主程序。
7. **移除全部遥测**：上游的 Sentry 错误上报（DSN 主机 `steambird.cocogoat.cn`）与
   前端使用统计（`77.cocogoat.cn/ev`）连依赖一起删除，`@sentry/cli` 一并去掉，
   两个 lock 重新生成。本项目是个人自用构建，不做任何统计。

逐文件的改动位置、原因与升级套用顺序见 [`LOCAL_PATCHES.md`](LOCAL_PATCHES.md)。
第 1～6 项都是「加字段 / 加分支 / 加样式覆盖」，不写这些配置项时行为与上游完全一致；
**第 7 项是删除型改动**，升级上游时必须重做（上游几乎一定会带着 Sentry 回来），
`tools/devcheck` 的 `vendor` 层第 8 组断言会在它回来时直接失败。

## 构建产物

`installer/build-kachina.ps1` 会产出：

```
installer/tools/kachina-builder.exe
```

它是上游 `pnpm build` 的结果 —— 即 `kachina-builder-standalone.exe` 与
`kachina-installer.exe` 的二进制拼接（上游 `package.json` 的 build 脚本行为），
一个文件同时包含「打包器 CLI」与「安装器 GUI 模板」。该目录已被 `.gitignore`
排除，不进版本库。

## 构建前置

- Rust **nightly**（上游 `src-tauri/rust-toolchain.toml` 指定）+ `rust-src` 组件
  （`-Z build-std=std,panic_abort` 需要从源码编译标准库）
- 目标三元组 `x86_64-win7-windows-msvc`
- Node.js 20+ 与 pnpm 10（上游 `packageManager: pnpm@10.17.0`）
- MSVC 生成工具（`crt-static` 链接参数见 `src-tauri/.cargo/config.toml`）
- 仅在 Windows 上构建（上游依赖 `windows` / `win32-version-info` / `mslnk` 等 crate）

首次冷构建耗时较长（`lto = true`、`codegen-units = 1`、静态 msquic）。
CI 里用 `Swatinem/rust-cache` 缓存后通常几分钟内完成。

## 许可提示（重要）

截至快照日期，**上游仓库没有提供 LICENSE 文件**，`package.json` / `Cargo.toml`
里也没有 `license` 字段。按著作权法默认规则，这属于「保留所有权利」。
本目录作为构建工具的源码快照引入，并在其上有本地修改（`LOCAL_PATCHES.md`）；
如需对外分发本仓库，请先向上游确认授权（含修改与再分发的权利），
或改为 git submodule / CI 下载官方 Release 二进制的方式（那样就需要放弃这两项
本地增强，改用外部手段实现；同时要改 `tools/devcheck` 的 `vendor` 层 ——
它现在会把这两种做法判定为失败）。

## 升级上游版本

> **这是人工在本地做的操作，CI 永远不会执行它。**
> 构建（本地 `installer\build-kachina.ps1` 与 CI 的 `build-kachina` job）只用
> 本目录里已经在版本库中的源码；工作流与打包脚本里不允许出现任何从上游拉取的
> 动作，`pwsh tools/devcheck/devcheck.ps1 -Layer vendor` 会断言这一点。

```powershell
# 在仓库外克隆上游，切到目标 tag，再整体覆盖本目录
git clone https://github.com/YuehaiTeam/kachina-installer.git $env:TEMP\kachina
git -C $env:TEMP\kachina checkout <tag>
Remove-Item installer\kachina -Recurse -Force
Copy-Item $env:TEMP\kachina installer\kachina -Recurse -Force
Remove-Item installer\kachina\.git, installer\kachina\.github, installer\kachina\.vscode -Recurse -Force
# 然后更新本文件的 tag / commit / 日期
```

覆盖会连带删掉本地修改，因此升级后必须按
[`LOCAL_PATCHES.md`](LOCAL_PATCHES.md) 的「升级上游时的套用顺序」重新套用改动，
并把 `LOCAL_PATCHES.md` / `UPSTREAM.md` 两个文件恢复回来（它们在覆盖前可以先备份，
或用 `git checkout HEAD -- installer/kachina/LOCAL_PATCHES.md installer/kachina/UPSTREAM.md`）。

> CI 的 `build-kachina` 缓存 key 是 `hashFiles('installer/kachina/**')`，
> 改动本目录任意文件都会自动触发重建，不需要手动清缓存（也可用
> `rebuild_kachina` 输入强制重建）。

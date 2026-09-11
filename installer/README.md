# 安装

## 唯一安装方式：图形安装器

```powershell
.\build.ps1 -Configuration Release
.\dist\Setup\GenshinFpsUnlocker.Setup.exe
```

或：

```powershell
.\build.ps1 -Configuration Release -Install
```

### 界面

| 步骤 | 内容 |
|------|------|
| 欢迎 | 说明 |
| 选项 | **选择安装目录**、桌面图标、Defender、自启、装完运行 |
| 安装中 | **进度条** + 当前文件 + **滚动列表**（文件名与目标目录） |
| 完成 | 显示安装路径 |

底部按钮：**上一步** / **下一步（安装）** / **取消**（安装中可取消）。

窗口固定大小，文件再多也只在列表内滚动，不会把界面撑大。

### 静默（可选）

```text
GenshinFpsUnlocker.Setup.exe --quiet [--dir "D:\Apps\GenshinFpsUnlocker"] [--no-run] [--no-desktop] [--autostart]
```

### 权限

| 场景 | UAC |
|------|-----|
| 运行安装器 | 一次（写安装目录） |
| 日常 / 开机自启 | 无 |

### 卸载

开始菜单 →「卸载 原神帧率解锁」，或运行已安装目录中的主程序：

```text
GenshinFpsUnlocker.exe --uninstall
```

### 布局

```
dist\Setup\
  GenshinFpsUnlocker.Setup.exe
  Payload\          ← 主程序与依赖
    GenshinFpsUnlocker.exe
    FpsUnlockerStub.dll
    ...
```

源码：`src/Setup/`

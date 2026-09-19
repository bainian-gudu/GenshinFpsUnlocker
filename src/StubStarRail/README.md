# StarRailStub.dll 适配规格书（已实现，待 Windows 侧验证）

崩坏：星穹铁道的注入模块。宿主侧已经按「独立模块」接好，本文件记录**要做到什么、
怎么做、怎么验**，以及实现前为什么**刻意不塞一个空壳 DLL**。

## 〇、实现状态（2026-09-19）

源码已经落地，当前等待 Windows 侧的 MSVC 编译与游戏内验证：

| 文件 | 职责 |
| --- | --- |
| `dllmain.cpp` | 模块生命周期、共享内存连接、定位重试、状态机与错误码 |
| `Il2CppBridge.h/.cpp` | RVA / 特征码定位、函数头校验、自建 il2cpp string、Find / GetComponent、`m_Color` 读写、`SetVerticesDirty` 通知 |
| `AntiBlur.h/.cpp` | Hook `BaseShaderPropertyTransition` 的相机 Dither 入口，开启时把 Camera 来源 alpha 压回 1.0 |
| `HideUid.h/.cpp` | Hook `RPGApplication.OnUpdate`，主线程 tick 隐藏两条 UID 路径并支持恢复 |
| `CMakeLists.txt` / `StarRailStub.def` | 独立构建 `StarRailStub.dll`，导出 `WndProc` |

**解耦约定**：原神 `src/Stub` 与星铁 `src/StubStarRail` 各自拥有完整的业务代码，
互不引用；两者只共用 `src/Common` 下与游戏无关的 `Scanner` / `PatternMatch` /
`IpcData`。公共扫描器由原神侧 `Scanner.cpp` 迁移而来，原神构建改引用
`../Common/Scanner.cpp`，功能与特征码不变。

构建入口已接进根目录 `build.ps1`：原神 Stub 构建完成后，会在独立的
`out/stub-starrail` 目录构建 `StarRailStub.dll` 并拷贝到 `dist`。

错误码（Host 状态栏按十六进制显示）：

| 错误码 | 含义 |
| --- | --- |
| `0xE101` | 找不到 `GameAssembly.dll` |
| `0xE102` | `GameObject.Find` 定位失败 |
| `0xE103` | `GameObject.GetComponent` 定位失败 |
| `0xE104` | `RPGApplication.OnUpdate` 定位失败 |
| `0xE105` | 相机 Dither 汇合入口与距离 / 高度兜底入口全部定位失败 |
| `0xE106` | 反虚化 Hook 创建失败 |
| `0xE107` | UID 主线程 Hook 创建失败 |
| `0xE108` | `MH_EnableHook` 失败 |
| `0xE109` | 主线程 tick 踩到结构化异常 |
| `0xE10A` | MinHook 初始化失败 |
| `0xE10B` | 模块 PIN 失败 |

## 一、现状：缺这个模块会发生什么

- 宿主 `GameCatalog.StarRail.StubFileName = "StarRailStub.dll"`，只有在星铁档案里
  开启了画面效果（反角色虚化 / 隐藏 UID 水印）时才会去注入，并且注入前做可信度校验
  （`ModuleTrust`）。
- DLL 不在 exe 旁时：状态栏显示「缺少 StarRailStub.dll（应位于 …）」，**不注入任何东西**，
  游戏进程保持干净。
- **星铁的帧率解锁不依赖本模块**：走 `src/Host/StarRailFpsRegistry.cs` 直接改注册表，
  所以缺模块只影响上面那两项画面效果。

## 二、实现前为什么不塞一个空壳 DLL

星铁进程里有 `mhypbase.dll` 反作弊。往这种进程里注入，只有**真的能干活**才值得冒暴露风险：
空壳 DLL 收益为零、暴露面照旧。所以在本模块具备真实功能前，保持「缺失即不注入」。

## 三、目标产物

| 项 | 约定 |
| --- | --- |
| 文件名 | `StarRailStub.dll`（与 `FpsUnlockerStub.dll` 并列放在 exe 旁） |
| 位数 / 运行库 | x64，静态 CRT（`/MT`），MinHook 直接编入（与 `src/Stub` 一致） |
| IPC | 复用 `src/Common/IpcData.h`（保留历史兼容映射名，实际值见源码） |
| Host 写入 | `AntiBlurPerspective`、`HideUid`（外加协议里已有的其它字段） |
| Stub 写入 | `Status`（Waiting / Ready / Error / Exiting）、`AntiBlurState`（bit0 就绪）、`HideUidState`（bit0 就绪 / bit1 生效中）、`LastError` |
| 状态机 | 首轮定位全部成功才置 `Ready`；任何一步失败置 `Error` + 错误码，**绝不半开**（避免游戏侧出现「一半功能生效」） |

宿主不需要再改：DLL 到位即可用；缺失时的提示、退避重试、托盘就绪标记都已实现。

## 四、定位策略：dump.cs 定 RVA，特征码兜底

> 2026-09-19 实测（国服 4.5.0，`GameAssembly.dll` 536 MB / 2026-08-13 构建）：
> 用 `im-remi/HSRGlobalMetadata`（静态提取器，其测试版本 `OSPRODWin4.5.0` 与本机一致）
> 生成了完整 `dump.cs`（238 万行 / 128 MB）与 `stringliterals.json`（11 MB）。
> **下面所有偏移与 RVA 都来自这次 dump，不是猜的。**
>
> 两条死路先记下来：`GameAssembly.dll` 只导出 `il2cpp_get_api_table` 一个符号
> （RVA `0x644ba0`，ImageBase `0x180000000`）；`global-metadata.dat` 是**分区段加密**的
> （文件头是 `MHY\0`，标准 Il2CppDumper 直接报 "Metadata file not found or encrypted"）。

1. **主路线：直接用 dump.cs 里的 RVA**（`基址 + RVA`），并对目标地址做函数头字节
   校验（例如 `RPGApplication.OnUpdate` 必须仍以 `56 57 48 83 EC 48 …` 开头）。
   IL2CPP 的引擎方法在这里是 `jmp [rip+off]` 跳转桩，调用桩等于调用真实实现；
   函数头校验用于拦住「RVA 仍落在模块内、但已经指向无关函数」的版本更新场景。
2. **兜底：特征码唯一命中**。参考实现（30launchers）的两条特征码在 4.5.0 上仍然命中，
   但**必须选对那一处**：
   - `GameObject.Find(string)` → 命中 9 处，正确的是**第 4 个**（RVA `0x1DEDE300`）
   - `GameObject.GetComponent(string)` → 命中 8 处，正确的是**第 1 个**（RVA `0x1DEDDE30`）

   其余命中是 `Texture2D.SetPixels32`、`Animator.Play` 这类无关函数 ——
   **不要照搬参考实现「全部无差别调用」的做法**（它靠 SEH 硬试，有崩游戏的风险）。
   当前实现是：RVA 命中就直接用；RVA 失效时只接受特征码的**唯一命中**，
   9 处 / 8 处这种多命中场景一律判失败并报 `Error`。宁可让宿主提示版本未适配，
   也不调用无关函数。
3. 两条路都失败 → `Error`，卸载并退出工作线程。

## 五、功能 1：隐藏 UID 水印（建议先做，风险最低）

- 目标节点（**就这 2 条，按 2 条处理**）：
  - `/UIRoot/AboveDialog/BetaHintDialog(Clone)/Contents/VersionText`
  - `/UIRoot/Page/MobilePhoneMainPage(Clone)/Content/Content/LeftPlane/Tittle/UID/NumText`
- 范围说明：这 2 条分别对应主界面右下角版本号旁的 UID 与手机界面左上角的 UID。原神侧
  `src/Stub/HideUid.cpp` 同样是 2 条（拍照水印 + 资料页），参考实现 30launchers 的
  `Sr_adv_addon` 也只列这 2 条。客户端里若还有别的显示点，不在本次范围内 —— 先把这 2 条
  做稳（能隐藏、能恢复、重建 UI 后仍生效），要扩再单独提。
- 做法：取到 `UnityEngine.UI.Graphic` 组件，把 `color` 的 alpha 写 0
  （**偏移已复核**：`UnityEngine.UI.Graphic.m_Color // Offset: 0x20`，与参考实现的
  `GRAPHIC_COLOR_OFFSET = 0x20` 一致），随后调用 `Graphic.SetVerticesDirty`
  （RVA `0x1B78C0C0`）触发 UI 重建，让直接写入的字段立即生效。
  `SetVerticesDirty` 是可选辅助路径：定位失败时降级为只写字段，不让整个模块 Error。
- 触发：挂一个轻量入口按需刷新（例如 FOV 变化 / 场景切换后），并保存原 alpha，
  关闭开关时原样恢复。
- 不要用「关掉 `s_UICamera`」那种做法（Pipsi 的 `hide_ui.cpp`）：会把整个 HUD 一起藏掉。

## 六、功能 2：反角色虚化

早期实现曾 Hook `VCameraDOFEffectOverride`，但那是**场景景深 DOF**，不是
「角色靠近镜头变半透明」的机制；所以 UI 显示已开启，实际镜头拉近仍会透明。
Windows 侧实测也确认：同一 DLL 的隐藏 UID Hook 正常，排除注入与反作弊拦截。

真正的链路是 `RPG.Client.BaseShaderPropertyTransition` 的相机 Dither。
反汇编确认（dump.cs，4.5.0）：

```
public enum DitherSourcePriority {
    Default = 0,
    Camera  = 1,  // 相机碰撞 / 靠近角色虚化
    Logic   = 2,  // 剧情 / 任务淡入淡出
}

public class BaseShaderPropertyTransition : UnityEngine.MonoBehaviour {
    public float TargetDitherAlpha        // Offset: 0x20
    public DitherSourcePriority CurrentControlSource // Offset: 0x28
    public float ElevationDitherAlpha     // Offset: 0x2C
    public float DistanceDitherAlpha      // Offset: 0x30

    // 私有汇合入口：value / priority / force
    private bool HBPKIAAKMPE(float, DitherSourcePriority, bool) // RVA: 0x19F1BE00
    public void SetDistanceDitherAlphaValue(float, bool)        // RVA: 0x19F1C0E0
    public void SetElevationDitherAlphaValue(float)             // RVA: 0x19F1BD70
    public void ClearCameraDitherAlpha()                        // RVA: 0x19F1C810
}
```

- 首选：Hook `HBPKIAAKMPE`。所有距离 / 高度相机 Dither 都会汇入这里；
  仅当 `priority == Camera(1)` 且用户开关开启时，把 `value` 改成 `1.0`，
  再调用原函数。`Logic(2)` 与默认来源不受影响。
- 兜底：私有入口定位失败时，Hook `SetDistanceDitherAlphaValue` 与
  `SetElevationDitherAlphaValue`，同样只在开关开启时把入参改成 `1.0`。
- 关闭开关时不改写任何参数，完整保留游戏原始表现。

## 七、验证清单（需要 Windows + 星铁）

1. 不放 DLL：宿主状态栏显示「缺少 StarRailStub.dll」，游戏内无任何变化。
2. 放好 DLL + 只开「隐藏 UID 水印」：水印消失，关闭开关能恢复；游戏退出后 DLL 不在进程里。
3. 只开「反角色虚化」：镜头拉近角色不再透明化；切场景后仍有效。
4. 版本更新后再跑：定位失败要变成 `Error` 而不是崩游戏（宿主会显示错误码并按退避重试）。
5. 全程不修改游戏目录里的任何文件（只读 + 内存操作）。

## 八、风险与边界

- 星铁有 `mhypbase.dll` 反作弊；联机 / 千星奇域等玩法保持关闭。
- 默认关闭，只在用户显式开启时注入；失败即卸载，不做兜底 patch。
- 本模块只做「反角色虚化 / 隐藏 UID」两项，不碰帧率（帧率归注册表），也不碰存档与网络。

## 九、继续推进需要什么（一步采集）

需要一次可运行的 Windows + 星铁环境，采到目标版本的 IL2CPP 材料：
导出符号是否存在、类 / 字段真实偏移、调用点、UI 节点名。

静态部分已经采过一轮（落到工作区 `hsr-capture\`）：Unity 2019.4.34.11513818、
`StarRail.exe` 684 KB、`GameAssembly.dll` 536 MB、`global-metadata.dat` 100 MB
（两个大文件都已复制到 `bin\`，SHA256 与 `info.txt` 记录一致）、
`mhypbase.dll` 1.0.1.40399480（26 MB，在游戏根目录，不在 `StarRail_Data\Plugins` 下）。
注册表 `GraphicsSettings_Model_h2986158309` 实测是 `REG_BINARY` + UTF-8 JSON，当前 `"FPS":120`。

**模块基址采不到，而且不需要采**：游戏运行时实测，`Process.Modules` 返回空集合，
Toolhelp32 的 `CreateToolhelp32Snapshot` 报 `win32 error 5 (Access is denied)` ——
`HoYoProtect`（`C:\WINDOWS\system32\HoYoKProtect.sys`）内核驱动在运行，跨进程模块查询
一律被拒，提权也大概率无效。Stub 在游戏进程内用 `GetModuleHandle(L"GameAssembly.dll")`
自己取基址即可，不依赖这份数据。

**dump.cs 也已经拿到**（2026-09-19）：`global-metadata.dat` 被米哈游分区段加密，标准
Il2CppDumper 解不了（报 "Metadata file not found or encrypted"）；改用
`im-remi/HSRGlobalMetadata` 静态提取，它的测试版本 `OSPRODWin4.5.0` 与本机 4.5.0 正好对上，
产出 238 万行 / 128 MB 的 `dump.cs` 与 11 MB 的 `stringliterals.json`，
产物在工作区 `hsr-game-view/dump/`。

版本更新后重新提取（全程 WSL 内，不需要启动游戏）：

```bash
# 1) 装 .NET 10 SDK 到工作区（不需要 root）
./dotnet-install.sh --channel 10.0 --install-dir <tools>/dotnet

# 2) 把游戏目录映射成 WSL 侧视图，必须包含：
#    GameAssembly.dll
#    StarRail_Data/il2cpp_data/Metadata/global-metadata.dat
#    StarRail_Data/il2cpp_data/Metadata/startup-metadata.dat   <- 提取器两个都要

# 3) 跑提取器
dotnet run -c Release --no-build <game-view-dir>
# 产物：<game-view-dir>/dump/dump.cs、stringliterals.json
```

至此写 Stub 需要的材料已经齐了：目标偏移（`EnableDOF` 0x18、`m_Color` 0x20）、
目标方法 RVA、两条 UID 节点路径，以及两条特征码各自该选第几个命中。

采集用仓库自带的只读脚本，**不需要安装任何东西**（只用 Win10/11 自带的 Windows PowerShell 5.1）：

```
双击：tools\hsr-capture.cmd
或：  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<仓库>\tools\hsr-capture.ps1"
```

推荐先把游戏开到有 UID 水印的界面，再运行一次（这样能采到模块基址）。脚本只读，
不改游戏目录、不写注册表、不注入进程，结果落到仓库上一级的 `hsr-capture\`：

| 产物 | 用途 |
| --- | --- |
| `bin\GameAssembly.dll`、`bin\global-metadata.dat` | 离线分析：`objdump -p` 确认导出（只有 `il2cpp_get_api_table`）；Il2CppDumper 出 `dump.cs` 拿类 / 字段偏移与方法地址 |
| `modules.txt` | 模块列表（被 `HoYoProtect` 拒绝时记录 win32 错误码与提权状态）+ 反作弊驱动状态 |
| `registry.txt` | `GraphicsSettings_Model_h*` 的真实值名与 `FPS` 字段（核对注册表解锁实现） |
| `unity-log-tail.txt` | 确认 Unity 日志目录与 Unity 版本 |
| `info.txt` | 各文件版本 / 大小 / SHA256，用于判断适配的目标版本 |

实现顺序（已完成）：按第四节把 RVA / 特征码定位打通 → 隐藏 UID 水印 →
反角色虚化 → 按第七节清单在 Windows 上验收。

在那之前，「模块缺失即不注入」就是最稳的状态：帧率解锁照常可用，画面效果保持未就绪提示。

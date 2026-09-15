import { Check, ChevronRight, Download, FileJson, FolderOpen, Info, LoaderCircle, RotateCcw, Settings2, Shield, ShieldCheck, SlidersHorizontal, Sparkles, Trash2, Upload, WandSparkles } from 'lucide-react';
import { useEffect, useState } from 'react';
import type { KeyboardEvent as ReactKeyboardEvent } from 'react';
import type { LogLevel, UnlockerConfig, UpdateConfig, UpscalerQuality, UpscalerMode } from '../lib/config';
import type { UpscalerState } from '../lib/native';
import { FpsControl } from '../components/FpsControl';
import { PageHeading, ToggleRow } from '../components/ui';

function NumberSetting({ title, description, value, min, max, unit, onChange }: {
  title: string; description: string; value: number; min: number; max: number; unit: string; onChange: (value: number) => void;
}) {
  const [draft, setDraft] = useState(String(value));
  const [error, setError] = useState(false);
  useEffect(() => { setDraft(String(value)); setError(false); }, [value]);
  const commit = () => {
    const next = Number(draft);
    if (!draft || !Number.isInteger(next) || next < min || next > max) { setError(true); return; }
    setDraft(String(next)); setError(false); onChange(next);
  };
  return <div className="setting-row"><div><span className="row-title">{title}</span><p className={error ? 'field-error' : ''}>{error ? `请输入 ${min} 至 ${max} 之间的整数` : description}</p></div><div className="number-setting"><input aria-label={title} type="number" min={min} max={max} value={draft} aria-invalid={error} onChange={(event) => setDraft(event.target.value)} onBlur={commit} onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur(); }} /><span>{unit}</span></div></div>;
}

export function SettingsPage({ config, updateConfig, onPath, onExport, onImport, onReset, onUninstall, busy, isNative, isElevated, onRestartElevated, elevating, upscaler }: {
  config: UnlockerConfig; updateConfig: UpdateConfig; onPath: () => void; onExport: () => void; onImport: () => void; onReset: () => void; onUninstall?: () => void; busy: boolean; isNative?: boolean;
  isElevated?: boolean; onRestartElevated?: () => void; elevating?: boolean; upscaler: UpscalerState;
}) {
  const [tab, setTab] = useState<'game' | 'behavior' | 'advanced'>('game');
  const tabs = [{ id: 'game', label: '游戏与解锁', icon: SlidersHorizontal }, { id: 'behavior', label: '启动与行为', icon: Settings2 }, { id: 'advanced', label: '高级设置', icon: FileJson }] as const;

  function handleTabKey(event: ReactKeyboardEvent<HTMLDivElement>) {
    const current = tabs.findIndex((item) => item.id === tab);
    const next = event.key === 'ArrowRight' ? (current + 1) % tabs.length
      : event.key === 'ArrowLeft' ? (current - 1 + tabs.length) % tabs.length
      : event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : -1;
    if (next < 0) return;
    event.preventDefault();
    setTab(tabs[next].id);
    document.getElementById(`tab-${tabs[next].id}`)?.focus();
  }

  return <>
    <PageHeading title="游戏设置" description="按你的习惯，配置每一次启动。"><span className="autosave-label"><Check size={14} />更改自动保存</span></PageHeading>
    <div className="tabs" role="tablist" aria-label="设置分类" onKeyDown={handleTabKey}>
      {tabs.map(({ id, label, icon: Icon }) => (
        <button role="tab" key={id} id={`tab-${id}`} tabIndex={tab === id ? 0 : -1}
          aria-controls={tab === id ? `settings-${id}` : undefined} aria-selected={tab === id}
          className={tab === id ? 'active' : ''} onClick={() => setTab(id)}>
          <Icon size={16} />{label}
        </button>
      ))}
    </div>
    <div role="tabpanel" id={`settings-${tab}`} aria-labelledby={`tab-${tab}`} className="settings-tab-content" key={tab}>
      {tab === 'game' && <>
        <div className="settings-game-grid">
          <FpsControl value={config.targetFps} enabled={config.enabled} masterEnabled={config.masterEnabled} onChange={(value) => updateConfig('targetFps', value)} onToggle={(value) => updateConfig('enabled', value)} />
          <section className="control-panel settings-control"><div className="panel-heading"><h2><ShieldCheck size={18} />解锁行为</h2></div>
            <ToggleRow title="解锁服务总开关" description="关闭后不再注入、不再强制帧率，游戏会回到自身的帧率档位" checked={config.masterEnabled} onChange={(value) => updateConfig('masterEnabled', value)} />
            <ToggleRow title="自动解锁" description="检测到游戏启动后，自动应用帧率设置" checked={config.autoWatch} onChange={(value) => updateConfig('autoWatch', value)} />
            <p className="settings-small-note"><Info size={14} />总开关与帧率解锁同时开启时，目标帧率才会生效。</p>
          </section>
        </div>
        <section className="control-panel settings-control settings-antiblur"><div className="panel-heading"><h2><WandSparkles size={18} />画面效果注入</h2></div>
          <ToggleRow title="反角色虚化" description="开启后镜头拉近时，角色不再透明化（虚化效果被跳过）" checked={config.antiBlurPerspective} onChange={(value) => updateConfig('antiBlurPerspective', value)} />
          <ToggleRow title="移除水下马赛克" description="开启后角色入水时，不再显示马赛克虚化效果" checked={config.antiBlurDiveMosaic} onChange={(value) => updateConfig('antiBlurDiveMosaic', value)} />
          <p className="settings-small-note"><Info size={14} />两项功能随游戏进程注入即时生效。仅供单机体验，联机与千星奇域等玩法中请保持关闭；游戏版本更新后若未生效，请等待特征适配更新。</p>
        </section>
        <section className="control-panel settings-control settings-upscaler"><div className="panel-heading"><h2><Sparkles size={18} />超分辨率替换</h2><span className="feature-badge">实验组件</span></div>
          <ToggleRow title={`启用 FSR 2.0 → DLSS${config.upscalerMode === 'dlss5' ? ' 5' : ' 4'}`} description={upscaler.status} checked={config.upscalerReplacementEnabled} onChange={(value) => updateConfig('upscalerReplacementEnabled', value)} />
          <div className="setting-row upscaler-mode-row"><div><span className="row-title">DLSS 版本</span><p>{config.upscalerMode === 'dlss5' ? '超分辨率 + AI 神经渲染，提升光影与材质细节（支持 RTX 20/30/40/50）' : '标准 AI 超分辨率，兼容性最广，适用于所有 RTX 显卡'}</p></div><select className="select-input" aria-label="DLSS 版本" value={config.upscalerMode} onChange={(event) => updateConfig('upscalerMode', event.target.value as UpscalerMode)}><option value="dlss4">DLSS 4 · 超分辨率</option><option value="dlss5">DLSS 5 · 超分辨率 + 神经渲染</option></select></div>
          <div className="setting-row upscaler-quality-row"><div><span className="row-title">超分辨率挡位</span><p>选择 DLSS 输出质量；未设置游戏路径时仅保存偏好。</p></div><select className="select-input" aria-label="超分辨率挡位" value={config.upscalerQuality} onChange={(event) => updateConfig('upscalerQuality', event.target.value as UpscalerQuality)}><option value="nativeAA">DLAA</option><option value="quality">质量</option><option value="balanced">均衡</option><option value="performance">性能</option><option value="ultraPerformance">超高性能</option></select></div>
          <p className="settings-small-note"><Info size={14} />替换功能与帧率解锁、反虚化注入独立运行。切换 DLSS 版本需重启游戏才能生效。</p>
        </section>
        <section className="control-panel settings-path-panel"><div className="panel-heading"><h2><FolderOpen size={18} />游戏安装位置</h2><button className="text-button" onClick={onPath} disabled={busy}>更改路径<ChevronRight size={15} /></button></div><p className="path-display">{config.gamePath || '尚未设置游戏路径'}</p><p className="input-help">请选择游戏本体，而非米哈游启动器。支持国服和国际服客户端。</p></section>
      </>}
      {tab === 'behavior' && <section className="control-panel setting-list"><div className="section-intro"><h2>更安静，也更顺手</h2><p>让解锁器融入你的游戏习惯，无需每次重复操作。</p></div>
        <ToggleRow title="开机自启动" description="登录 Windows 后自动启动，在后台等待游戏运行（普通权限，不弹 UAC）" checked={config.autoStartWithWindows} onChange={(value) => updateConfig('autoStartWithWindows', value)} />
        <ToggleRow title="启动时自动以管理员权限运行" description="手动启动请求 UAC；开机自启不提权。需要时可点击「以管理员重新启动」" checked={config.autoStartAsAdministrator} onChange={(value) => updateConfig('autoStartAsAdministrator', value)} />
        <ToggleRow title="启动后最小化到托盘" description="开启：下次启动直接进托盘。关闭主窗口或点最小化 → 始终进入托盘后台（托盘「退出」才结束）" checked={config.startMinimized} onChange={(value) => updateConfig('startMinimized', value)} />
        <ToggleRow title="启动时显示用户协议" description="每次手动启动时展示用户协议与安全声明" checked={config.showSafetyNoticeOnStartup} onChange={(value) => updateConfig('showSafetyNoticeOnStartup', value)} />
        {isNative && <ToggleRow title="隐藏管理员权限提醒" description="关闭后，概览页不再显示「以管理员重新启动」提示条" checked={config.suppressAdminHint} onChange={(value) => updateConfig('suppressAdminHint', value)} />}
        {isNative && (
          <div className="setting-row admin-setting-row">
            <div>
              <span className="row-title">运行权限</span>
              <p>{isElevated
                ? '当前已以管理员身份运行，可向游戏进程注入解锁模块。'
                : '标准用户下注入可能失败。可一键提权重启（仅本次会话弹一次 UAC；开机自启仍为普通权限）。'}</p>
            </div>
            {isElevated
              ? <span className="admin-pill is-on"><ShieldCheck size={14} />管理员</span>
              : <button type="button" className="button button-secondary" disabled={busy || elevating} onClick={onRestartElevated}>
                  {elevating ? <LoaderCircle size={15} className="spin" /> : <Shield size={15} />}
                  {elevating ? '请求中…' : '以管理员重新启动'}
                </button>}
          </div>
        )}
      </section>}
      {tab === 'advanced' && <>
        <section className="control-panel setting-list"><div className="section-intro"><h2>后台与诊断</h2><p>默认值适用于日常使用，仅在需要时调整。</p></div>
          <NumberSetting title="进程检测间隔" description="检测游戏进程的时间间隔，范围 200 - 10000 ms" min={200} max={10000} unit="ms" value={config.pollIntervalMs} onChange={(value) => updateConfig('pollIntervalMs', value)} />
          <ToggleRow title="调试日志" description="在桌面版中记录详细诊断信息，帮助排查运行问题" checked={config.debugLogging} onChange={(value) => updateConfig('debugLogging', value)} />
          <div className="setting-row"><div><label className="row-title" htmlFor="log-level-setting">最低日志级别</label><p>{isNative ? '过滤写入磁盘日志文件的最低级别' : '此偏好用于桌面日志；网页会话日志始终保留交互记录'}</p></div><select id="log-level-setting" className="select-input" value={config.logLevel} onChange={(event) => updateConfig('logLevel', event.target.value as LogLevel)}>{['Trace', 'Debug', 'Info', 'Warn', 'Error'].map((level) => <option key={level}>{level}</option>)}</select></div>
          <NumberSetting title="日志保留时间" description="桌面版自动清理超过保留时间的日志，范围 1 - 90 天" min={1} max={90} unit="天" value={config.logRetainDays} onChange={(value) => updateConfig('logRetainDays', value)} />
        </section>
        <section className="control-panel config-tools"><div className="section-intro"><h2>配置管理</h2><p>在不同设备间迁移偏好，或保留一份熟悉的配置。</p></div><div className="config-tool-buttons"><button className="button button-secondary" onClick={onImport} disabled={busy}><Upload size={16} />导入配置</button><button className="button button-secondary" onClick={onExport}><Download size={16} />导出配置</button><button className="button button-quiet reset-button" onClick={onReset} disabled={busy}><RotateCcw size={15} />恢复默认</button></div><p className="input-help">{busy ? '请稍候再导入或恢复配置。导出仍可正常使用。' : isNative ? '导入/导出与桌面版 config.json 字段兼容。' : '导出为原项目兼容的 config.json。'}</p></section>
        {isNative && onUninstall && (
          <section className="control-panel config-tools uninstall-panel"><div className="section-intro"><h2>卸载</h2><p>调用安装器（Kachina）的卸载向导：清理程序文件、桌面与开始菜单快捷方式、开机自启动注册表项，以及「安装的应用」中的卸载登记。</p></div><div className="config-tool-buttons"><button className="button button-danger" onClick={onUninstall} disabled={busy}><Trash2 size={16} />卸载本软件</button></div><p className="input-help">卸载向导中可选择是否同时删除配置与日志（%LocalAppData%\GenshinFpsUnlocker）。安装器会自行申请管理员权限。</p></section>
        )}
      </>}
      {isNative
        ? <div className="settings-native-note"><Info size={16} /><p>当前已连接桌面服务。配置写入 %LocalAppData%\GenshinFpsUnlocker\config.json；自启、托盘与注入由宿主进程管理。需要卸载时，使用「高级设置 → 卸载」中的按钮，或在 Windows「设置 → 应用 → 安装的应用」中卸载（两者都会调用安装目录下的 GenshinFpsUnlocker.uninst.exe）。</p></div>
        : <div className="settings-native-note"><Info size={16} /><p>当前为网页预览，所有更改保存在此浏览器中。Windows 自启、托盘与进程检测等系统功能，需要连接桌面服务后生效。</p></div>}
    </div>
  </>;
}

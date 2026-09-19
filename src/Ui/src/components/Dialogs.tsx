/** 应用对话框：游戏路径设置、用户协议与安全声明、启动确认与通用危险操作确认。 */
import { ArrowUpRight, Check, CircleHelp, FileCode2, FolderOpen, Info, Monitor, Play, ScanLine, ShieldCheck, TriangleAlert } from 'lucide-react';
import { useEffect, useState } from 'react';
import { APP_NAME, GAME_META, cleanPath, gamePathHint, isValidGamePath, PROJECT_URL } from '../lib/config';
import type { GameId, UnlockerConfig } from '../lib/config';
import { nativeInvoke } from '../lib/native';
import { Checkbox, Modal } from './ui';

/**
 * 游戏路径设置对话框：浏览本地文件 / 自动查找（仅桌面版）、示例路径（仅网页预览）与查找指引。
 * 标题、示例路径与校验都跟着当前游戏走。
 */
export function PathDialog({ game, path, onSave, onClose, isNative, onBrowse, onAutoLocate }: {
  game: GameId;
  path: string | null;
  onSave: (path: string) => void | Promise<void>;
  onClose: () => void;
  isNative?: boolean;
  onBrowse?: () => Promise<string | null>;
  onAutoLocate?: () => Promise<string | null>;
}) {
  const meta = GAME_META[game];
  const [draft, setDraft] = useState(path ?? '');
  const [error, setError] = useState('');
  const [showHelp, setShowHelp] = useState(false);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    const cleaned = cleanPath(draft);
    if (!isValidGamePath(cleaned, game)) {
      setError(`请输入 Windows 完整路径，并以 ${gamePathHint(game)} 结尾。`);
      return;
    }
    setBusy(true);
    try { await onSave(cleaned); }
    catch (e) { setError(e instanceof Error ? e.message : '保存失败'); }
    finally { setBusy(false); }
  };
  return (
    <Modal title={game === 'genshin' ? '找到你的提瓦特' : '找到你的星穹列车'} description={`设置${meta.name}游戏主程序的完整路径。`} icon={FolderOpen} onClose={onClose} wide
      footer={<><button className="button button-quiet" onClick={onClose} disabled={busy}>取消</button><button className="button button-primary" onClick={() => void submit()} disabled={busy}><Check size={15} />保存路径</button></>}>
      <form onSubmit={(event) => { event.preventDefault(); void submit(); }}>
        <label htmlFor="game-path" className="input-label">{meta.name}主程序路径</label>
        <input data-autofocus id="game-path" className={`text-input path-input ${error ? 'input-invalid' : ''}`} value={draft}
          placeholder={meta.pathPlaceholder} spellCheck={false} autoComplete="off"
          aria-invalid={!!error} aria-describedby="path-help"
          onFocus={(event) => event.currentTarget.select()}
          onChange={(event) => { setDraft(event.target.value); setError(''); }} />
        <p id="path-help" className={error ? 'field-error' : 'input-help'}>{error || `支持 ${gamePathHint(game)}。`}</p>
        <div className="path-helper-actions">
          {isNative && onBrowse && <button type="button" className="text-button" disabled={busy} onClick={() => { void (async () => { setBusy(true); try { const p = await onBrowse(); if (p) setDraft(p); } finally { setBusy(false); } })(); }}><FolderOpen size={14} />浏览本地文件</button>}
          {isNative && onAutoLocate && <button type="button" className="text-button" disabled={busy} onClick={() => { void (async () => { setBusy(true); try { const p = await onAutoLocate(); if (p) setDraft(p); } finally { setBusy(false); } })(); }}><ScanLine size={14} />自动查找</button>}
          {!isNative && <button type="button" className="text-button" onClick={() => { setDraft(meta.demoPath); setError(''); }}><FileCode2 size={14} />使用示例路径</button>}
          <button type="button" className="text-button muted" onClick={() => setShowHelp(!showHelp)} aria-expanded={showHelp}><CircleHelp size={14} />如何查找路径？</button>
        </div>
        {showHelp && <div className="inline-instructions"><ol><li>在米哈游启动器中打开游戏设置，选择「打开游戏安装目录」。</li><li>找到 {gamePathHint(game)}，右键选择「复制文件地址」。</li><li>将完整地址粘贴到上方输入框，外层引号会自动去除。</li></ol></div>}
        {!isNative && <div className="subtle-notice"><Info size={16} /><p>浏览器无法验证本地文件是否存在。这里仅保存路径格式。</p></div>}
      </form>
    </Modal>
  );
}

/** 安全声明正文：优先显示宿主下发的协议全文，获取失败时回退到内置摘要。 */
function SafetyContent({ fullText }: { fullText?: string }) {
  if (fullText) {
    return <div className="safety-content"><pre className="safety-pre">{fullText}</pre></div>;
  }
  return (
    <div className="safety-content">
      <p>{APP_NAME} 是个人自用的第三方开源工具，代码与文档主要由 AI 生成；与米哈游 / HoYoverse 无关联，也未获得官方授权。</p>
      <div className="safety-points">
        <div><TriangleAlert size={17} /><span><strong>使用风险由你决定</strong><p>桌面版通过向游戏进程注入模块调整帧率，可能违反游戏服务条款。无法保证不会触发反作弊或账号限制。</p></span></div>
        <div><Monitor size={17} /><span><strong>先关闭垂直同步</strong><p>请在游戏的「设置 → 图像」中关闭垂直同步（V-Sync），并根据显示器刷新率和设备性能选择帧率。</p></span></div>
        <div><ShieldCheck size={17} /><span><strong>从可信来源下载</strong><p>仅使用项目官方 GitHub 仓库的构建或自行编译。不要随意关闭系统安全保护。</p></span></div>
      </div>
    </div>
  );
}

/** 用户协议与安全声明对话框：桌面版从宿主拉取协议全文；可勾选不再自动显示。 */
export function SafetyDialog({ onClose, onAcknowledge, isNative }: {
  onClose: () => void;
  onAcknowledge?: (showOnStartup: boolean) => void | Promise<void>;
  isNative?: boolean;
}) {
  const [fullText, setFullText] = useState<string | undefined>();
  const [showOnStartup, setShowOnStartup] = useState(true);
  useEffect(() => {
    if (!isNative) return;
    void nativeInvoke<{ fullText?: string }>('getSafetyText').then((r) => {
      if (r?.fullText) setFullText(r.fullText);
    }).catch(() => { /* 获取失败时使用内置摘要文案 */ });
  }, [isNative]);
  return (
    <Modal title="用户协议与安全声明" description="安装与使用前，请阅读并了解相关风险与责任。" icon={ShieldCheck} onClose={onClose}
      footer={<>
        <a className="text-button muted" href={`${PROJECT_URL}#安全说明`} target="_blank" rel="noreferrer">项目安全说明<ArrowUpRight size={14} /></a>
        {onAcknowledge
          ? <button className="button button-primary" onClick={() => void onAcknowledge(showOnStartup)}>我已阅读并同意</button>
          : <button className="button button-primary" onClick={onClose}>我已阅读并同意</button>}
      </>}>
      <SafetyContent fullText={fullText} />
      {onAcknowledge && (
        <div className="launch-checkboxes" style={{ marginTop: 12 }}>
          <Checkbox checked={!showOnStartup} onChange={(v) => setShowOnStartup(!v)}>下次启动不再自动显示</Checkbox>
        </div>
      )}
    </Modal>
  );
}

/** 启动确认框：汇总当前游戏、目标帧率与运行方式，勾选「已知悉风险」后才允许启动。 */
export function LaunchDialog({ game, config, onStart, onClose, isNative }: {
  game: GameId;
  config: UnlockerConfig;
  onStart: (dontAskAgain: boolean) => void | Promise<void>;
  onClose: () => void;
  isNative?: boolean;
}) {
  const profile = config.games[game];
  const [acknowledged, setAcknowledged] = useState(config.safetyNoticeAcknowledged);
  const [dontAskAgain, setDontAskAgain] = useState(!config.showSafetyNoticeOnStartup);
  const [busy, setBusy] = useState(false);
  return (
    <Modal title="准备好，开启流畅之旅" description={isNative ? `即将启动${GAME_META[game].name}并在后台应用帧率设置。` : '你即将体验一次完整的模拟启动流程。'} icon={Play} onClose={onClose}
      footer={<><button className="button button-quiet" onClick={onClose} disabled={busy}>暂不启动</button><button className="button button-primary" disabled={!acknowledged || busy} onClick={() => { void (async () => { setBusy(true); try { await onStart(dontAskAgain); } finally { setBusy(false); } })(); }}><Play size={15} fill="currentColor" />{isNative ? '启动游戏' : '开始启动演示'}</button></>}>
      <div className="launch-summary"><div><span>目标帧率</span><strong>{config.masterEnabled && profile.enabled ? profile.targetFps : 60}<small> FPS</small></strong></div><div><span>游戏</span><strong className="summary-mode">{GAME_META[game].name}</strong></div><div><span>运行方式</span><strong className="summary-mode">{isNative ? '桌面服务' : '网页交互演示'}</strong></div></div>
      {!isNative && <div className="subtle-notice preview-notice"><Info size={17} /><p>网页不会启动真实游戏，也不会读取或注入游戏进程。</p></div>}
      <p className="launch-risk">本工具属于第三方注入类软件，可能违反游戏服务条款，存在账号风险。请关闭游戏内 V-Sync，并自行评估后使用。桌面版注入通常需要管理员权限，可在概览页一键提权重启。</p>
      <div className="launch-checkboxes"><Checkbox checked={acknowledged} onChange={setAcknowledged}>我已了解第三方工具的使用风险</Checkbox><Checkbox checked={dontAskAgain} onChange={setDontAskAgain}>下次启动不再提示</Checkbox></div>
    </Modal>
  );
}

/** 通用确认框（红色按钮的危险操作）：恢复默认、清空日志、卸载等场景复用。 */
export function ConfirmDialog({ title, description, action, onConfirm, onClose }: {
  title: string; description: string; action: string; onConfirm: () => void | Promise<void>; onClose: () => void;
}) {
  const [busy, setBusy] = useState(false);
  return <Modal title={title} icon={TriangleAlert} onClose={onClose}
    footer={<><button className="button button-quiet" onClick={onClose} disabled={busy}>取消</button><button className="button button-danger" disabled={busy} onClick={() => { void (async () => { setBusy(true); try { await onConfirm(); } finally { setBusy(false); } })(); }}>{action}</button></>}><p className="confirmation-description">{description}</p></Modal>;
}

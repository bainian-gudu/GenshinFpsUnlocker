import { motion } from 'framer-motion';
import { ArrowRight, ArrowUpRight, ChevronRight, CircleHelp, Folder, FolderOpen, LayoutGrid, LoaderCircle, PanelBottomClose, Play, Power, ScanLine, Shield, ShieldAlert, ShieldCheck, SlidersHorizontal } from 'lucide-react';
import type { AppState } from '../hooks/useAppState';
import { FpsControl } from '../components/FpsControl';
import { RuntimeStatus } from '../components/RuntimeStatus';
import { FEATURE_ICONS, GameMark } from '../components/GameIcon';
import { PageHeading, ToggleRow } from '../components/ui';
import { APP_NAME, GAME_IDS, GAME_META } from '../lib/config';
import type { GameId } from '../lib/config';

/** 国服 / 国际服：只有原神能从可执行文件名区分，星穹铁道两边同名。 */
function regionOf(game: GameId, path: string | null): string | null {
  if (game !== 'genshin' || !path) return null;
  return path.toLowerCase().includes('genshinimpact.exe') ? '国际服' : '国服';
}

/**
 * 游戏概览页：主视觉、帧率与快捷设置、游戏库、权限提示。
 * 页面内容全部来自「当前游戏」的独立配置，切换游戏后整体换一套。
 */
export function OverviewPage({ app }: { app: AppState }) {
  const {
    native, config, gameConfig, activeGame, sessionGame, setModal, launchState, statusText, runningGame, attachedGame, attachedPid,
    isElevated, needsAdmin, elevating, effectiveEnabled, readiness, navigate, updateConfig,
    updateGameConfig, restartElevated, handleLaunch, openPathDialog,
  } = app;
  const meta = GAME_META[activeGame];

  return (
    <>
      <PageHeading title="游戏概览" description={`准备好，以更流畅的方式游玩${meta.name}。`}><div className={`readiness ${!effectiveEnabled || !gameConfig.gamePath ? 'is-paused' : ''}`} aria-live="polite">{launchState === 'launching' ? <LoaderCircle size={13} className="spin" /> : <span className={`status-dot ${attachedGame === activeGame && effectiveEnabled ? 'pulse' : ''}`} />}{readiness}</div></PageHeading>
      <section className={`overview-hero is-${activeGame}`} aria-label={`${APP_NAME} · ${meta.name}`}>
        {/* 星穹铁道主视觉素材未定，先复用原神那张风景图占位；换素材时改这里的 src 即可。 */}
        <motion.img className="hero-image" src="/images/teyvat-landscape.webp" alt={activeGame === 'genshin' ? '阳光下的璃月风格山峦、亭台与碧水' : '主视觉占位图（星穹铁道素材待替换）'} initial={{ scale: 1.045 }} animate={{ scale: 1 }} transition={{ duration: 1.8, ease: 'easeOut' }} />
        <div className="hero-shade" />
        <motion.div className="hero-copy" initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.65, delay: 0.1 }}><span className="hero-eyebrow">{meta.name}</span><h2>{APP_NAME}</h2><h3>{meta.hero.tagline}</h3><p>{meta.hero.copy}</p><button className="hero-guide" onClick={() => navigate('guide')}>初次使用？从这里开始<ArrowRight size={14} /></button></motion.div>
      </section>
      <motion.div className="overview-controls" initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.4, delay: 0.12 }}>
        <div className="overview-left-stack">
          <FpsControl value={gameConfig.targetFps} enabled={gameConfig.enabled} masterEnabled={config.masterEnabled} lock={meta.fpsLock} onChange={(value) => updateGameConfig('targetFps', value)} onToggle={(value) => updateGameConfig('enabled', value)} />
          <RuntimeStatus app={app} />
        </div>
        <div className="overview-right-stack">
          <section className="control-panel quick-settings"><div className="panel-heading"><h2><SlidersHorizontal size={17} strokeWidth={1.7} />快捷设置</h2><button className="text-button muted all-settings" onClick={() => navigate('settings')}>全部设置<ChevronRight size={13} /></button></div><div className="quick-settings-rows">
            {meta.injection.features.map(({ key, title, description }) => (
              <ToggleRow key={key} icon={FEATURE_ICONS[key]} title={title} description={description} checked={gameConfig[key]} onChange={(value) => updateGameConfig(key, value)} />
            ))}
            <ToggleRow icon={ScanLine} title="自动解锁" description="检测到游戏启动后，自动应用帧率设置" checked={config.autoWatch} onChange={(value) => updateConfig('autoWatch', value)} />
            <ToggleRow icon={Power} title="开机自启动" description="登录 Windows 后自动启动，在后台等待游戏运行" checked={config.autoStartWithWindows} onChange={(value) => updateConfig('autoStartWithWindows', value)} />
            <ToggleRow icon={Shield} title="启动时自动提权" description="登录自启改由最高权限计划任务启动（不弹 UAC）；手动启动请求一次 UAC" checked={config.autoStartAsAdministrator} onChange={(value) => updateConfig('autoStartAsAdministrator', value)} />
            <ToggleRow icon={PanelBottomClose} title="启动后最小化到托盘" description="开启后直接进托盘，关闭主窗口也进入托盘后台" checked={config.startMinimized} onChange={(value) => updateConfig('startMinimized', value)} />
          </div></section>
        </div>
      </motion.div>
      <motion.section className="control-panel game-library" aria-label="游戏库" initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.4, delay: 0.2 }}>
        <div className="panel-heading"><h2><LayoutGrid size={17} strokeWidth={1.7} />游戏库</h2><span className="panel-note">为任意一个游戏设置路径并启动，切换游戏请用顶部按钮</span></div>
        <div className="game-library-rows">
          {GAME_IDS.map((id) => {
            const item = config.games[id];
            const isCurrent = id === activeGame;
            const isSession = id === sessionGame;
            const region = regionOf(id, item.gamePath);
            const busy = isSession && launchState === 'launching';
            // 运行状态按游戏归属：只有真正在跑的那款显示运行/附加，另一款一律「等待启动」。
            const running = attachedGame === id;
            const state = running ? `已附加 · PID ${attachedPid}`
              : busy ? '正在启动…'
              : runningGame === id ? (native ? statusText || '运行中' : '运行中')
              : !native && isSession && launchState === 'running' ? '演示会话进行中'
              : '等待启动';
            return (
              <div key={id} className={`game-row ${isCurrent ? 'is-active' : ''}`}>
                <div className="game-art"><GameMark game={id} size={44} className="game-art-icon" /></div>
                <div className="game-info">
                  <div className="game-info-title">
                    <h3>{GAME_META[id].name}</h3>
                    {region && <span className="region-label">{region}</span>}
                    <span className="region-label">{item.targetFps} FPS</span>
                    <span className={`game-state ${running ? 'game-state-active' : ''}`}>{state}</span>
                  </div>
                  <div className="game-path"><Folder size={12} /><span title={item.gamePath ?? undefined}>{item.gamePath ?? '请先设置游戏主程序路径'}</span></div>
                </div>
                <div className="game-row-actions">
                  <button className="button button-secondary path-button" onClick={() => openPathDialog(id)} disabled={busy}><FolderOpen size={15} />更改路径</button>
                  <button className={`button button-primary launch-button ${running ? 'is-running' : ''}`} onClick={() => handleLaunch(id)} disabled={busy}>
                    {busy ? <LoaderCircle size={17} className="spin" /> : <Play size={16} fill="currentColor" />}
                    <span>{busy ? '启动中' : running ? '再次启动' : '启动游戏'}</span>
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      </motion.section>
      {native && needsAdmin && !config.suppressAdminHint && (
        <div className="admin-banner" role="status">
          <ShieldAlert size={18} strokeWidth={1.6} />
          <div className="admin-banner-body">
            <strong>解锁帧率需要管理员权限</strong>
            <p>向游戏进程注入模块时，标准用户可能无法打开目标进程（OpenProcess 失败）。点击下方按钮将弹出一次 UAC，同意后以管理员重新启动本程序。若希望登录时就直接以管理员权限运行，可在「全部设置 → 启动与行为」开启「启动时自动以管理员权限运行」，该方式由计划任务完成，登录时不会弹 UAC。</p>
          </div>
          <div className="admin-banner-actions">
            <button className="button button-primary" disabled={elevating} onClick={() => void restartElevated()}>
              {elevating ? <LoaderCircle size={16} className="spin" /> : <Shield size={16} />}
              <span>{elevating ? '请求中…' : '以管理员重新启动'}</span>
            </button>
            <button className="button button-quiet" disabled={elevating} onClick={() => updateConfig('suppressAdminHint', true)}>不再提醒</button>
          </div>
        </div>
      )}
      {native && isElevated && (
        <div className="admin-banner is-elevated" role="status">
          <ShieldCheck size={18} strokeWidth={1.6} />
          <div className="admin-banner-body">
            <strong>已以管理员权限运行</strong>
            <p>当前会话可正常向游戏进程注入帧率解锁模块。关闭本窗口仍会驻留托盘。</p>
          </div>
        </div>
      )}
      <div className="overview-tip"><ShieldCheck size={16} strokeWidth={1.6} /><p><span>冒险小贴士</span>请先关闭游戏内垂直同步（V-Sync）。第三方工具存在使用风险，使用前请阅读<button onClick={() => setModal('safety')}>用户协议<ArrowUpRight size={12} /></button></p><button className="icon-button tip-help" onClick={() => navigate('guide')} aria-label="查看使用帮助"><CircleHelp size={16} /></button></div>
    </>
  );
}

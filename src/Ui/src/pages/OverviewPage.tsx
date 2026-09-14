import { motion } from 'framer-motion';
import { ArrowRight, ArrowUpRight, ChevronRight, CircleHelp, Download, Folder, FolderOpen, LoaderCircle, PanelBottomClose, Play, Power, ScanLine, Shield, ShieldAlert, ShieldCheck, SlidersHorizontal, Sparkles, WandSparkles } from 'lucide-react';
import type { AppState } from '../hooks/useAppState';
import { FpsControl } from '../components/FpsControl';
import { PageHeading, ToggleRow } from '../components/ui';

/** 游戏概览页：主视觉、帧率与快捷设置、启动面板、权限提示。 */
export function OverviewPage({ app }: { app: AppState }) {
  const {
    native, config, setModal, launchState, statusText, attachedPid, isElevated, needsAdmin, elevating,
    effectiveEnabled, readiness, upscaler, navigate, updateConfig, restartElevated, handleLaunch, downloadDlssRuntime, downloadingDlss,
  } = app;

  return (
    <>
      <PageHeading title="游戏概览" description="准备好，以更流畅的方式探索提瓦特。"><div className={`readiness ${!effectiveEnabled || !config.gamePath ? 'is-paused' : ''}`} aria-live="polite">{launchState === 'launching' ? <LoaderCircle size={13} className="spin" /> : <span className={`status-dot ${attachedPid > 0 && effectiveEnabled ? 'pulse' : ''}`} />}{readiness}</div></PageHeading>
      <section className="overview-hero" aria-label="Genshin FPS Unlocker">
        <motion.img className="hero-image" src="/images/teyvat-landscape.jpg" alt="阳光下的璃月风格山峦、亭台与碧水" initial={{ scale: 1.045 }} animate={{ scale: 1 }} transition={{ duration: 1.8, ease: 'easeOut' }} />
        <div className="hero-shade" />
        <motion.div className="hero-copy" initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.65, delay: 0.1 }}><h2>Genshin FPS Unlocker</h2><h3>让每一帧，都不被设限。</h3><p>更高帧率，更自在的冒险。以你喜欢的节奏，探索提瓦特。</p><button className="hero-guide" onClick={() => navigate('guide')}>初次使用？从这里开始<ArrowRight size={14} /></button></motion.div>
      </section>
      <motion.div className="overview-controls" initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.4, delay: 0.12 }}>
        <div className="overview-left-stack">
          <FpsControl value={config.targetFps} enabled={config.enabled} masterEnabled={config.masterEnabled} onChange={(value) => updateConfig('targetFps', value)} onToggle={(value) => updateConfig('enabled', value)} />
          <section className="control-panel upscaler-card" aria-label="超分辨率替换">
            <div className="panel-heading"><h2><Sparkles size={17} strokeWidth={1.7} />超分辨率替换</h2><span className="feature-badge">实验组件</span></div>
            <div className="upscaler-card-body">
              <div className="upscaler-card-icon"><Sparkles size={22} strokeWidth={1.5} /></div>
              <div className="upscaler-card-copy"><strong>FSR 2.0 → DLSS</strong><p>独立代理组件与现有注入模块分开运行，完成渲染接口验证后可在这里启用。</p></div>
              <span className="feature-status">{upscaler.status}</span>
            </div>
            <div className="upscaler-quick-toggle"><ToggleRow title="启动游戏时自动替换" description="检测到原神启动后自动应用超分辨率替换" checked={config.upscalerReplacementEnabled} onChange={(value) => updateConfig('upscalerReplacementEnabled', value)} disabled={!upscaler.available} /></div>
            <p className="upscaler-card-note">需要 NVIDIA RTX 显卡、兼容驱动和用户提供的官方 DLSS Runtime；不会改变帧率解锁与反虚化注入。</p>
            {native && <button className="button button-secondary upscaler-download-button" type="button" onClick={() => void downloadDlssRuntime()} disabled={downloadingDlss || upscaler.dlssRuntimePresent}>
              {downloadingDlss ? <LoaderCircle size={14} className="spin" /> : <Download size={14} />}
              {downloadingDlss ? '下载中…' : upscaler.dlssRuntimePresent ? 'DLSS Runtime 已存在' : '下载 DLSS Runtime'}
            </button>}
          </section>
        </div>
        <section className="control-panel quick-settings"><div className="panel-heading"><h2><SlidersHorizontal size={17} strokeWidth={1.7} />快捷设置</h2><button className="text-button muted all-settings" onClick={() => navigate('settings')}>全部设置<ChevronRight size={13} /></button></div><div className="quick-settings-rows"><ToggleRow icon={ScanLine} title="自动解锁" description="检测到游戏启动后自动应用设置" checked={config.autoWatch} onChange={(value) => updateConfig('autoWatch', value)} /><ToggleRow icon={WandSparkles} title="反角色虚化" description="镜头拉近时角色不再透明化" checked={config.antiBlurPerspective} onChange={(value) => updateConfig('antiBlurPerspective', value)} /><ToggleRow icon={WandSparkles} title="移除水下马赛克" description="角色入水时不再显示马赛克虚化" checked={config.antiBlurDiveMosaic} onChange={(value) => updateConfig('antiBlurDiveMosaic', value)} /><ToggleRow icon={Power} title="开机自启动" description="登录 Windows 后在后台运行" checked={config.autoStartWithWindows} onChange={(value) => updateConfig('autoStartWithWindows', value)} /><ToggleRow icon={PanelBottomClose} title="启动后最小化到托盘" description="开启后下次启动直接进托盘；关窗/最小化始终会藏到托盘" checked={config.startMinimized} onChange={(value) => updateConfig('startMinimized', value)} /></div></section>
      </motion.div>
      <motion.section className={`game-launch-panel ${launchState !== 'idle' || attachedPid > 0 ? 'session-active' : ''}`} aria-label="游戏与启动" initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.4, delay: 0.2 }}>
        <div className="game-art" aria-hidden="true"><img className="game-art-icon" src="/images/game-icon.webp" alt="" width={54} height={54} draggable={false} /></div>
        <div className="game-info"><div className="game-info-title"><h2>原神</h2><span className="region-label">{config.gamePath?.toLowerCase().includes('genshinimpact.exe') ? '国际服' : '国服'}</span><span className={`game-state ${attachedPid > 0 ? 'game-state-active' : ''}`}>{attachedPid > 0 ? `已附加 · PID ${attachedPid}` : launchState === 'launching' ? '正在启动…' : statusText || '等待启动'}</span></div><div className="game-path"><Folder size={12} /><span title={config.gamePath ?? undefined}>{config.gamePath ?? '请先设置游戏主程序路径'}</span></div></div>
        <div className="launch-actions">
          <button className="button button-secondary path-button" onClick={() => setModal('path')} disabled={launchState === 'launching'}><FolderOpen size={15} />更改路径</button>
          <button className={`button button-primary launch-button ${attachedPid > 0 ? 'is-running' : ''}`} onClick={handleLaunch} disabled={launchState === 'launching'}>
            {launchState === 'launching' ? <LoaderCircle size={17} className="spin" /> : <Play size={16} fill="currentColor" />}
            <span>{launchState === 'launching' ? '启动中' : attachedPid > 0 ? '再次启动' : '启动游戏'}</span>
          </button>
        </div>
      </motion.section>
      {native && needsAdmin && !config.suppressAdminHint && (
        <div className="admin-banner" role="status">
          <ShieldAlert size={18} strokeWidth={1.6} />
          <div className="admin-banner-body">
            <strong>解锁帧率需要管理员权限</strong>
            <p>向游戏进程注入模块时，标准用户可能无法打开目标进程（OpenProcess 失败）。点击下方按钮将弹出一次 UAC，同意后以管理员重新启动本程序。开机自启仍为普通权限，不会每天弹窗。</p>
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

import { AnimatePresence, MotionConfig, motion } from 'framer-motion';
import { LoaderCircle } from 'lucide-react';
import { AppDialogs } from './components/AppDialogs';
import { AppSidebar } from './components/AppSidebar';
import { AppTopbar } from './components/AppTopbar';
import { StatusBar } from './components/StatusBar';
import { Toasts } from './components/ui';
import { useAppState } from './hooks/useAppState';
import { nativeInvoke } from './lib/native';
import { AboutPage } from './pages/AboutPage';
import { GuidePage } from './pages/GuidePage';
import { LogsPage } from './pages/LogsPage';
import { OverviewPage } from './pages/OverviewPage';
import { SettingsPage } from './pages/SettingsPage';

/**
 * 应用外壳：状态与动作集中在 useAppState，各区块拆到 components / pages。
 * 本文件只负责组合与页面路由（hash）。
 */
export default function App() {
  const app = useAppState();
  const {
    native, booting, config, page, sidebarOpen, setSidebarOpen, setModal, toasts, dismissToast,
    logs, launchState, isElevated, elevating, version, importRef, navigate, updateConfig,
    restartElevated, exportConfig, importConfig, exportLogs,
  } = app;

  if (booting) {
    return <div className="app-shell boot-shell"><div className="boot-card"><LoaderCircle className="spin" size={28} /><p>正在连接桌面服务…</p></div></div>;
  }

  return (
    <MotionConfig reducedMotion="user">
      <div className="app-shell">
        <a href="#main-content" className="skip-link" onClick={(event) => { event.preventDefault(); document.getElementById('main-content')?.focus(); }}>跳转到主要内容</a>
        <AnimatePresence>{sidebarOpen && <motion.div initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} className="sidebar-backdrop" onClick={() => setSidebarOpen(false)} />}</AnimatePresence>
        <AppSidebar app={app} />
        <div className="main-pane">
          <AppTopbar app={app} />
          <AnimatePresence mode="wait" initial={false}>
            <motion.main id="main-content" tabIndex={-1} className={`main-content page-${page}`} key={page} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -4 }} transition={{ duration: 0.18 }}>
              {page === 'overview' && <OverviewPage app={app} />}
              {page === 'settings' && <SettingsPage config={config} updateConfig={updateConfig} onPath={() => setModal('path')} onExport={exportConfig} onImport={() => importRef.current?.click()} onReset={() => setModal('reset')} onUninstall={native ? () => setModal('uninstall') : undefined} busy={launchState === 'launching' || elevating} isNative={native} isElevated={isElevated} onRestartElevated={native && !isElevated ? () => void restartElevated() : undefined} elevating={elevating} upscaler={app.upscaler} autostart={app.autostart} />}
              {page === 'logs' && <LogsPage logs={logs} onClear={() => setModal('clearLogs')} onExport={exportLogs} isNative={native} onOpenFolder={native ? () => { void nativeInvoke('openLogFolder').catch(() => undefined); } : undefined} />}
              {page === 'guide' && <GuidePage navigate={navigate} onSafety={() => setModal('safety')} isNative={native} />}
              {page === 'about' && <AboutPage onSafety={() => setModal('safety')} version={version} isNative={native} onUninstall={native ? () => setModal('uninstall') : undefined} />}
            </motion.main>
          </AnimatePresence>
          <StatusBar app={app} />
        </div>
        <input ref={importRef} type="file" accept="application/json,.json" className="visually-hidden" aria-label="导入配置文件" tabIndex={-1} onChange={importConfig} />
        <Toasts items={toasts} onDismiss={dismissToast} />
        <AppDialogs app={app} />
      </div>
    </MotionConfig>
  );
}

import { AnimatePresence, MotionConfig, motion } from 'framer-motion';
import {
  ArrowRight, ArrowUpRight, BookOpen, Check, ChevronRight, CircleHelp, Folder,
  FolderOpen, Info, LayoutGrid, LoaderCircle, Menu, Moon, PanelBottomClose,
  PanelsTopLeft, Play, Power, ScanLine, Shield, ShieldAlert, ShieldCheck,
  SlidersHorizontal, SquareTerminal, Sun, X,
} from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { ChangeEvent } from 'react';
import { Brand, BrandMark, GithubIcon } from './components/Brand';
import { ConfirmDialog, LaunchDialog, PathDialog, SafetyDialog } from './components/Dialogs';
import { FpsControl } from './components/FpsControl';
import { PageHeading, Toasts, ToggleRow } from './components/ui';
import type { ToastItem } from './components/ui';
import {
  CONFIG_LABELS, DEFAULT_CONFIG, downloadFile, getPage, loadConfig,
  makeLog, parseConfig, PROJECT_URL, STORAGE_KEY,
} from './lib/config';
import type { LogEntry, LogLevel, Page, Theme, UnlockerConfig } from './lib/config';
import {
  isNativeHost, nativeGetBootstrap, nativeInvoke, onNativeLog, onNativeState,
} from './lib/native';
import type { NativeState } from './lib/native';
import { AboutPage } from './pages/AboutPage';
import { GuidePage } from './pages/GuidePage';
import { LogsPage } from './pages/LogsPage';
import { SettingsPage } from './pages/SettingsPage';

type ModalType = 'path' | 'safety' | 'launch' | 'reset' | 'clearLogs' | 'uninstall' | null;
type LaunchState = 'idle' | 'launching' | 'running';
const PAGE_NAMES: Record<Page, string> = { overview: '游戏概览', settings: '游戏设置', logs: '运行日志', guide: '使用指南', about: '关于项目' };
const NAV_ITEMS = [{ page: 'overview', label: '游戏概览', icon: LayoutGrid }, { page: 'settings', label: '游戏设置', icon: SlidersHorizontal }, { page: 'logs', label: '运行日志', icon: SquareTerminal }] as const;
const SECONDARY_NAV = [{ page: 'guide', label: '使用指南', icon: BookOpen }, { page: 'about', label: '关于项目', icon: Info }] as const;

export default function App() {
  const native = isNativeHost();
  const [booting, setBooting] = useState(native);
  const [initial] = useState(() => (native ? { config: { ...DEFAULT_CONFIG }, recovered: false } : loadConfig()));
  const [config, setConfig] = useState<UnlockerConfig>(initial.config);
  const configRef = useRef(config);
  configRef.current = config;
  const [page, setPage] = useState<Page>(getPage);
  const [theme, setTheme] = useState<Theme>(() => {
    try { return localStorage.getItem('genshin-fps-unlocker.theme') === 'light' ? 'light' : 'dark'; } catch { return 'dark'; }
  });
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [modal, setModal] = useState<ModalType>(null);
  const [saveState, setSaveState] = useState<'saving' | 'saved' | 'error'>('saved');
  const [toasts, setToasts] = useState<ToastItem[]>([]);
  const toastId = useRef(0);
  const [logs, setLogs] = useState<LogEntry[]>(() => (
    native ? [] : [
      makeLog('Info', 'Genshin FPS Unlocker 网页界面已就绪。'),
      makeLog(initial.recovered ? 'Warn' : 'Info', initial.recovered ? '本地配置无法读取，已恢复演示默认值。' : '已载入本地偏好。'),
    ]
  ));
  const [launchState, setLaunchState] = useState<LaunchState>('idle');
  const launchStateRef = useRef<LaunchState>(launchState);
  launchStateRef.current = launchState;
  const [statusText, setStatusText] = useState('准备中');
  const [attachedPid, setAttachedPid] = useState(0);
  const [currentFps, setCurrentFps] = useState(0);
  const [isElevated, setIsElevated] = useState(false);
  const [needsAdmin, setNeedsAdmin] = useState(false);
  const [elevating, setElevating] = useState(false);
  const [version, setVersion] = useState('1.0.0');
  const importRef = useRef<HTMLInputElement>(null);
  const sidebarRef = useRef<HTMLElement>(null);
  const previousFps = useRef(config.targetFps);
  const patchTimer = useRef<number | null>(null);
  const pendingPatch = useRef<Partial<UnlockerConfig>>({});

  const addLog = useCallback((level: LogLevel, message: string) => {
    setLogs((previous) => [...previous, makeLog(level, message)].slice(-200));
  }, []);
  const notify = useCallback((title: string, description?: string, type: ToastItem['type'] = 'success') => {
    setToasts((previous) => [...previous.slice(-2), { id: ++toastId.current, title, description, type }]);
  }, []);
  const dismissToast = useCallback((id: number) => setToasts((previous) => previous.filter((toast) => toast.id !== id)), []);
  const navigate = useCallback((next: Page) => {
    setPage(next);
    setSidebarOpen(false);
    window.location.hash = next;
    window.scrollTo({ top: 0, behavior: 'auto' });
  }, []);

  const applyNativeState = useCallback((state: NativeState) => {
    setConfig(state.config);
    setSaveState(state.saveState);
    setStatusText(state.statusText || '就绪');
    setAttachedPid(state.attachedPid);
    setCurrentFps(state.currentFps);
    setIsElevated(Boolean(state.isElevated));
    setNeedsAdmin(Boolean(state.needsAdminForUnlock));
    setVersion(state.version || '1.0.0');
    setLaunchState(state.attachedPid > 0 ? 'running' : 'idle');
  }, []);

  useEffect(() => {
    if (!native) return;
    let cancelled = false;
    (async () => {
      try {
        const boot = await nativeGetBootstrap();
        if (cancelled) return;
        applyNativeState(boot.state);
        if (boot.logs.length) setLogs(boot.logs);
        else addLog('Info', '已连接桌面服务。');
        if (!boot.state.config.safetyNoticeAcknowledged) setModal('safety');
      } catch (error) {
        notify('无法连接桌面服务', error instanceof Error ? error.message : '未知错误', 'error');
        addLog('Error', '原生桥初始化失败');
      } finally {
        if (!cancelled) setBooting(false);
      }
    })();
    const offState = onNativeState((state) => applyNativeState(state));
    const offLog = onNativeLog((entry) => setLogs((prev) => [...prev, entry].slice(-200)));
    return () => { cancelled = true; offState(); offLog(); };
  }, [native, applyNativeState, addLog, notify]);

  useEffect(() => {
    const onHashChange = () => { setPage(getPage()); setSidebarOpen(false); window.scrollTo({ top: 0, behavior: 'auto' }); };
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);

  useEffect(() => {
    if (!sidebarOpen) return;
    const query = window.matchMedia('(max-width: 560px)');
    if (!query.matches) { setSidebarOpen(false); return; }
    const previousFocus = document.activeElement as HTMLElement | null;
    const previousOverflow = document.body.style.overflow;
    const pane = document.querySelector<HTMLElement>('.main-pane');
    if (pane) pane.inert = true;
    document.body.style.overflow = 'hidden';
    const frame = requestAnimationFrame(() => sidebarRef.current?.querySelector<HTMLElement>('.nav-item.active')?.focus());
    const onResize = () => { if (!query.matches) setSidebarOpen(false); };
    const onTab = (event: KeyboardEvent) => {
      if (event.key !== 'Tab') return;
      const elements = Array.from(sidebarRef.current?.querySelectorAll<HTMLElement>('a[href], button') ?? [])
        .filter((element) => element.getClientRects().length > 0);
      const first = elements[0];
      const last = elements[elements.length - 1];
      if (!first) return;
      if (!sidebarRef.current?.contains(document.activeElement)) { event.preventDefault(); first.focus(); }
      else if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    query.addEventListener('change', onResize);
    document.addEventListener('keydown', onTab);
    return () => {
      cancelAnimationFrame(frame);
      if (pane) pane.inert = false;
      document.body.style.overflow = previousOverflow;
      query.removeEventListener('change', onResize);
      document.removeEventListener('keydown', onTab);
      if (previousFocus?.isConnected) previousFocus.focus();
    };
  }, [sidebarOpen]);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? '#121319' : '#f5f5f8');
    try { localStorage.setItem('genshin-fps-unlocker.theme', theme); } catch { /* ignore */ }
    // 桌面宿主：标题栏 / 窗体底色与 UI 深浅一致（Win11 caption color）
    if (native) {
      void nativeInvoke('setUiTheme', { theme }).catch(() => undefined);
    }
  }, [theme, native]);

  useEffect(() => {
    document.title = `${PAGE_NAMES[page]} | Genshin FPS Unlocker`;
  }, [page]);

  // Web-only localStorage persistence
  useEffect(() => {
    if (native) return;
    setSaveState('saving');
    const timer = window.setTimeout(() => {
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(config));
        setSaveState('saved');
      } catch {
        setSaveState('error');
        notify('无法保存到浏览器', '请检查浏览器存储权限。', 'error');
      }
    }, 400);
    return () => clearTimeout(timer);
  }, [config, native, notify]);

  useEffect(() => {
    if (previousFps.current === config.targetFps) return;
    const timer = window.setTimeout(() => {
      addLog('Info', `目标帧率已调整为 ${config.targetFps} FPS。`);
      previousFps.current = config.targetFps;
    }, 600);
    return () => clearTimeout(timer);
  }, [config.targetFps, addLog]);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const target = event.target as HTMLElement;
      if (target.matches('input, textarea, select') || target.isContentEditable || modal) return;
      if ((event.ctrlKey || event.metaKey) && event.key === ',') { event.preventDefault(); navigate('settings'); }
      if (event.altKey && ['1', '2', '3'].includes(event.key)) { event.preventDefault(); navigate(NAV_ITEMS[Number(event.key) - 1].page); }
      if (event.key === 'Escape') setSidebarOpen(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [modal, navigate]);

  const flushNativePatch = useCallback(async () => {
    if (!native) return;
    const patch = pendingPatch.current;
    pendingPatch.current = {};
    if (!Object.keys(patch).length) return;
    setSaveState('saving');
    try {
      const state = await nativeInvoke<NativeState['config'] extends never ? never : any>('patchConfig', patch);
      if (state?.config) applyNativeState(state as NativeState);
      else setSaveState('saved');
    } catch (error) {
      setSaveState('error');
      notify('保存失败', error instanceof Error ? error.message : '未知错误', 'error');
    }
  }, [native, applyNativeState, notify]);

  function updateConfig<K extends keyof UnlockerConfig>(key: K, value: UnlockerConfig[K]) {
    if (configRef.current[key] === value) return;
    setConfig((previous) => ({ ...previous, [key]: value }));
    if (key !== 'targetFps') {
      addLog('Info', `已更新「${CONFIG_LABELS[key]}」：${typeof value === 'boolean' ? value ? '开启' : '关闭' : value ?? '未设置'}。`);
    }
    if (native) {
      pendingPatch.current = { ...pendingPatch.current, [key]: value };
      if (patchTimer.current) window.clearTimeout(patchTimer.current);
      const delay = key === 'targetFps' ? 350 : 80;
      patchTimer.current = window.setTimeout(() => { void flushNativePatch(); }, delay);
    }
  }

  async function beginLaunch() {
    setModal(null);
    if (!native) {
      setLaunchState('running');
      addLog('Info', '网页演示会话已开始（未连接桌面服务）。');
      notify('演示已开始', '当前为浏览器预览。');
      return;
    }
    setLaunchState('launching');
    try {
      const result = await nativeInvoke<{ ok: boolean; message: string; state?: NativeState }>('launchGame');
      if (result.state) applyNativeState(result.state as NativeState);
      if (result.ok) {
        setLaunchState('running');
        notify('已启动游戏', result.message);
        addLog('Info', result.message);
      } else {
        setLaunchState('idle');
        notify('启动失败', result.message, 'error');
        addLog('Warn', result.message);
      }
    } catch (error) {
      setLaunchState('idle');
      notify('启动失败', error instanceof Error ? error.message : '未知错误', 'error');
    }
  }

  async function restartElevated() {
    if (!native || elevating || isElevated) return;
    setElevating(true);
    try {
      const result = await nativeInvoke<{ ok: boolean; message?: string }>('restartElevated');
      if (result?.ok) {
        notify('正在请求管理员权限', '请在 UAC 对话框中选择「是」。本窗口即将关闭。', 'success');
        addLog('Info', '已请求以管理员身份重新启动');
      } else {
        notify('未能提权重启', result?.message || '用户取消了授权，或系统拒绝了请求。', 'error');
        addLog('Warn', result?.message || 'restartElevated 失败');
        setElevating(false);
      }
    } catch (error) {
      notify('提权失败', error instanceof Error ? error.message : '未知错误', 'error');
      setElevating(false);
    }
  }

  /**
   * 卸载：只负责拉起 Kachina 安装器生成的 uninst.exe，
   * 文件 / 快捷方式 / 自启动注册表 / 卸载登记项全部由卸载器清理。
   */
  async function startUninstall() {
    if (!native) return;
    try {
      const result = await nativeInvoke<{ ok: boolean; message?: string }>('uninstall');
      if (result?.ok) {
        notify('正在启动卸载向导', result.message || '卸载向导已打开，本窗口即将关闭。');
        addLog('Info', '已请求启动 Kachina 卸载程序');
      } else {
        notify('无法启动卸载', result?.message || '未找到卸载程序。', 'error');
        addLog('Warn', result?.message || 'uninstall 失败');
      }
    } catch (error) {
      notify('无法启动卸载', error instanceof Error ? error.message : '未知错误', 'error');
      addLog('Error', `uninstall 异常: ${error instanceof Error ? error.message : String(error)}`);
    }
  }

    function handleLaunch() {
    if (launchState === 'launching') return;
    if (launchState === 'running' && !native) {
      setLaunchState('idle');
      notify('演示已结束');
      return;
    }
    if (!config.gamePath) { setModal('path'); notify('先设置游戏路径', undefined, 'info'); return; }
    if (config.safetyNoticeAcknowledged && !config.showSafetyNoticeOnStartup) void beginLaunch();
    else setModal('launch');
  }

  async function exportConfig() {
    try {
      if (native) {
        const result = await nativeInvoke<{ json: string; fileName: string }>('exportConfig');
        downloadFile(result.json, result.fileName);
      } else {
        downloadFile(`${JSON.stringify(config, null, 2)}\n`, 'config.json');
      }
      notify('配置已导出');
      addLog('Info', '已导出 config.json。');
    } catch (error) {
      notify('导出失败', error instanceof Error ? error.message : '未知错误', 'error');
    }
  }

  async function importConfig(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    try {
      if (file.size > 256 * 1024) throw new Error('配置文件过大。');
      const text = await file.text();
      if (native) {
        const state = await nativeInvoke<any>('importConfig', { json: text });
        applyNativeState(state as NativeState);
      } else {
        setConfig(parseConfig(JSON.parse(text)));
      }
      notify('配置导入成功');
      addLog('Info', `已从 ${file.name} 导入配置。`);
    } catch (error) {
      const message = error instanceof SyntaxError ? '文件不是有效的 JSON。' : error instanceof Error ? error.message : '无法读取此文件。';
      notify('配置导入失败', message, 'error');
      addLog('Error', `配置导入失败：${message}`);
    }
  }

  async function exportLogs() {
    try {
      if (native) {
        const result = await nativeInvoke<{ content: string; fileName: string }>('exportLogs');
        downloadFile(result.content, result.fileName, 'text/plain');
      } else {
        const content = logs.map((entry) => `[${entry.timestamp}] [${entry.level}] ${entry.message}`).join('\n');
        downloadFile(content + '\n', `genshin-unlocker-${new Date().toISOString().slice(0, 10)}.log`, 'text/plain');
      }
      notify('日志已导出');
    } catch (error) {
      notify('导出失败', error instanceof Error ? error.message : '未知错误', 'error');
    }
  }

  async function savePath(path: string) {
    if (native) {
      const state = await nativeInvoke<any>('setGamePath', { path });
      applyNativeState(state as NativeState);
    } else {
      updateConfig('gamePath', path);
    }
    setModal(null);
    notify('游戏路径已保存');
  }

  async function browsePath() {
    if (!native) return null;
    const result = await nativeInvoke<{ ok: boolean; path?: string; detail?: string; state?: any }>('browseGamePath');
    if (result.state) applyNativeState(result.state as NativeState);
    if (result.ok && result.path) {
      setModal(null);
      notify('游戏路径已保存', result.path);
      return result.path;
    }
    if (result.detail && result.detail !== '已取消手动选择') notify('选择路径', result.detail, 'info');
    return null;
  }

  async function autoLocatePath() {
    if (!native) return null;
    const result = await nativeInvoke<{ ok: boolean; path?: string; detail?: string; state?: any }>('autoLocateGamePath');
    if (result.state) applyNativeState(result.state as NativeState);
    if (result.ok) {
      setModal(null);
      notify('已自动找到游戏', result.path);
      return result.path ?? null;
    }
    notify('未找到游戏', result.detail, 'error');
    return null;
  }

  const effectiveEnabled = config.masterEnabled && config.enabled;
  const readiness = launchState === 'launching' ? '正在启动…'
    : launchState === 'running' || attachedPid > 0
      ? (effectiveEnabled ? `运行中 · PID ${attachedPid || '—'}${currentFps > 0 ? ` · ${currentFps} FPS` : ''}` : '已附加 · 解锁暂停')
    : !config.gamePath ? '请先设置游戏路径'
    : !config.masterEnabled ? '解锁服务已暂停'
    : !config.enabled ? '帧率解锁已关闭'
    : statusText || '准备就绪';

  if (booting) {
    return <div className="app-shell boot-shell"><div className="boot-card"><LoaderCircle className="spin" size={28} /><p>正在连接桌面服务…</p></div></div>;
  }

  return (
    <MotionConfig reducedMotion="user">
      <div className="app-shell">
        <a href="#main-content" className="skip-link" onClick={(event) => { event.preventDefault(); document.getElementById('main-content')?.focus(); }}>跳转到主要内容</a>
        <AnimatePresence>{sidebarOpen && <motion.div initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }} className="sidebar-backdrop" onClick={() => setSidebarOpen(false)} />}</AnimatePresence>
        <aside ref={sidebarRef} id="app-navigation" className={`sidebar ${sidebarOpen ? 'sidebar-open' : ''}`} aria-label="主导航" role={sidebarOpen ? 'dialog' : undefined} aria-modal={sidebarOpen || undefined}>
          <div className="sidebar-brand-row"><button className="brand-button" aria-label="返回游戏概览" onClick={() => navigate('overview')}><Brand /></button><button className="icon-button sidebar-close" onClick={() => setSidebarOpen(false)} aria-label="关闭导航"><X size={19} /></button></div>
          <div className="nav-group-label">工作空间</div>
          <nav className="primary-nav" aria-label="工作空间">
            {NAV_ITEMS.map(({ page: itemPage, label, icon: Icon }, index) => (
              <a key={itemPage} href={`#${itemPage}`} aria-label={label} title={`${label} (Alt+${index + 1})`}
                aria-current={page === itemPage ? 'page' : undefined} className={`nav-item ${page === itemPage ? 'active' : ''}`}
                onClick={(event) => { event.preventDefault(); navigate(itemPage); }}>
                {page === itemPage && <motion.span layoutId="active-navigation" className="nav-active-background" transition={{ type: 'spring', stiffness: 400, damping: 36 }} />}
                <Icon size={19} strokeWidth={1.6} /><span>{label}</span>
                {itemPage === 'logs' && logs.length > 0 && <span className="nav-count">{logs.length > 99 ? '99+' : logs.length}</span>}
              </a>
            ))}
          </nav>
          <div className="sidebar-bottom"><div className="sidebar-constellation" aria-hidden="true"><svg viewBox="0 0 180 130" fill="none"><path d="m12 102 32-30 36 14 29-47 48-23" stroke="currentColor" strokeWidth=".7" /><circle cx="12" cy="102" r="2" fill="currentColor" /><circle cx="44" cy="72" r="3" fill="currentColor" /><circle cx="80" cy="86" r="2" fill="currentColor" /><circle cx="109" cy="39" r="2.5" fill="currentColor" /><path d="m157 9 2 5 5 2-5 2-2 5-2-5-5-2 5-2 2-5Z" fill="currentColor" /><circle cx="72" cy="30" r="1" fill="currentColor" /><circle cx="145" cy="76" r="1" fill="currentColor" /></svg></div>
            <nav className="secondary-nav" aria-label="帮助与项目">
              {SECONDARY_NAV.map(({ page: itemPage, label, icon: Icon }) => (
                <a href={`#${itemPage}`} key={itemPage} aria-label={label} title={label}
                  aria-current={page === itemPage ? 'page' : undefined} className={`nav-item ${page === itemPage ? 'active' : ''}`}
                  onClick={(event) => { event.preventDefault(); navigate(itemPage); }}>
                  {page === itemPage && <motion.span layoutId="active-navigation" className="nav-active-background" />}
                  <Icon size={18} strokeWidth={1.6} /><span>{label}</span>
                </a>
              ))}
            </nav>
            <div className="sidebar-version"><button onClick={() => navigate('about')}><span className="version-dot" />v{version}</button><a href={`${PROJECT_URL}/blob/main/LICENSE`} target="_blank" rel="noreferrer">MIT License<ArrowUpRight size={11} /></a></div>
          </div>
        </aside>

        <div className="main-pane">
          <header className="topbar">
            <div className="breadcrumb">
              <button className="icon-button mobile-menu-button" onClick={() => setSidebarOpen(true)} aria-label="打开导航" aria-controls="app-navigation" aria-expanded={sidebarOpen}><Menu size={20} /></button>
              <span className="breadcrumb-root"><PanelsTopLeft size={15} strokeWidth={1.5} /><span>工作台</span><ChevronRight size={12} /></span>
              <span>{PAGE_NAMES[page]}</span>
            </div>
            <div className="topbar-actions">
              {native ? <span className="preview-label is-native"><ShieldCheck size={13} />桌面版</span>
                : <span className="preview-label">浏览器预览</span>}
              <button className="icon-button theme-button" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} aria-label={theme === 'dark' ? '切换到浅色模式' : '切换到深色模式'} title={theme === 'dark' ? '切换到浅色模式' : '切换到深色模式'}>
                {theme === 'dark'
                  ? <Moon size={18} strokeWidth={1.5} className="theme-icon theme-icon-moon" />
                  : <Sun size={18} strokeWidth={1.5} className="theme-icon theme-icon-sun" />}
              </button>
              <span className="topbar-divider" />
              <a className="github-link" href={PROJECT_URL} target="_blank" rel="noreferrer" aria-label="在 GitHub 查看项目"><GithubIcon size={17} /><span>GitHub</span><ArrowUpRight size={12} /></a>
            </div>
          </header>

          <AnimatePresence mode="wait" initial={false}>
            <motion.main id="main-content" tabIndex={-1} className={`main-content page-${page}`} key={page} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} exit={{ opacity: 0, y: -4 }} transition={{ duration: 0.18 }}>
              {page === 'overview' && <>
                <PageHeading title="游戏概览" description="准备好，以更流畅的方式探索提瓦特。"><div className={`readiness ${!effectiveEnabled || !config.gamePath ? 'is-paused' : ''}`} aria-live="polite">{launchState === 'launching' ? <LoaderCircle size={13} className="spin" /> : <span className={`status-dot ${attachedPid > 0 && effectiveEnabled ? 'pulse' : ''}`} />}{readiness}</div></PageHeading>
                <section className="overview-hero" aria-label="Genshin FPS Unlocker">
                  <motion.img className="hero-image" src="/images/teyvat-landscape.jpg" alt="阳光下的璃月风格山峦、亭台与碧水" initial={{ scale: 1.045 }} animate={{ scale: 1 }} transition={{ duration: 1.8, ease: 'easeOut' }} />
                  <div className="hero-shade" />
                  <motion.div className="hero-copy" initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.65, delay: 0.1 }}><h2>Genshin FPS Unlocker</h2><h3>让每一帧，都不被设限。</h3><p>更高帧率，更自在的冒险。以你喜欢的节奏，探索提瓦特。</p><button className="hero-guide" onClick={() => navigate('guide')}>初次使用？从这里开始<ArrowRight size={14} /></button></motion.div>
                </section>
                <motion.div className="overview-controls" initial={{ opacity: 0, y: 12 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.4, delay: 0.12 }}>
                  <FpsControl value={config.targetFps} enabled={config.enabled} masterEnabled={config.masterEnabled} onChange={(value) => updateConfig('targetFps', value)} onToggle={(value) => updateConfig('enabled', value)} />
                  <section className="control-panel quick-settings"><div className="panel-heading"><h2><SlidersHorizontal size={17} strokeWidth={1.7} />快捷设置</h2><button className="text-button muted all-settings" onClick={() => navigate('settings')}>全部设置<ChevronRight size={13} /></button></div><div className="quick-settings-rows"><ToggleRow icon={ScanLine} title="自动解锁" description="检测到游戏启动后自动应用设置" checked={config.autoWatch} onChange={(value) => updateConfig('autoWatch', value)} /><ToggleRow icon={Power} title="开机自启动" description="登录 Windows 后在后台运行" checked={config.autoStartWithWindows} onChange={(value) => updateConfig('autoStartWithWindows', value)} /><ToggleRow icon={PanelBottomClose} title="启动后最小化到托盘" description="开启后下次启动直接进托盘；关窗/最小化始终会藏到托盘" checked={config.startMinimized} onChange={(value) => updateConfig('startMinimized', value)} /></div></section>
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
              </>}
              {page === 'settings' && <SettingsPage config={config} updateConfig={updateConfig} onPath={() => setModal('path')} onExport={exportConfig} onImport={() => importRef.current?.click()} onReset={() => setModal('reset')} onUninstall={native ? () => setModal('uninstall') : undefined} busy={launchState === 'launching' || elevating} isNative={native} isElevated={isElevated} onRestartElevated={native && !isElevated ? () => void restartElevated() : undefined} elevating={elevating} />}
              {page === 'logs' && <LogsPage logs={logs} onClear={() => setModal('clearLogs')} onExport={exportLogs} isNative={native} onOpenFolder={native ? () => { void nativeInvoke('openLogFolder').catch(() => undefined); } : undefined} />}
              {page === 'guide' && <GuidePage navigate={navigate} onSafety={() => setModal('safety')} isNative={native} />}
              {page === 'about' && <AboutPage onSafety={() => setModal('safety')} version={version} isNative={native} onUninstall={native ? () => setModal('uninstall') : undefined} />}
            </motion.main>
          </AnimatePresence>

          <footer className="status-bar">
            <div className={`status-bar-left ${!config.autoWatch || !config.masterEnabled ? 'monitor-paused' : ''}`}>
              <span className={`status-dot ${attachedPid > 0 ? 'green pulse' : ''}`} />
              <span>{attachedPid > 0 ? '游戏进程已附加' : config.autoWatch && config.masterEnabled ? '自动监视中' : '后台监视已暂停'}</span>
              <span className="status-bar-separator" /><span className="status-target">目标 {config.targetFps} FPS</span>
              {native && (
                <>
                  <span className="status-bar-separator" />
                  <span className={`status-admin ${isElevated ? 'is-on' : 'is-off'}`} title={isElevated ? '已以管理员运行' : '标准用户 — 注入可能失败'}>
                    {isElevated ? <ShieldCheck size={12} /> : <ShieldAlert size={12} />}
                    {isElevated ? '管理员' : '标准权限'}
                  </span>
                </>
              )}
            </div>
            <div className="status-bar-right">
              <span className={`save-status ${saveState === 'error' ? 'save-error' : ''}`} aria-live="polite">
                {saveState === 'saving' ? <LoaderCircle size={12} className="spin" /> : saveState === 'saved' ? <Check size={12} /> : <Info size={12} />}
                {saveState === 'saving' ? '正在保存...' : saveState === 'saved' ? '所有更改已保存' : '保存失败'}
              </span>
              {native && <>
                <span className="status-bar-separator" />
                <button type="button" className="status-tray-btn" title="最小化到系统托盘（关闭窗口同样驻留后台）"
                  onClick={() => { void nativeInvoke('minimizeToTray').catch(() => undefined); }}>
                  <PanelBottomClose size={13} />驻留托盘
                </button>
              </>}
            </div>
          </footer>
        </div>
        <input ref={importRef} type="file" accept="application/json,.json" className="visually-hidden" aria-label="导入配置文件" tabIndex={-1} onChange={importConfig} />
        <Toasts items={toasts} onDismiss={dismissToast} />
        <AnimatePresence>
          {modal === 'path' && <PathDialog key="path" path={config.gamePath} isNative={native} onClose={() => setModal(null)} onSave={savePath} onBrowse={native ? browsePath : undefined} onAutoLocate={native ? autoLocatePath : undefined} />}
          {modal === 'safety' && <SafetyDialog key="safety" isNative={native} onClose={() => setModal(null)} onAcknowledge={async (showOnStartup) => {
            if (native) {
              const state = await nativeInvoke<any>('acknowledgeSafety', { showOnStartup });
              applyNativeState(state as NativeState);
            } else {
              setConfig((c) => ({ ...c, safetyNoticeAcknowledged: true, showSafetyNoticeOnStartup: showOnStartup }));
            }
            setModal(null);
          }} />}
          {modal === 'launch' && <LaunchDialog key="launch" config={config} isNative={native} onClose={() => setModal(null)} onStart={async (dontAskAgain) => {
            if (native) {
              await nativeInvoke('acknowledgeSafety', { showOnStartup: !dontAskAgain });
            } else {
              setConfig((previous) => ({ ...previous, safetyNoticeAcknowledged: true, showSafetyNoticeOnStartup: !dontAskAgain }));
            }
            await beginLaunch();
          }} />}
          {modal === 'reset' && <ConfirmDialog key="reset" title="恢复默认设置？" description="这将覆盖当前解锁器配置为默认值。外观主题与日志不会受影响。建议先导出一份配置备份。" action="恢复默认" onClose={() => setModal(null)} onConfirm={async () => {
            if (native) {
              const state = await nativeInvoke<any>('resetConfig');
              applyNativeState(state as NativeState);
            } else setConfig({ ...DEFAULT_CONFIG });
            setModal(null);
            notify('已恢复默认设置');
          }} />}
          {modal === 'uninstall' && <ConfirmDialog key="uninstall" title="卸载本软件？" description="将启动安装器（Kachina）的卸载向导：清理程序文件、桌面与开始菜单快捷方式、开机自启动注册表项，以及「安装的应用」中的卸载登记。可在向导中选择是否同时删除配置与日志。此操作不可自动撤销。" action="开始卸载" onClose={() => setModal(null)} onConfirm={async () => { setModal(null); await startUninstall(); }} />}
          {modal === 'clearLogs' && <ConfirmDialog key="clear-logs" title="清空日志列表？" description={`当前列表中的 ${logs.length} 条记录将从界面清除（桌面版不会删除磁盘日志文件）。`} action="清空列表" onClose={() => setModal(null)} onConfirm={() => { setLogs([]); setModal(null); notify('日志列表已清空'); }} />}
        </AnimatePresence>
      </div>
    </MotionConfig>
  );
}

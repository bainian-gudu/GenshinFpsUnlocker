import type { ChangeEvent } from 'react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { NAV_ITEMS, PAGE_NAMES } from '../lib/nav';
import type { ToastItem } from '../components/ui';
import type { LogEntry, LogLevel, Page, Theme, UnlockerConfig } from '../lib/config';
import { CONFIG_LABELS, DEFAULT_CONFIG, STORAGE_KEY, downloadFile, getPage, loadConfig, makeLog, parseConfig } from '../lib/config';
import type { NativeState, UpscalerState } from '../lib/native';
import { isNativeHost, nativeGetBootstrap, nativeInvoke, onNativeLog, onNativeNavigate, onNativeState } from '../lib/native';

export type ModalType = 'path' | 'safety' | 'launch' | 'reset' | 'clearLogs' | 'uninstall' | null;
export type LaunchState = 'idle' | 'launching' | 'running';

/**
 * 应用级状态与动作：配置 / 日志 / 主题 / 导航 / 原生桥（WebView2）以及全部交互回调。
 * 原先内联在 App.tsx 中，此处按原样拆出，行为未变；组件通过 AppState 取用。
 */
export function useAppState() {
  const native = isNativeHost();
  const [booting, setBooting] = useState(native);
  const [initial] = useState(() => (native ? { config: { ...DEFAULT_CONFIG }, recovered: false } : loadConfig()));
  const [config, setConfig] = useState<UnlockerConfig>(initial.config);
  const configRef = useRef(config);
  configRef.current = config;
  const [page, setPage] = useState<Page>(getPage);
  const [theme, setTheme] = useState<Theme>(() => {
    try { return localStorage.getItem('genshin-fps-unlocker.theme') === 'dark' ? 'dark' : 'light'; } catch { return 'light'; }
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
  const [upscaler, setUpscaler] = useState<UpscalerState>({
    enabled: false,
    active: false,
    activePid: 0,
    available: false,
    proxyPresent: false,
    dlssRuntimePresent: false,
    gameConfigured: false,
    quality: 'quality',
    status: '组件未检测',
  });
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
    setUpscaler(state.upscaler);
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
        if (boot.logs.length) {
          // 监听器在 bootstrap 之前就挂上了，这期间可能已经收到增量日志；
          // 合并而不是整体替换，并按 id 去重，免得丢掉或重复这几条。
          setLogs((prev) => {
            const seen = new Set(boot.logs.map((entry) => entry.id));
            return [...boot.logs, ...prev.filter((entry) => !seen.has(entry.id))].slice(-200);
          });
        }
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

  // 宿主在窗口进托盘（最小化 / 关窗）时发 navigate，把界面复位到「游戏概览」：
  // 下次从托盘打开主界面不会还停在上次浏览的页面。
  useEffect(() => {
    if (!native) return;
    const offNavigate = onNativeNavigate((next) => navigate(next));
    return () => { offNavigate(); };
  }, [native, navigate]);

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

  return {
    native, booting, config, setConfig, page, theme, setTheme, sidebarOpen, setSidebarOpen,
    modal, setModal, saveState, toasts, dismissToast, logs, setLogs, launchState, statusText,
    attachedPid, currentFps, isElevated, needsAdmin, elevating, upscaler, version, effectiveEnabled, readiness,
    importRef, sidebarRef, addLog, notify, navigate, applyNativeState, updateConfig, beginLaunch,
    restartElevated, startUninstall, handleLaunch, exportConfig, importConfig, exportLogs,
    savePath, browsePath, autoLocatePath,
  };
}

export type AppState = ReturnType<typeof useAppState>;

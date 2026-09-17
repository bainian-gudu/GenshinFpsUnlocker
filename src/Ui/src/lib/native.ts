import type { LogEntry, LogLevel, Page, UnlockerConfig } from './config';
import { asPage, DEFAULT_CONFIG, parseConfig } from './config';

export type NativeState = {
  config: UnlockerConfig;
  statusText: string;
  gamePathStatus: string;
  attachedPid: number;
  currentFps: number;
  stubStatus: number;
  saveState: 'saving' | 'saved' | 'error';
  isNative: true;
  /** 宿主进程是否已提权（管理员） */
  isElevated: boolean;
  /** 解锁注入通常需要管理员；与 isElevated 相反时便于 UI 横幅 */
  needsAdminForUnlock: boolean;
  /** 登录自启动实际生效的方式与提示 */
  autostart: AutostartState;
  version: string;
};

/** 登录自启实际登记的通道：计划任务（最高权限）/ HKCU Run（标准权限）/ 登记失败回退 */
export type AutostartMode = 'disabled' | 'standard' | 'elevated' | 'fallback';

export type AutostartState = {
  mode: AutostartMode;
  /** 需要用户处理的提示（如计划任务没登记成功），没有则为 null */
  notice: string | null;
};

type Pending = {
  resolve: (value: unknown) => void;
  reject: (reason?: unknown) => void;
  timer: number;
};

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (message: unknown) => void;
        addEventListener: (type: string, listener: (event: MessageEvent) => void) => void;
        removeEventListener: (type: string, listener: (event: MessageEvent) => void) => void;
      };
    };
    genshinNative?: {
      postMessage: (message: string) => void;
    };
  }
}

const pending = new Map<string, Pending>();
let seq = 0;
let listenersReady = false;
const stateListeners = new Set<(state: NativeState) => void>();
const logListeners = new Set<(entry: LogEntry) => void>();
const navigateListeners = new Set<(page: Page) => void>();

export function isNativeHost(): boolean {
  return Boolean(window.chrome?.webview) || Boolean(window.genshinNative);
}

function postRaw(payload: Record<string, unknown>) {
  const text = JSON.stringify(payload);
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage(text);
    return;
  }
  if (window.genshinNative?.postMessage) {
    window.genshinNative.postMessage(text);
    return;
  }
  throw new Error('原生宿主不可用');
}

function ensureListeners() {
  if (listenersReady) return;
  listenersReady = true;
  const handler = (event: MessageEvent) => {
    let data: any = event.data;
    try {
      if (typeof data === 'string') data = JSON.parse(data);
    } catch {
      return;
    }
    if (!data || typeof data !== 'object') return;

    if (data.type === 'response' && data.id) {
      const item = pending.get(String(data.id));
      if (!item) return;
      window.clearTimeout(item.timer);
      pending.delete(String(data.id));
      if (data.ok) item.resolve(data.result);
      else item.reject(new Error(String(data.error ?? '原生调用失败')));
      return;
    }

    if (data.type === 'state' && data.state) {
      const state = normalizeState(data.state);
      stateListeners.forEach((fn) => fn(state));
      return;
    }

    if (data.type === 'log' && data.entry) {
      const entry = normalizeLog(data.entry);
      logListeners.forEach((fn) => fn(entry));
      return;
    }

    // 宿主主动切页（窗口进托盘后复位到概览页）；page 非法时 asPage 收敛为 overview
    if (data.type === 'navigate') {
      const page = asPage(data.page);
      navigateListeners.forEach((fn) => fn(page));
    }
  };

  // 只挂 chrome.webview 通道：宿主的 PostWebMessageAsJson 消息同时可见于
  // window 'message'（两者语义等价），双挂会让同一 hostMessage 被 handler 处理两次
  // （重复响应无害，但增量日志会被追加两遍）。window 路径一律忽略。
  window.chrome?.webview?.addEventListener('message', handler);
}

function normalizeState(raw: any): NativeState {
  const config = parseConfig({ ...DEFAULT_CONFIG, ...(raw.config ?? {}) });
  const isElevated = Boolean(raw.isElevated);
  return {
    config,
    statusText: String(raw.statusText ?? ''),
    gamePathStatus: String(raw.gamePathStatus ?? ''),
    attachedPid: Number(raw.attachedPid ?? 0),
    currentFps: Number(raw.currentFps ?? 0),
    stubStatus: Number(raw.stubStatus ?? 0),
    saveState: raw.saveState === 'saving' || raw.saveState === 'error' ? raw.saveState : 'saved',
    isNative: true,
    isElevated,
    needsAdminForUnlock: raw.needsAdminForUnlock != null ? Boolean(raw.needsAdminForUnlock) : !isElevated,
    autostart: {
      mode: (['standard', 'elevated', 'fallback'] as const).includes(raw.autostart?.mode)
        ? (raw.autostart.mode as AutostartMode)
        : 'disabled',
      notice: typeof raw.autostart?.notice === 'string' && raw.autostart.notice ? raw.autostart.notice : null,
    },
    version: String(raw.version ?? '1.0.0'),
  };
}

function normalizeLog(raw: any): LogEntry {
  return {
    id: String(raw.id ?? `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`),
    timestamp: String(raw.timestamp ?? new Date().toISOString()),
    level: (['Trace', 'Debug', 'Info', 'Warn', 'Error'].includes(raw.level) ? raw.level : 'Info') as LogLevel,
    message: String(raw.message ?? ''),
  };
}

export function onNativeState(listener: (state: NativeState) => void): () => void {
  ensureListeners();
  stateListeners.add(listener);
  return () => stateListeners.delete(listener);
}

export function onNativeLog(listener: (entry: LogEntry) => void): () => void {
  ensureListeners();
  logListeners.add(listener);
  return () => logListeners.delete(listener);
}

/** 宿主请求切换页面（见 UiBridge.ResetUiPage）。 */
export function onNativeNavigate(listener: (page: Page) => void): () => void {
  ensureListeners();
  navigateListeners.add(listener);
  return () => navigateListeners.delete(listener);
}

export function nativeInvoke<T = unknown>(method: string, params: Record<string, unknown> = {}, timeoutMs = 30000): Promise<T> {
  ensureListeners();
  if (!isNativeHost()) return Promise.reject(new Error('非原生环境'));
  const id = `c${++seq}`;
  return new Promise<T>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      pending.delete(id);
      reject(new Error(`原生调用超时: ${method}`));
    }, timeoutMs);
    pending.set(id, {
      resolve: (v) => resolve(v as T),
      reject,
      timer,
    });
    try {
      postRaw({ type: 'call', id, method, params });
    } catch (error) {
      window.clearTimeout(timer);
      pending.delete(id);
      reject(error);
    }
  });
}

export async function nativeGetBootstrap(): Promise<{ state: NativeState; logs: LogEntry[] }> {
  const result = await nativeInvoke<{ state: any; logs?: any[] }>('getBootstrap');
  return {
    state: normalizeState(result.state),
    logs: Array.isArray(result.logs) ? result.logs.map(normalizeLog) : [],
  };
}

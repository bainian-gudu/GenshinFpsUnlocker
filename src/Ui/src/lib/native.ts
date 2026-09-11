import type { LogEntry, LogLevel, UnlockerConfig } from './config';
import { DEFAULT_CONFIG, parseConfig } from './config';

export type NativeState = {
  config: UnlockerConfig;
  statusText: string;
  gamePathStatus: string;
  attachedPid: number;
  currentFps: number;
  stubStatus: number;
  saveState: 'saving' | 'saved' | 'error';
  isNative: true;
  version: string;
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
    }
  };

  window.chrome?.webview?.addEventListener('message', handler);
  window.addEventListener('message', handler as EventListener);
}

function normalizeState(raw: any): NativeState {
  const config = parseConfig({ ...DEFAULT_CONFIG, ...(raw.config ?? {}) });
  return {
    config,
    statusText: String(raw.statusText ?? ''),
    gamePathStatus: String(raw.gamePathStatus ?? ''),
    attachedPid: Number(raw.attachedPid ?? 0),
    currentFps: Number(raw.currentFps ?? 0),
    stubStatus: Number(raw.stubStatus ?? 0),
    saveState: raw.saveState === 'saving' || raw.saveState === 'error' ? raw.saveState : 'saved',
    isNative: true,
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

export function nativeInvoke<T = unknown>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  ensureListeners();
  if (!isNativeHost()) return Promise.reject(new Error('非原生环境'));
  const id = `c${++seq}`;
  return new Promise<T>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      pending.delete(id);
      reject(new Error(`原生调用超时: ${method}`));
    }, 30000);
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

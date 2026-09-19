import type { GameId, LogEntry, LogLevel, Page, UnlockerConfig } from './config';
import { asPage, isGameId, parseConfig } from './config';

export type NativeState = {
  config: UnlockerConfig;
  /**
   * 界面 / 托盘当前展示的游戏。自动跟随运行中的游戏时它与 config.activeGame
   * （用户保存的选择）不同；前端按这个字段切页，不把跟随状态写回用户选择。
   */
  displayGame: GameId;
  statusText: string;
  gamePathStatus: string;
  /** 当前检测到正在运行的游戏；没有游戏进程时为 null */
  runningGame: GameId | null;
  /** 当前已注入（附着）的游戏；未附着时为 null */
  attachedGame: GameId | null;
  attachedPid: number;
  currentFps: number;
  /** Stub 生命周期状态：0 未注入 / 1 解析中 / 2 就绪 / 3 失败 / 4 退出中 */
  stubStatus: number;
  /** Stub 上报的错误码，0 表示无错误（0xE001 起为自定义错误） */
  stubLastError: number;
  /** 反虚化就绪位掩码：bit0 反角色虚化 / bit1 水下马赛克 / bit2 马赛克已生效 */
  antiBlurState: number;
  /** UID 隐藏状态位掩码：bit0 已就绪 / bit1 隐藏生效中 */
  hideUidState: number;
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

  // 只挂 chrome.webview 通道：官方文档中宿主 PostWebMessageAsJson 的接收端
  // 就是 chrome.webview 的 message 事件（含官方示例）。window 'message' 在本
  // 应用没有任何生产者，挂上属于冗余，且其派发语义未被子文档明确承诺，
  // 移除后可排除同一条宿主消息被 handler 处理多遍的隐患。
  window.chrome?.webview?.addEventListener('message', handler);
}

function normalizeState(raw: any): NativeState {
  // 宿主当前下发的是 v1 扁平 config（原神），parseConfig 会把它迁移到 games.genshin，
  // 星穹铁道保留默认值；等宿主改为下发 games 结构后无需再改这里。
  const config = parseConfig(raw.config ?? {});
  const isElevated = Boolean(raw.isElevated);
  return {
    config,
    displayGame: isGameId(raw.displayGame) ? raw.displayGame : config.activeGame,
    statusText: String(raw.statusText ?? ''),
    gamePathStatus: String(raw.gamePathStatus ?? ''),
    runningGame: isGameId(raw.runningGame) ? raw.runningGame : null,
    attachedGame: isGameId(raw.attachedGame) ? raw.attachedGame : null,
    attachedPid: Number(raw.attachedPid ?? 0),
    currentFps: Number(raw.currentFps ?? 0),
    stubStatus: Number(raw.stubStatus ?? 0),
    stubLastError: Number(raw.stubLastError ?? 0),
    antiBlurState: Number(raw.antiBlurState ?? 0),
    hideUidState: Number(raw.hideUidState ?? 0),
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
    game: isGameId(raw.game) ? raw.game : undefined,
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

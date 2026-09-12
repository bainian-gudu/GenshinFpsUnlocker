export const PROJECT_URL = 'https://github.com/bainian-gudu/GenshinFpsUnlocker';
export const STORAGE_KEY = 'genshin-fps-unlocker.config.v1';
export const DEMO_GAME_PATH = 'D:\\Games\\Genshin Impact\\Genshin Impact Game\\YuanShen.exe';

export type Theme = 'dark' | 'light';
/** 全部页面 id；顺序即侧栏主导航 + 次级导航的顺序。 */
export const PAGES = ['overview', 'settings', 'logs', 'guide', 'about'] as const;
export type Page = (typeof PAGES)[number];
export type LogLevel = 'Info' | 'Warn' | 'Error' | 'Debug' | 'Trace';

export interface UnlockerConfig {
  targetFps: number;
  enabled: boolean;
  masterEnabled: boolean;
  autoWatch: boolean;
  antiBlurPerspective: boolean;
  antiBlurDiveMosaic: boolean;
  startMinimized: boolean;
  autoStartWithWindows: boolean;
  pollIntervalMs: number;
  gamePath: string | null;
  safetyNoticeAcknowledged: boolean;
  showSafetyNoticeOnStartup: boolean;
  defenderExclusionApplied: boolean;
  debugLogging: boolean;
  logLevel: LogLevel;
  logRetainDays: number;
  createDesktopShortcut: boolean;
  suppressAdminHint: boolean;
}

export type UpdateConfig = <K extends keyof UnlockerConfig>(key: K, value: UnlockerConfig[K]) => void;

export interface LogEntry {
  id: string;
  timestamp: string;
  level: LogLevel;
  message: string;
}

export const DEFAULT_CONFIG: UnlockerConfig = {
  targetFps: 120,
  enabled: true,
  masterEnabled: true,
  autoWatch: true,
  antiBlurPerspective: false,
  antiBlurDiveMosaic: false,
  startMinimized: false,
  autoStartWithWindows: false,
  pollIntervalMs: 1000,
  gamePath: DEMO_GAME_PATH,
  safetyNoticeAcknowledged: false,
  showSafetyNoticeOnStartup: true,
  defenderExclusionApplied: false,
  debugLogging: true,
  logLevel: 'Debug',
  logRetainDays: 14,
  createDesktopShortcut: true,
  suppressAdminHint: false,
};

export const CONFIG_LABELS: Record<keyof UnlockerConfig, string> = {
  targetFps: '目标帧率',
  enabled: '帧率解锁',
  masterEnabled: '解锁服务总开关',
  autoWatch: '自动解锁',
  antiBlurPerspective: '反角色虚化',
  antiBlurDiveMosaic: '移除水下马赛克',
  startMinimized: '启动后最小化到托盘',
  autoStartWithWindows: '开机自启动',
  pollIntervalMs: '进程检测间隔',
  gamePath: '游戏路径',
  safetyNoticeAcknowledged: '安全声明确认',
  showSafetyNoticeOnStartup: '启动时显示安全声明',
  defenderExclusionApplied: 'Defender 排除状态',
  debugLogging: '调试日志',
  logLevel: '最低日志级别',
  logRetainDays: '日志保留天数',
  createDesktopShortcut: '桌面快捷方式',
  suppressAdminHint: '隐藏管理员权限提醒',
};

export function isValidGamePath(path: string): boolean {
  return /^[a-z]:[\\/](?:[^<>:"|?*\r\n]+[\\/])?(?:YuanShen|GenshinImpact)\.exe$/i.test(path);
}

export function cleanPath(path: string): string {
  return path.trim().replace(/^"(.*)"$/, '$1');
}

export function parseConfig(value: unknown): UnlockerConfig {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('配置文件必须是一个 JSON 对象。');
  }
  const input = value as Record<string, unknown>;
  if (!Number.isInteger(input.targetFps) || Number(input.targetFps) < 1 || Number(input.targetFps) > 540) {
    throw new Error('targetFps 必须是 1 至 540 之间的整数。');
  }
  const next = { ...DEFAULT_CONFIG };
  for (const key of Object.keys(DEFAULT_CONFIG) as (keyof UnlockerConfig)[]) {
    if (!(key in input)) continue;
    const item = input[key];
    if (typeof DEFAULT_CONFIG[key] === 'boolean') {
      if (typeof item !== 'boolean') throw new Error(`${key} 必须为 true 或 false。`);
      Object.assign(next, { [key]: item });
    }
  }
  next.targetFps = Number(input.targetFps);
  for (const [key, min, max] of [
    ['pollIntervalMs', 200, 10000],
    ['logRetainDays', 1, 90],
  ] as const) {
    if (!(key in input)) continue;
    if (!Number.isInteger(input[key]) || Number(input[key]) < min || Number(input[key]) > max) {
      throw new Error(`${key} 必须是 ${min} 至 ${max} 之间的整数。`);
    }
    next[key] = Number(input[key]);
  }
  if ('gamePath' in input) {
    if (input.gamePath === null || input.gamePath === '') next.gamePath = null;
    else if (typeof input.gamePath === 'string' && isValidGamePath(cleanPath(input.gamePath))) {
      next.gamePath = cleanPath(input.gamePath);
    } else {
      throw new Error('游戏路径无效，请使用 YuanShen.exe 或 GenshinImpact.exe 的 Windows 完整路径。');
    }
  }
  if ('logLevel' in input) {
    if (typeof input.logLevel !== 'string' || !['Trace', 'Debug', 'Info', 'Warn', 'Error'].includes(input.logLevel)) {
      throw new Error('不支持的日志级别。');
    }
    next.logLevel = input.logLevel as LogLevel;
  }
  return next;
}

export function loadConfig(): { config: UnlockerConfig; recovered: boolean } {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    return { config: stored ? parseConfig(JSON.parse(stored)) : { ...DEFAULT_CONFIG }, recovered: false };
  } catch {
    return { config: { ...DEFAULT_CONFIG }, recovered: true };
  }
}

export function downloadFile(content: string, name: string, type = 'application/json') {
  const url = URL.createObjectURL(new Blob([content], { type: `${type};charset=utf-8` }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export function makeLog(level: LogLevel, message: string): LogEntry {
  return {
    id: `${Date.now()}-${Math.random().toString(36).slice(2, 9)}`,
    timestamp: new Date().toISOString(),
    level,
    message,
  };
}

export function formatTime(timestamp: string): string {
  return new Date(timestamp).toLocaleTimeString('zh-CN', { hour12: false });
}

/** 把任意来源（地址栏 hash、宿主消息）的值收敛成合法页面，非法一律回概览页。 */
export function asPage(value: unknown): Page {
  return (PAGES as readonly string[]).includes(value as string) ? (value as Page) : 'overview';
}

export function getPage(): Page {
  return asPage(window.location.hash.slice(1));
}

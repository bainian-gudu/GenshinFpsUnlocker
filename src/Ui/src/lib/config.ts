export const PROJECT_URL = 'https://github.com/bainian-gudu/HoYoEnhance';
/**
 * 应用名称：品牌区、窗口标题、关于页与安全声明共用这几个常量，改名只动这里。
 * 现在同时支持原神与崩坏：星穹铁道，所以不再沿用只带一款游戏的名字。
 */
export const BRAND_NAME = 'HoYoEnhance';
export const BRAND_SUB = 'FPS Unlocker';
/** 窗口标题、主视觉标题与日志里显示的产品名。 */
export const APP_NAME = BRAND_NAME;
/** 配置存储键（v2：每个游戏一份 games[game] 档案）。旧键只用于一次性迁移读取。 */
export const STORAGE_KEY = 'genshin-fps-unlocker.config.v2';
export const LEGACY_STORAGE_KEY = 'genshin-fps-unlocker.config.v1';

export type Theme = 'dark' | 'light';
/** 全部页面 id；顺序即侧栏主导航 + 次级导航的顺序。 */
export const PAGES = ['overview', 'settings', 'logs', 'guide', 'about'] as const;
export type Page = (typeof PAGES)[number];
export type LogLevel = 'Info' | 'Warn' | 'Error' | 'Debug' | 'Trace';

/** 支持的游戏：原神与崩坏：星穹铁道，各自持有一份互不影响的配置。 */
export const GAME_IDS = ['genshin', 'starRail'] as const;
export type GameId = (typeof GAME_IDS)[number];

/** 画面效果对应的配置键：每个游戏只列出自己注入模块里真实存在的那几项。 */
export type GameFeatureKey = 'hideUid' | 'antiBlurPerspective' | 'antiBlurDiveMosaic';

/** 一条画面效果。名称与说明由该游戏自己的注入模块决定，两个游戏之间不共用。 */
export interface GameFeatureMeta {
  key: GameFeatureKey;
  title: string;
  description: string;
}

/** 某个游戏的注入模块：两个游戏的模块相互独立，连文件名都不一样。 */
export interface GameInjectionMeta {
  /** 注入的模块（DLL）文件名，界面原样显示，便于排查。 */
  module: string;
  /** 该模块提供的画面效果条目。 */
  features: readonly GameFeatureMeta[];
}

export interface GameMeta {
  id: GameId;
  /** 完整名称：标题、游戏库、日志里使用。 */
  name: string;
  /** 窄容器（侧栏分组、顶部切换器）使用的短名。 */
  short: string;
  /** 判定游戏路径用的可执行文件名（不含扩展名）。 */
  exeNames: readonly string[];
  /**
   * 帧率上限被锁死的游戏：星穹铁道走注册表解锁、只支持 120 帧，
   * 界面上不提供滑块与自定义档位，读入配置时也统一归一到这个值。
   */
  fpsLock?: { value: number; notes: readonly string[] };
  /** 网页预览里的示例路径。 */
  demoPath: string;
  /** 路径输入框占位符。 */
  pathPlaceholder: string;
  /** 「游戏安装位置」面板的说明。 */
  pathHint: string;
  /** 主视觉与使用指南文案（主视觉标题统一用应用名，这里只放该游戏自己的文案）。 */
  hero: { tagline: string; copy: string; guideKicker: string; guideHeadline: string };
  /** 该游戏自己的注入模块与画面效果。 */
  injection: GameInjectionMeta;
}

export const GAME_META: Record<GameId, GameMeta> = {
  genshin: {
    id: 'genshin',
    name: '原神',
    short: '原神',
    exeNames: ['YuanShen', 'GenshinImpact'],
    demoPath: 'D:\\Games\\Genshin Impact\\Genshin Impact Game\\YuanShen.exe',
    pathPlaceholder: 'D:\\Games\\Genshin Impact\\Genshin Impact Game\\YuanShen.exe',
    pathHint: '请选择游戏本体，而非米哈游启动器。支持国服 YuanShen.exe 与国际服 GenshinImpact.exe。',
    hero: {
      tagline: '让每一帧，都不被设限。',
      copy: '更高帧率，更自在的冒险。以你喜欢的节奏，探索提瓦特。',
      guideKicker: 'QUICK START',
      guideHeadline: '下一段旅程，更顺畅一点。',
    },
    // 原神的注入模块与命名保持现状：FpsUnlockerStub.dll 里的三项效果原名不动。
    injection: {
      module: 'FpsUnlockerStub.dll',
      features: [
        { key: 'antiBlurPerspective', title: '反角色虚化', description: '开启后镜头拉近时，角色不再透明化（虚化效果被跳过）' },
        { key: 'antiBlurDiveMosaic', title: '移除水下马赛克', description: '开启后角色入水时，不再显示马赛克虚化效果' },
        { key: 'hideUid', title: '隐藏 UID', description: '隐藏游戏水印与资料页上的 UID 文本' },
      ],
    },
  },
  starRail: {
    id: 'starRail',
    name: '崩坏：星穹铁道',
    short: '星穹铁道',
    exeNames: ['StarRail'],
    // 星穹铁道不改内存、不注入进程，直接写注册表里的画面设置；只支持 120 帧。
    fpsLock: {
      value: 120,
      notes: [
        '星穹铁道通过注册表解锁帧率，不注入游戏进程，只支持 120 FPS。',
        '开启后会先检查注册表：已经是 120 FPS 就不覆盖，否则写入 120。',
        '关闭开关不会回写注册表，游戏沿用现有设置；改完需重启游戏生效。',
      ],
    },
    demoPath: 'D:\\Games\\Star Rail\\Game\\StarRail.exe',
    pathPlaceholder: 'D:\\Games\\Star Rail\\Game\\StarRail.exe',
    pathHint: '请选择游戏本体 StarRail.exe（国服与国际服同名），不要选择启动器或下载器。',
    hero: {
      tagline: '让每一帧，都跟得上列车。',
      copy: '把星穹列车开到更高帧率，让每一次跃迁都顺滑到底。',
      guideKicker: 'QUICK START',
      guideHeadline: '下一站，更顺畅一点。',
    },
    // 星穹铁道走自己独立的注入模块，效果名称与条目都不沿用原神那一套。
    injection: {
      module: 'StarRailStub.dll',
      features: [
        { key: 'antiBlurPerspective', title: '解除角色虚化', description: '开启后镜头拉近时，角色不再透明化' },
        { key: 'hideUid', title: '隐藏 UID 水印', description: '隐藏星穹铁道界面上的 UID 水印文本' },
      ],
    },
  },
};

/** 某个游戏单独保存的配置项。 */
export interface GameProfile {
  targetFps: number;
  enabled: boolean;
  hideUid: boolean;
  antiBlurPerspective: boolean;
  antiBlurDiveMosaic: boolean;
  /** 该游戏主程序的完整 Windows 路径。 */
  gamePath: string | null;
}

export type UpdateGameConfig = <K extends keyof GameProfile>(key: K, value: GameProfile[K]) => void;

export interface UnlockerConfig {
  /** 当前正在配置的游戏：概览 / 设置 / 使用指南三页跟着它切换。 */
  activeGame: GameId;
  /** 每个游戏一份的独立配置。 */
  games: Record<GameId, GameProfile>;
  /** 以下为两个游戏共用的解锁器级设置。 */
  masterEnabled: boolean;
  autoWatch: boolean;
  startMinimized: boolean;
  autoStartWithWindows: boolean;
  autoStartAsAdministrator: boolean;
  pollIntervalMs: number;
  safetyNoticeAcknowledged: boolean;
  showSafetyNoticeOnStartup: boolean;
  defenderExclusionApplied: boolean;
  debugLogging: boolean;
  logLevel: LogLevel;
  logRetainDays: number;
  suppressAdminHint: boolean;
}

export type UpdateConfig = <K extends keyof UnlockerConfig>(key: K, value: UnlockerConfig[K]) => void;

export interface LogEntry {
  id: string;
  timestamp: string;
  level: LogLevel;
  message: string;
  /** 归属游戏；解锁器自身的日志（启动、自启、配置）没有这一项。 */
  game?: GameId;
}

export const DEFAULT_CONFIG: UnlockerConfig = {
  activeGame: 'genshin',
  games: {
    genshin: {
      targetFps: 120,
      enabled: true,
      hideUid: false,
      antiBlurPerspective: false,
      antiBlurDiveMosaic: false,
      gamePath: GAME_META.genshin.demoPath,
    },
    starRail: {
      targetFps: 120,
      enabled: true,
      hideUid: false,
      antiBlurPerspective: false,
      antiBlurDiveMosaic: false,
      gamePath: GAME_META.starRail.demoPath,
    },
  },
  masterEnabled: true,
  autoWatch: true,
  startMinimized: false,
  autoStartWithWindows: false,
  autoStartAsAdministrator: false,
  pollIntervalMs: 1000,
  safetyNoticeAcknowledged: false,
  showSafetyNoticeOnStartup: true,
  defenderExclusionApplied: false,
  debugLogging: true,
  logLevel: 'Debug',
  logRetainDays: 14,
  suppressAdminHint: false,
};

/** 默认配置的深拷贝（games 是嵌套对象，浅拷贝会共享同一份档案）。 */
export function createDefaultConfig(): UnlockerConfig {
  return {
    ...DEFAULT_CONFIG,
    games: {
      genshin: { ...DEFAULT_CONFIG.games.genshin },
      starRail: { ...DEFAULT_CONFIG.games.starRail },
    },
  };
}

export const CONFIG_LABELS: Record<keyof UnlockerConfig, string> = {
  activeGame: '当前游戏',
  games: '游戏配置',
  masterEnabled: '解锁服务总开关',
  autoWatch: '自动解锁',
  startMinimized: '启动后最小化到托盘',
  autoStartWithWindows: '开机自启动',
  autoStartAsAdministrator: '启动时自动以管理员权限运行',
  pollIntervalMs: '进程检测间隔',
  safetyNoticeAcknowledged: '安全声明确认',
  showSafetyNoticeOnStartup: '启动时显示安全声明',
  defenderExclusionApplied: 'Defender 排除状态',
  debugLogging: '调试日志',
  logLevel: '最低日志级别',
  logRetainDays: '日志保留天数',
  suppressAdminHint: '隐藏管理员权限提醒',
};

export const GAME_CONFIG_LABELS: Record<keyof GameProfile, string> = {
  targetFps: '目标帧率',
  enabled: '帧率解锁',
  hideUid: '隐藏 UID',
  antiBlurPerspective: '反角色虚化',
  antiBlurDiveMosaic: '移除水下马赛克',
  gamePath: '游戏路径',
};

export function isGameId(value: unknown): value is GameId {
  return typeof value === 'string' && (GAME_IDS as readonly string[]).includes(value);
}

/** 按游戏校验路径结尾的可执行文件名。 */
export function isValidGamePath(path: string, game: GameId = 'genshin'): boolean {
  const names = GAME_META[game].exeNames.join('|');
  return new RegExp(`^[a-z]:[\\\\/](?:[^<>:"|?*\\r\\n]+[\\\\/])?(?:${names})\\.exe$`, 'i').test(path);
}

/** 路径结尾可执行文件名的可读提示，例如「YuanShen.exe 或 GenshinImpact.exe」。 */
export function gamePathHint(game: GameId): string {
  return GAME_META[game].exeNames.map((name) => `${name}.exe`).join(' 或 ');
}

export function cleanPath(path: string): string {
  return path.trim().replace(/^"(.*)"$/, '$1');
}

/** 单个游戏的档案解析：只认 GameProfile 里存在的键，缺失的沿用 fallback。 */
function parseGameProfile(value: unknown, game: GameId, fallback: GameProfile): GameProfile {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${GAME_META[game].name} 的配置必须是一个 JSON 对象。`);
  }
  const input = value as Record<string, unknown>;
  const next: GameProfile = { ...fallback };
  for (const key of Object.keys(next) as (keyof GameProfile)[]) {
    if (!(key in input)) continue;
    const item = input[key];
    if (typeof next[key] !== 'boolean') continue;
    if (typeof item !== 'boolean') throw new Error(`${key} 必须为 true 或 false。`);
    Object.assign(next, { [key]: item });
  }
  if ('targetFps' in input) {
    if (!Number.isInteger(input.targetFps) || Number(input.targetFps) < 1 || Number(input.targetFps) > 540) {
      throw new Error('targetFps 必须是 1 至 540 之间的整数。');
    }
    next.targetFps = Number(input.targetFps);
  }
  // 锁定帧率的游戏（星穹铁道）只认自己的固定值，旧配置里的其它数值一律归一。
  const lock = GAME_META[game].fpsLock;
  if (lock) next.targetFps = lock.value;
  if ('gamePath' in input) {
    if (input.gamePath === null || input.gamePath === '') next.gamePath = null;
    else if (typeof input.gamePath === 'string' && isValidGamePath(cleanPath(input.gamePath), game)) {
      next.gamePath = cleanPath(input.gamePath);
    } else {
      throw new Error(`游戏路径无效，请使用 ${gamePathHint(game)} 的 Windows 完整路径。`);
    }
  }
  return next;
}

export function parseConfig(value: unknown): UnlockerConfig {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error('配置文件必须是一个 JSON 对象。');
  }
  const input = value as Record<string, unknown>;
  const next = createDefaultConfig();
  // 只认 DEFAULT_CONFIG 里存在的共享字段：旧导出文件里多出来的字段
  // 会在这里被当未知键忽略，不影响导入。
  for (const key of Object.keys(DEFAULT_CONFIG) as (keyof UnlockerConfig)[]) {
    if (key === 'games' || key === 'activeGame') continue;
    if (!(key in input)) continue;
    const item = input[key];
    if (typeof DEFAULT_CONFIG[key] === 'boolean') {
      if (typeof item !== 'boolean') throw new Error(`${key} 必须为 true 或 false。`);
      Object.assign(next, { [key]: item });
    }
  }
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
  if ('logLevel' in input) {
    if (typeof input.logLevel !== 'string' || !['Trace', 'Debug', 'Info', 'Warn', 'Error'].includes(input.logLevel)) {
      throw new Error('不支持的日志级别。');
    }
    next.logLevel = input.logLevel as LogLevel;
  }
  if ('activeGame' in input) {
    if (!isGameId(input.activeGame)) throw new Error('activeGame 必须是 genshin 或 starRail。');
    next.activeGame = input.activeGame;
  }
  if ('games' in input) {
    const raw = input.games;
    if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw new Error('games 必须是一个 JSON 对象。');
    for (const id of GAME_IDS) {
      const profile = (raw as Record<string, unknown>)[id];
      if (profile == null) continue;
      next.games[id] = parseGameProfile(profile, id, next.games[id]);
    }
  } else {
    // v1 扁平结构（旧的 config.json、宿主当前下发的 config）：整段按原神档案迁移，
    // 星穹铁道保持默认值，不会因为读旧配置而被清空。
    next.games.genshin = parseGameProfile(input, 'genshin', next.games.genshin);
  }
  return next;
}

export function loadConfig(): { config: UnlockerConfig; recovered: boolean } {
  try {
    const stored = localStorage.getItem(STORAGE_KEY) ?? localStorage.getItem(LEGACY_STORAGE_KEY);
    return { config: stored ? parseConfig(JSON.parse(stored)) : createDefaultConfig(), recovered: false };
  } catch {
    return { config: createDefaultConfig(), recovered: true };
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

export function makeLog(level: LogLevel, message: string, game?: GameId): LogEntry {
  return {
    id: `${Date.now()}-${Math.random().toString(36).slice(2, 9)}`,
    timestamp: new Date().toISOString(),
    level,
    message,
    game,
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

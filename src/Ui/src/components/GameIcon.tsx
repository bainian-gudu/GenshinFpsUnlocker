/** 游戏图标：原神与崩坏：星穹铁道的官方应用图标，按尺寸取用。 */
import { EyeOff, WandSparkles } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import type { CSSProperties } from 'react';
import type { GameFeatureKey, GameId } from '../lib/config';

const ICON_SRC: Record<GameId, string> = {
  genshin: '/images/game-icon.webp',
  starRail: '/images/starrail-icon.webp',
};

/** 画面效果行的图标：按功能键取用，效果名称由各游戏自己的注入模块给出。 */
export const FEATURE_ICONS: Record<GameFeatureKey, LucideIcon> = {
  hideUid: EyeOff,
  antiBlurPerspective: WandSparkles,
  antiBlurDiveMosaic: WandSparkles,
};

/** 小尺寸游戏标记（侧栏分组、顶部切换器、状态栏）。 */
export function GameMark({ game, size = 20, className = '' }: { game: GameId; size?: number; className?: string }) {
  return (
    <img
      className={`game-mark ${className}`}
      src={ICON_SRC[game]}
      alt=""
      aria-hidden="true"
      draggable={false}
      style={{ '--game-mark-size': `${size}px` } as CSSProperties}
    />
  );
}

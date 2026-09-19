/** 顶部游戏切换器：只在「游戏概览 / 游戏设置 / 使用指南」这些每个游戏一份的页面出现。 */
import { GAME_IDS, GAME_META } from '../lib/config';
import type { GameId } from '../lib/config';
import { GameMark } from './GameIcon';

export function GameSwitcher({ game, onChange }: { game: GameId; onChange: (game: GameId) => void }) {
  return (
    <div className="game-switcher" role="group" aria-label="切换游戏">
      {GAME_IDS.map((id) => {
        const active = id === game;
        return (
          <button
            key={id}
            type="button"
            className={`game-switcher-item ${active ? 'is-active' : ''}`}
            aria-pressed={active}
            title={`${GAME_META[id].name} · 独立配置`}
            onClick={() => { if (!active) onChange(id); }}
          >
            <GameMark game={id} size={19} />
            <span>{GAME_META[id].short}</span>
          </button>
        );
      })}
    </div>
  );
}

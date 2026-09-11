import {
  CircleHelp, Folder, Gauge, Info, LoaderCircle, Monitor,
  Play, Power, Rocket, ScanLine, Settings2, SlidersHorizontal, Users,
} from 'lucide-react';
import type { UnlockerConfig } from '../lib/config';
import { BrandMark } from './Brand';
import { Toggle } from './ui';

const FPS_PRESETS = [60, 120, 144] as const;

export function OverviewDashboard({
  config,
  updateConfig,
  launchState,
  attachedPid,
  currentFps,
  statusText,
  readiness,
  effectiveEnabled,
  onLaunch,
  onPath,
  onNavigateSettings,
  onSafety,
}: {
  config: UnlockerConfig;
  updateConfig: <K extends keyof UnlockerConfig>(key: K, value: UnlockerConfig[K]) => void;
  launchState: 'idle' | 'launching' | 'running';
  attachedPid: number;
  currentFps: number;
  statusText: string;
  readiness: string;
  effectiveEnabled: boolean;
  onLaunch: () => void;
  onPath: () => void;
  onNavigateSettings: () => void;
  onSafety: () => void;
}) {
  const running = attachedPid > 0 || launchState === 'running';
  const launching = launchState === 'launching';
  const region = config.gamePath?.toLowerCase().includes('genshinimpact.exe') ? '国际服' : '国服';

  return (
    <div className="yae-overview">
      <section className="yae-hero" aria-label="GenshinFpsUnlocker">
        <img className="yae-hero-img" src="/images/teyvat-landscape.jpg" alt="" draggable={false} />
        <div className="yae-hero-shade" />
        <div className="yae-hero-copy">
          <h2>GenshinFpsUnlocker</h2>
          <h3>解锁帧率 · 畅享提瓦特</h3>
          <p>让每一帧，都是更清晰的风景</p>
        </div>
      </section>

      <div className="yae-mid-grid">
        <section className="yae-card yae-fps-card" aria-label="目标帧率">
          <div className="yae-card-head">
            <h3><Gauge size={16} strokeWidth={1.8} />目标帧率 (FPS)</h3>
            <button type="button" className="yae-info-btn" title="建议选择接近显示器刷新率的数值" aria-label="帧率说明">
              <CircleHelp size={14} />
            </button>
          </div>
          <div className="yae-fps-presets" role="group" aria-label="帧率预设">
            {FPS_PRESETS.map((fps) => (
              <button
                key={fps}
                type="button"
                className={config.targetFps === fps ? 'is-active' : ''}
                aria-pressed={config.targetFps === fps}
                onClick={() => updateConfig('targetFps', fps)}
              >
                {fps}
              </button>
            ))}
          </div>
          <button
            type="button"
            className={`yae-launch-btn ${running ? 'is-running' : ''}`}
            onClick={onLaunch}
            disabled={launching}
          >
            {launching ? <LoaderCircle size={18} className="spin" /> : <Play size={16} fill="currentColor" />}
            <span>{launching ? '启动中…' : running ? '再次启动' : '启动'}</span>
          </button>
          <div className={`yae-ready-line ${running && effectiveEnabled ? 'is-ok' : ''}`}>
            <span className={`yae-dot ${running && effectiveEnabled ? 'pulse' : ''}`} />
            <span>{readiness}</span>
          </div>
        </section>

        <section className="yae-card yae-quote-card" aria-hidden="true">
          <blockquote>
            「稻妻的樱花，
            <br />
            永远不会凋零。」
            <cite>— 八重神子</cite>
          </blockquote>
        </section>

        <section className="yae-card yae-detect-card" aria-label="游戏检测">
          <div className="yae-card-head">
            <h3><Users size={16} strokeWidth={1.8} />游戏检测</h3>
          </div>
          <div className="yae-game-row">
            <BrandMark className="yae-game-thumb" />
            <div className="yae-game-meta">
              <div className="yae-game-title">
                <strong>原神</strong>
                <span className="yae-region">{region}</span>
                <span className={`yae-run-pill ${running ? 'on' : ''}`}>{running ? '运行中' : launching ? '启动中' : '未运行'}</span>
              </div>
              <p>{running ? '已检测到游戏进程' : config.gamePath ? (statusText || '等待游戏进程') : '请先设置游戏路径'}</p>
            </div>
          </div>
          <dl className="yae-stat-list">
            <div><dt>进程 ID</dt><dd>{attachedPid > 0 ? attachedPid : '—'}</dd></div>
            <div><dt>当前帧率</dt><dd>{currentFps > 0 ? `${currentFps} FPS` : '—'}</dd></div>
            <div><dt>目标帧率</dt><dd>{config.targetFps} FPS</dd></div>
          </dl>
          <button type="button" className="yae-path-link" onClick={onPath}>
            <Folder size={12} />
            <span title={config.gamePath ?? undefined}>{config.gamePath ?? '设置游戏路径…'}</span>
          </button>
        </section>
      </div>

      <div className="yae-bottom-grid">
        <section className="yae-card yae-settings-card">
          <div className="yae-card-head">
            <h3><Settings2 size={16} strokeWidth={1.8} />基本设置</h3>
          </div>
          <div className="yae-toggle-list">
            <div className="yae-toggle-row">
              <div>
                <strong>启动时自动检测游戏</strong>
                <p>启动软件后自动检测原神进程</p>
              </div>
              <Toggle label="自动检测" checked={config.autoWatch} onChange={(v) => updateConfig('autoWatch', v)} />
            </div>
            <div className="yae-toggle-row">
              <div>
                <strong>启动软件时最小化到托盘</strong>
                <p>启动后隐藏主窗口，仅在托盘显示</p>
              </div>
              <Toggle label="最小化到托盘" checked={config.startMinimized} onChange={(v) => updateConfig('startMinimized', v)} />
            </div>
            <div className="yae-toggle-row">
              <div>
                <strong>帧率解锁</strong>
                <p>向游戏进程应用目标帧率</p>
              </div>
              <Toggle label="帧率解锁" checked={config.enabled} onChange={(v) => updateConfig('enabled', v)} />
            </div>
            <div className="yae-toggle-row">
              <div>
                <strong>开机自启</strong>
                <p>系统启动时自动运行本软件</p>
              </div>
              <Toggle label="开机自启" checked={config.autoStartWithWindows} onChange={(v) => updateConfig('autoStartWithWindows', v)} />
            </div>
          </div>
        </section>

        <section className="yae-card yae-settings-card">
          <div className="yae-card-head">
            <h3><SlidersHorizontal size={16} strokeWidth={1.8} />高级设置</h3>
          </div>
          <div className="yae-adv-list">
            <div className="yae-adv-row">
              <span className="yae-adv-label"><ScanLine size={14} />解锁服务</span>
              <button
                type="button"
                className={`yae-chip ${config.masterEnabled ? 'on' : ''}`}
                onClick={() => updateConfig('masterEnabled', !config.masterEnabled)}
              >
                {config.masterEnabled ? '已开启' : '已关闭'}
              </button>
            </div>
            <div className="yae-adv-row">
              <span className="yae-adv-label"><Folder size={14} />游戏路径</span>
              <button type="button" className="yae-chip path" onClick={onPath} title={config.gamePath ?? undefined}>
                {config.gamePath ? '已设置 · 更改' : '未设置'}
              </button>
            </div>
            <div className="yae-adv-row">
              <span className="yae-adv-label"><Monitor size={14} />托盘图标</span>
              <span className="yae-chip static">八重神子</span>
            </div>
            <div className="yae-adv-row">
              <span className="yae-adv-label"><Info size={14} />更多选项</span>
              <button type="button" className="yae-chip" onClick={onNavigateSettings}>全部设置</button>
            </div>
          </div>
        </section>

        <section className="yae-card yae-launch-opts">
          <div className="yae-card-head">
            <h3><Rocket size={16} strokeWidth={1.8} />启动选项</h3>
          </div>
          <div className="yae-check-list">
            <label className="yae-check">
              <input type="checkbox" checked={config.autoWatch} onChange={(e) => updateConfig('autoWatch', e.target.checked)} />
              <span>启动时自动监视</span>
            </label>
            <label className="yae-check">
              <input type="checkbox" checked={config.startMinimized} onChange={(e) => updateConfig('startMinimized', e.target.checked)} />
              <span>最小化启动</span>
            </label>
            <label className="yae-check">
              <input type="checkbox" checked={config.enabled && config.masterEnabled} onChange={(e) => {
                updateConfig('enabled', e.target.checked);
                if (e.target.checked) updateConfig('masterEnabled', true);
              }} />
              <span>启动后立即开始解锁</span>
            </label>
          </div>
          <div className="yae-launch-art">
            <img src="/images/teyvat-landscape.jpg" alt="" draggable={false} />
            <div className="yae-launch-caption">
              <strong>鸣神大社</strong>
              <span>在樱花的尽头<br />等待与你相遇</span>
            </div>
          </div>
        </section>
      </div>

      <div className="yae-tip">
        <Power size={14} />
        <p>
          <span>冒险小贴士</span>
          请先关闭游戏内垂直同步（V-Sync）。第三方工具存在使用风险，使用前请阅读
          <button type="button" onClick={onSafety}>用户协议</button>
        </p>
      </div>
    </div>
  );
}

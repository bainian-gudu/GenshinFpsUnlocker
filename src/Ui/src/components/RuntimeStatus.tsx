/** 运行状态卡：帧率 / 进程 / 解锁模块的实际反馈，以及三项画面效果注入的就绪状态。 */
import { Activity, ChevronRight } from 'lucide-react';
import type { AppState } from '../hooks/useAppState';
import { GAME_META } from '../lib/config';

type Tone = 'on' | 'wait' | 'off';

/** 就绪等级：0 未就绪 / 1 已就绪 / 2 已生效。 */
type ReadyLevel = 0 | 1 | 2;

/**
 * 单个注入功能的显示状态。就绪位来自 Stub 上报的位掩码：没附着时只能说「待注入」，
 * 附着后对应位没亮说明特征码还没适配，不能笼统写成「已开启」。
 */
function featureState(enabled: boolean, featuresActive: boolean, attached: boolean, level: ReadyLevel): { label: string; tone: Tone } {
  if (!enabled) return { label: '未开启', tone: 'off' };
  if (!featuresActive) return { label: '已暂停', tone: 'off' };
  if (!attached) return { label: '待注入', tone: 'wait' };
  if (level === 2) return { label: '已生效', tone: 'on' };
  if (level === 1) return { label: '已就绪', tone: 'wait' };
  return { label: '待适配', tone: 'wait' };
}

/** Stub 生命周期状态的可读文案（错误码定义见 src/Common/IpcData.h 与 dllmain.cpp）。 */
function stubText(status: number, lastError: number): string {
  switch (status) {
    case 1: return '正在解析特征…';
    case 2: return '已就绪';
    case 3: return lastError === 0xE001 ? '特征码未命中，等待版本适配' : '注入失败';
    case 4: return '正在退出';
    default: return '未注入';
  }
}

export function RuntimeStatus({ app }: { app: AppState }) {
  const { config, gameConfig, activeGame, currentFps, attachedPid, stubStatus, stubLastError, antiBlurState, hideUidState, navigate } = app;
  const injection = GAME_META[activeGame].injection;

  const attached = attachedPid > 0;
  const featuresActive = config.masterEnabled && config.autoWatch;

  const rows = [
    {
      label: '当前帧率',
      title: '当前帧率 → 目标帧率',
      value: !attached
        ? '等待游戏启动'
        : currentFps > 0 ? `${currentFps} → ${gameConfig.targetFps} FPS` : `目标 ${gameConfig.targetFps} FPS`,
    },
    { label: '游戏进程', title: undefined, value: attached ? `已附加 · PID ${attachedPid}` : '未检测到游戏' },
    { label: '解锁模块', title: injection.module, value: stubText(stubStatus, stubLastError) },
  ];

  // 位定义见 src/Common/IpcData.h：反虚化 bit0 反角色虚化 / bit1 马赛克就绪 / bit2 马赛克已生效，
  // UID 隐藏 bit0 就绪 / bit1 生效中。
  // 列表来自当前游戏自己的注入模块：原神与星穹铁道的效果名称、条目数都各自独立。
  const features = injection.features.map(({ key, title }) => {
    const level: ReadyLevel = key === 'antiBlurPerspective' ? ((antiBlurState & 1) !== 0 ? 2 : 0)
      : key === 'antiBlurDiveMosaic' ? ((antiBlurState & 4) !== 0 ? 2 : (antiBlurState & 2) !== 0 ? 1 : 0)
        : ((hideUidState & 2) !== 0 ? 2 : (hideUidState & 1) !== 0 ? 1 : 0);
    return { name: title, state: featureState(gameConfig[key], featuresActive, attached, level) };
  });

  return (
    <section className="control-panel runtime-panel" aria-label="运行状态">
      <div className="panel-heading">
        <h2><Activity size={17} strokeWidth={1.7} />运行状态</h2>
        <button type="button" className="text-button muted all-settings" onClick={() => navigate('settings')}>画面效果设置<ChevronRight size={13} /></button>
      </div>
      <dl className="runtime-rows">
        {rows.map((row) => (
          <div className="runtime-row" key={row.label}>
            <dt>{row.label}</dt>
            <dd title={row.title}>{row.value}</dd>
          </div>
        ))}
      </dl>
      <div className="runtime-divider" />
      <div className="runtime-subheading"><span>画面效果注入</span><span className="module-chip" title="该游戏独立的注入模块">{injection.module}</span></div>
      <ul className="runtime-features">
        {features.map(({ name, state }) => (
          <li key={name}>
            <span className="runtime-feature-name">{name}</span>
            <span className={`runtime-chip is-${state.tone}`}>{state.label}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}

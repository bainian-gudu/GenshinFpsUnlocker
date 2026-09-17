/** 帧率解锁控制面板：总开关联动、数值输入、滑块与快捷预设档位。 */
import { Gauge, Monitor, Sparkles } from 'lucide-react';
import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import { Toggle } from './ui';

/** 预设帧率档位；与宿主托盘菜单预设（MainForm.Tray.cs 的 TrayFpsPresets）保持一致。 */
const PRESETS = [60, 90, 120, 144, 165, 240];

/** 帧率控制主组件；总开关或解锁开关关闭时（`inactive`）整组禁用并在底部说明原因。 */
export function FpsControl({ value, enabled, masterEnabled, onChange, onToggle }: {
  value: number;
  enabled: boolean;
  masterEnabled: boolean;
  onChange: (value: number) => void;
  onToggle: (value: boolean) => void;
}) {
  const [draft, setDraft] = useState(String(value));
  const [error, setError] = useState('');
  useEffect(() => { setDraft(String(value)); setError(''); }, [value]);
  const inactive = !enabled || !masterEnabled;

  // 输入框为草稿态：失焦或回车才做整数/范围校验并提交，非法值提示并保留上次生效值
  const selectValue = (next: number) => {
    setDraft(String(next));
    setError('');
    onChange(next);
  };
  const commit = () => {
    const number = Number(draft);
    if (!draft.trim() || !Number.isInteger(number) || number < 1 || number > 540) {
      setError('请输入 1 至 540 之间的整数');
      return;
    }
    setDraft(String(number));
    setError('');
    onChange(number);
  };

  return (
    <section className={`control-panel fps-panel ${inactive ? 'fps-inactive' : ''}`} aria-label="帧率解锁设置">
      <div className="panel-heading">
        <h2><Gauge size={18} strokeWidth={1.7} />帧率解锁</h2>
        <div className="panel-toggle"><span>{enabled ? '已开启' : '已关闭'}</span><Toggle label="启用帧率解锁" checked={enabled} onChange={onToggle} /></div>
      </div>
      <fieldset disabled={inactive} className="fps-fields">
        <div className="fps-value-row">
          <div>
            <label className="field-caption" htmlFor="target-fps">目标帧率</label>
            <div className="fps-number-wrap">
              <input id="target-fps" type="number" inputMode="numeric" min={1} max={540} step={1}
                value={draft} aria-invalid={!!error} aria-describedby="fps-feedback"
                onFocus={(event) => event.target.select()}
                onChange={(event) => setDraft(event.target.value)} onBlur={commit}
                onKeyDown={(event) => { if (event.key === 'Enter') event.currentTarget.blur(); }}
                style={{ width: `${Math.max(draft.length, 2) * 35 + 3}px` }} />
              <span className="fps-unit">FPS</span>
              {value === 120 && <span className="recommended"><Sparkles size={11} />推荐</span>}
            </div>
          </div>
          <div className="fps-monitor-hint"><Monitor size={17} strokeWidth={1.5} /><span>跟随你的屏幕<br />找到流畅的平衡点</span></div>
        </div>
        <div className="slider-wrap">
          <input type="range" min={1} max={540} step={1} value={value} aria-label="调整目标帧率" aria-valuetext={`${value} FPS`}
            className="fps-slider" style={{ '--progress': `${((value - 1) / 539) * 100}%` } as CSSProperties}
            onChange={(event) => selectValue(Number(event.target.value))} />
          <div className="slider-scale"><span>1 FPS</span><span>540 FPS</span></div>
        </div>
        <div className="fps-presets" aria-label="帧率快捷预设">
          {PRESETS.map((fps) => (
            <button key={fps} type="button" className={value === fps ? 'selected' : ''} aria-pressed={value === fps}
              aria-label={`设置为 ${fps} FPS`} onClick={() => selectValue(fps)}>{fps}<span> FPS</span></button>
          ))}
        </div>
      </fieldset>
      <p id="fps-feedback" className={`fps-feedback ${error ? 'field-error' : ''}`} role={error ? 'alert' : undefined}>
        {error || (!masterEnabled ? '总开关已关闭，请前往设置启用解锁服务。' : !enabled ? '帧率解锁已暂停，游戏恢复自身档位；你的目标帧率会被保留。' : value > 240 ? '高帧率会增加设备负载，请根据屏幕刷新率与性能选择。' : value < 30 ? '当前上限较低，可能影响游戏流畅度，建议从 60 FPS 开始。' : '拖动滑块或点击数值自定义，设置将自动保存。')}
      </p>
    </section>
  );
}
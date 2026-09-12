import { Check, Info, LoaderCircle, PanelBottomClose, ShieldAlert, ShieldCheck } from 'lucide-react';
import type { AppState } from '../hooks/useAppState';
import { nativeInvoke } from '../lib/native';

/** 底部状态栏：附加状态、目标帧率、权限、保存状态与驻留托盘。 */
export function StatusBar({ app }: { app: AppState }) {
  const { native, config, saveState, attachedPid, isElevated } = app;

  return (
    <footer className="status-bar">
      <div className={`status-bar-left ${!config.autoWatch || !config.masterEnabled ? 'monitor-paused' : ''}`}>
        <span className={`status-dot ${attachedPid > 0 ? 'green pulse' : ''}`} />
        <span>{attachedPid > 0 ? '游戏进程已附加' : config.autoWatch && config.masterEnabled ? '自动监视中' : '后台监视已暂停'}</span>
        <span className="status-bar-separator" /><span className="status-target">目标 {config.targetFps} FPS</span>
        {native && (
          <>
            <span className="status-bar-separator" />
            <span className={`status-admin ${isElevated ? 'is-on' : 'is-off'}`} title={isElevated ? '已以管理员运行' : '标准用户 — 注入可能失败'}>
              {isElevated ? <ShieldCheck size={12} /> : <ShieldAlert size={12} />}
              {isElevated ? '管理员' : '标准权限'}
            </span>
          </>
        )}
      </div>
      <div className="status-bar-right">
        <span className={`save-status ${saveState === 'error' ? 'save-error' : ''}`} aria-live="polite">
          {saveState === 'saving' ? <LoaderCircle size={12} className="spin" /> : saveState === 'saved' ? <Check size={12} /> : <Info size={12} />}
          {saveState === 'saving' ? '正在保存...' : saveState === 'saved' ? '所有更改已保存' : '保存失败'}
        </span>
        {native && <>
          <span className="status-bar-separator" />
          <button type="button" className="status-tray-btn" title="最小化到系统托盘（关闭窗口同样驻留后台）"
            onClick={() => { void nativeInvoke('minimizeToTray').catch(() => undefined); }}>
            <PanelBottomClose size={13} />驻留托盘
          </button>
        </>}
      </div>
    </footer>
  );
}

import { AnimatePresence } from 'framer-motion';
import type { AppState } from '../hooks/useAppState';
import { ConfirmDialog, LaunchDialog, PathDialog, SafetyDialog } from '../components/Dialogs';
import { DEFAULT_CONFIG } from '../lib/config';
import type { NativeState } from '../lib/native';
import { nativeInvoke } from '../lib/native';

/** 全局弹窗集合：路径 / 用户协议 / 启动确认 / 重置 / 卸载 / 清空日志。 */
export function AppDialogs({ app }: { app: AppState }) {
  const {
    native, config, setConfig, modal, setModal, logs, setLogs, notify, applyNativeState, beginLaunch,
    startUninstall, savePath, browsePath, autoLocatePath,
  } = app;

  return (
    <AnimatePresence>
      {modal === 'path' && <PathDialog key="path" path={config.gamePath} isNative={native} onClose={() => setModal(null)} onSave={savePath} onBrowse={native ? browsePath : undefined} onAutoLocate={native ? autoLocatePath : undefined} />}
      {modal === 'safety' && <SafetyDialog key="safety" isNative={native} onClose={() => setModal(null)} onAcknowledge={async (showOnStartup) => {
        if (native) {
          const state = await nativeInvoke<any>('acknowledgeSafety', { showOnStartup });
          applyNativeState(state as NativeState);
        } else {
          setConfig((c) => ({ ...c, safetyNoticeAcknowledged: true, showSafetyNoticeOnStartup: showOnStartup }));
        }
        setModal(null);
      }} />}
      {modal === 'launch' && <LaunchDialog key="launch" config={config} isNative={native} onClose={() => setModal(null)} onStart={async (dontAskAgain) => {
        if (native) {
          // 与 SafetyDialog 分支对称：回写宿主返回的最新配置，
          // 否则同一会话再次启动会重复弹出本确认框。
          const state = await nativeInvoke<any>('acknowledgeSafety', { showOnStartup: !dontAskAgain });
          applyNativeState(state as NativeState);
        } else {
          setConfig((previous) => ({ ...previous, safetyNoticeAcknowledged: true, showSafetyNoticeOnStartup: !dontAskAgain }));
        }
        await beginLaunch();
      }} />}
      {modal === 'reset' && <ConfirmDialog key="reset" title="恢复默认设置？" description="这将覆盖当前解锁器配置为默认值。外观主题与日志不会受影响。建议先导出一份配置备份。" action="恢复默认" onClose={() => setModal(null)} onConfirm={async () => {
        if (native) {
          const state = await nativeInvoke<any>('resetConfig');
          applyNativeState(state as NativeState);
        } else setConfig({ ...DEFAULT_CONFIG });
        setModal(null);
        notify('已恢复默认设置');
      }} />}
      {modal === 'uninstall' && <ConfirmDialog key="uninstall" title="卸载本软件？" description="将启动安装器（Kachina）的卸载向导：清理程序文件、桌面与开始菜单快捷方式、开机自启动（注册表项与管理员计划任务），以及「安装的应用」中的卸载登记。可在向导中选择是否同时删除配置与日志。此操作不可自动撤销。" action="开始卸载" onClose={() => setModal(null)} onConfirm={async () => { setModal(null); await startUninstall(); }} />}
      {modal === 'clearLogs' && <ConfirmDialog key="clear-logs" title="清空日志列表？" description={`当前列表中的 ${logs.length} 条记录将从界面清除（桌面版不会删除磁盘日志文件）。`} action="清空列表" onClose={() => setModal(null)} onConfirm={() => { setLogs([]); setModal(null); notify('日志列表已清空'); }} />}
    </AnimatePresence>
  );
}

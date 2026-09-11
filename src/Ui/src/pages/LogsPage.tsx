import { Download, FileText, Search, SearchX, Trash2, X } from 'lucide-react';
import { useMemo, useState } from 'react';
import { formatTime } from '../lib/config';
import type { LogEntry } from '../lib/config';
import { PageHeading } from '../components/ui';

export function LogsPage({ logs, onClear, onExport, onOpenFolder, isNative }: { logs: LogEntry[]; onClear: () => void; onExport: () => void; onOpenFolder?: () => void; isNative?: boolean }) {
  const [search, setSearch] = useState('');
  const [level, setLevel] = useState('all');
  const filtered = useMemo(() => logs.filter((entry) => (level === 'all' || entry.level === level) && `${entry.message} ${formatTime(entry.timestamp)} ${entry.level}`.toLowerCase().includes(search.toLowerCase())), [logs, search, level]);
  return <>
    <PageHeading title="运行日志" description="每一步都有迹可循，让问题排查更简单。"><div className="page-heading-actions">{onOpenFolder && <button className="button button-secondary" onClick={onOpenFolder} type="button">打开日志目录</button>}<button className="button button-secondary" onClick={onExport} disabled={!logs.length}><Download size={15} />导出日志</button></div></PageHeading>
    <section className="log-workspace">
      <div className="log-toolbar"><div className="search-input"><Search size={17} /><input aria-label="搜索日志" placeholder="搜索日志内容或时间..." value={search} onChange={(event) => setSearch(event.target.value)} />{search && <button className="icon-button" onClick={() => setSearch('')} aria-label="清空搜索"><X size={14} /></button>}</div><select className="select-input" aria-label="筛选日志级别" value={level} onChange={(event) => setLevel(event.target.value)}><option value="all">全部级别</option><option value="Info">Info · 信息</option><option value="Warn">Warn · 警告</option><option value="Error">Error · 错误</option><option value="Debug">Debug · 调试</option><option value="Trace">Trace · 跟踪</option></select><button className="button button-quiet clear-logs" onClick={onClear} disabled={!logs.length}><Trash2 size={15} /><span>清空</span></button></div>
      <div className="log-session-heading"><span><span className="status-dot green" />{isNative ? '桌面运行日志' : '当前网页会话'}</span><span>{filtered.length} 条记录</span></div>
      <div className="log-table-container"><table className="log-table"><thead><tr><th>时间</th><th>级别</th><th>事件详情</th></tr></thead><tbody>{filtered.map((entry) => <tr key={entry.id}><td><time dateTime={entry.timestamp}>{formatTime(entry.timestamp)}</time></td><td><span className={`log-level level-${entry.level.toLowerCase()}`}>{entry.level.toUpperCase()}</span></td><td>{entry.message}</td></tr>)}</tbody></table>
        {!filtered.length && <div className="empty-state">{logs.length ? <SearchX size={34} strokeWidth={1.3} /> : <FileText size={34} strokeWidth={1.3} />}<h3>{logs.length ? '没有匹配的日志' : '一页清爽的新开始'}</h3><p>{logs.length ? '试试其他关键词，或切换日志级别。' : isNative ? '运行后的诊断信息将显示在这里。' : '接下来的设置更改将记录在这里。'}</p>{logs.length > 0 && <button className="text-button" onClick={() => { setSearch(''); setLevel('all'); }}>重置筛选</button>}</div>}
      </div>
      <div className="log-footer"><span>{isNative ? '展示宿主进程最近日志；完整文件见日志目录。' : '仅记录本次网页会话的交互。'}</span><span>最多保留 200 条</span></div>
    </section>
  </>;
}
import { Download, FileText, Search, SearchX, Trash2, X } from 'lucide-react';
import { memo, useMemo, useState } from 'react';
import { GAME_IDS, GAME_META, formatTime } from '../lib/config';
import type { GameId, LogEntry } from '../lib/config';
import { GameMark } from '../components/GameIcon';
import { PageHeading } from '../components/ui';

/** 日志来源筛选值：全部 / 某个游戏 / 解锁器自身（没有归属游戏的日志）。 */
type SourceFilter = 'all' | GameId;

/** 来源标签：游戏日志显示游戏图标与短名，解锁器自身的日志显示「解锁器」。 */
function SourceTag({ game }: { game?: GameId }) {
  if (!game) return <span className="log-source is-app">解锁器</span>;
  return <span className="log-source"><GameMark game={game} size={13} />{GAME_META[game].short}</span>;
}

// 状态推送（实时 FPS 等）每秒会重渲染整棵 App 树；日志条目对象在追加式更新下
// 身份稳定，memo 后未变化的行直接跳过，200 行的表格不再每次推送都重建。
const LogRow = memo(function LogRow({ entry }: { entry: LogEntry }) {
  return <tr><td><time dateTime={entry.timestamp}>{formatTime(entry.timestamp)}</time></td><td><span className={`log-level level-${entry.level.toLowerCase()}`}>{entry.level.toUpperCase()}</span></td><td><SourceTag game={entry.game} /></td><td>{entry.message}</td></tr>;
});

export function LogsPage({ logs, onClear, onExport, onOpenFolder, isNative }: { logs: LogEntry[]; onClear: () => void; onExport: () => void; onOpenFolder?: () => void; isNative?: boolean }) {
  const [search, setSearch] = useState('');
  const [level, setLevel] = useState('all');
  const [source, setSource] = useState<SourceFilter>('all');
  // 日志按时间倒序展示，最新一条固定在表格顶部；过滤在倒序后执行以保持顺序。
  const filtered = useMemo(() => [...logs].reverse().filter((entry) => (level === 'all' || entry.level === level)
    && (source === 'all' || entry.game === source)
    && `${entry.message} ${formatTime(entry.timestamp)} ${entry.level} ${entry.game ? GAME_META[entry.game].name : ''}`.toLowerCase().includes(search.toLowerCase())), [logs, search, level, source]);
  return <>
    <PageHeading title="运行日志" description="每一步都有迹可循，让问题排查更简单。"><div className="page-heading-actions">{onOpenFolder && <button className="button button-secondary" onClick={onOpenFolder} type="button">打开日志目录</button>}<button className="button button-secondary" onClick={onExport} disabled={!logs.length}><Download size={15} />导出日志</button></div></PageHeading>
    <section className="log-workspace">
      <div className="log-toolbar">
        <div className="search-input">
          <Search size={17} />
          <input aria-label="搜索日志" placeholder="搜索日志内容或时间..." value={search} onChange={(event) => setSearch(event.target.value)} />
          {search && <button className="icon-button" onClick={() => setSearch('')} aria-label="清空搜索"><X size={14} /></button>}
        </div>
        <div className="log-source-filter" role="group" aria-label="按来源筛选日志">
          <button type="button" className={source === 'all' ? 'is-active' : ''} aria-pressed={source === 'all'} onClick={() => setSource('all')}>全部</button>
          {GAME_IDS.map((id) => (
            <button type="button" key={id} className={source === id ? 'is-active' : ''} aria-pressed={source === id} title={GAME_META[id].name} onClick={() => setSource(id)}>
              <GameMark game={id} size={14} /><span>{GAME_META[id].short}</span>
            </button>
          ))}
        </div>
        <select className="select-input" aria-label="筛选日志级别" value={level} onChange={(event) => setLevel(event.target.value)}><option value="all">全部级别</option><option value="Info">Info · 信息</option><option value="Warn">Warn · 警告</option><option value="Error">Error · 错误</option><option value="Debug">Debug · 调试</option><option value="Trace">Trace · 跟踪</option></select>
        <button className="button button-quiet clear-logs" onClick={onClear} disabled={!logs.length}><Trash2 size={15} /><span>清空</span></button>
      </div>
      <div className="log-session-heading"><span><span className="status-dot green" />{isNative ? '桌面运行日志' : '当前网页会话'}</span><span>{filtered.length} 条记录</span></div>
      <div className="log-table-container"><table className="log-table"><thead><tr><th>时间</th><th>级别</th><th>来源</th><th>事件详情</th></tr></thead><tbody>{filtered.map((entry) => <LogRow key={entry.id} entry={entry} />)}</tbody></table>
        {!filtered.length && <div className="empty-state">{logs.length ? <SearchX size={34} strokeWidth={1.3} /> : <FileText size={34} strokeWidth={1.3} />}<h3>{logs.length ? '没有匹配的日志' : '一页清爽的新开始'}</h3><p>{logs.length ? '试试其他关键词，或切换日志级别与来源。' : isNative ? '运行后的诊断信息将显示在这里。' : '接下来的设置更改将记录在这里。'}</p>{logs.length > 0 && <button className="text-button" onClick={() => { setSearch(''); setLevel('all'); setSource('all'); }}>重置筛选</button>}</div>}
      </div>
      <div className="log-footer"><span>{isNative ? '展示宿主进程最近日志；完整文件见日志目录。' : '仅记录本次网页会话的交互。'}</span><span>最多保留 200 条</span></div>
    </section>
  </>;
}

import { motion } from 'framer-motion';
import { ArrowUpRight, X } from 'lucide-react';
import type { AppState } from '../hooks/useAppState';
import { NAV_ITEMS, SECONDARY_NAV } from '../lib/nav';
import { Brand } from '../components/Brand';
import { PROJECT_URL } from '../lib/config';

/** 左侧主导航：工作空间 + 帮助区（移动端为抽屉）。 */
export function AppSidebar({ app }: { app: AppState }) {
  const { page, sidebarOpen, setSidebarOpen, logs, version, sidebarRef, navigate } = app;

  return (
    <aside ref={sidebarRef} id="app-navigation" className={`sidebar ${sidebarOpen ? 'sidebar-open' : ''}`} aria-label="主导航" role={sidebarOpen ? 'dialog' : undefined} aria-modal={sidebarOpen || undefined}>
      <div className="sidebar-brand-row"><button className="brand-button" aria-label="返回游戏概览" onClick={() => navigate('overview')}><Brand /></button><button className="icon-button sidebar-close" onClick={() => setSidebarOpen(false)} aria-label="关闭导航"><X size={19} /></button></div>
      <div className="nav-group-label">工作空间</div>
      <nav className="primary-nav" aria-label="工作空间">
        {NAV_ITEMS.map(({ page: itemPage, label, icon: Icon }, index) => (
          <a key={itemPage} href={`#${itemPage}`} aria-label={label} title={`${label} (Alt+${index + 1})`}
            aria-current={page === itemPage ? 'page' : undefined} className={`nav-item ${page === itemPage ? 'active' : ''}`}
            onClick={(event) => { event.preventDefault(); navigate(itemPage); }}>
            {page === itemPage && <motion.span layoutId="active-navigation" className="nav-active-background" transition={{ type: 'spring', stiffness: 400, damping: 36 }} />}
            <Icon size={19} strokeWidth={1.6} /><span>{label}</span>
            {itemPage === 'logs' && logs.length > 0 && <span className="nav-count">{logs.length > 99 ? '99+' : logs.length}</span>}
          </a>
        ))}
      </nav>
      <div className="sidebar-bottom"><div className="sidebar-constellation" aria-hidden="true"><svg viewBox="0 0 180 130" fill="none"><path d="m12 102 32-30 36 14 29-47 48-23" stroke="currentColor" strokeWidth=".7" /><circle cx="12" cy="102" r="2" fill="currentColor" /><circle cx="44" cy="72" r="3" fill="currentColor" /><circle cx="80" cy="86" r="2" fill="currentColor" /><circle cx="109" cy="39" r="2.5" fill="currentColor" /><path d="m157 9 2 5 5 2-5 2-2 5-2-5-5-2 5-2 2-5Z" fill="currentColor" /><circle cx="72" cy="30" r="1" fill="currentColor" /><circle cx="145" cy="76" r="1" fill="currentColor" /></svg></div>
        <nav className="secondary-nav" aria-label="帮助与项目">
          {SECONDARY_NAV.map(({ page: itemPage, label, icon: Icon }) => (
            <a href={`#${itemPage}`} key={itemPage} aria-label={label} title={label}
              aria-current={page === itemPage ? 'page' : undefined} className={`nav-item ${page === itemPage ? 'active' : ''}`}
              onClick={(event) => { event.preventDefault(); navigate(itemPage); }}>
              {page === itemPage && <motion.span layoutId="active-navigation" className="nav-active-background" />}
              <Icon size={18} strokeWidth={1.6} /><span>{label}</span>
            </a>
          ))}
        </nav>
        <div className="sidebar-version"><button onClick={() => navigate('about')}><span className="version-dot" />v{version}</button><a href={`${PROJECT_URL}/blob/main/LICENSE`} target="_blank" rel="noreferrer">MIT License<ArrowUpRight size={11} /></a></div>
      </div>
    </aside>
  );
}

import { ArrowUpRight, ChevronRight, Menu, Moon, PanelsTopLeft, ShieldCheck, Sun } from 'lucide-react';
import type { AppState } from '../hooks/useAppState';
import { PAGE_NAMES } from '../lib/nav';
import { GithubIcon } from '../components/Brand';
import { PROJECT_URL } from '../lib/config';

/** 顶栏：面包屑、桌面版/预览标识、深浅色切换与 GitHub 链接。 */
export function AppTopbar({ app }: { app: AppState }) {
  const { native, page, theme, setTheme, sidebarOpen, setSidebarOpen } = app;

  return (
    <header className="topbar">
      <div className="breadcrumb">
        <button className="icon-button mobile-menu-button" onClick={() => setSidebarOpen(true)} aria-label="打开导航" aria-controls="app-navigation" aria-expanded={sidebarOpen}><Menu size={20} /></button>
        <span className="breadcrumb-root"><PanelsTopLeft size={15} strokeWidth={1.5} /><span>工作台</span><ChevronRight size={12} /></span>
        <span>{PAGE_NAMES[page]}</span>
      </div>
      <div className="topbar-actions">
        {native ? <span className="preview-label is-native"><ShieldCheck size={13} />桌面版</span>
          : <span className="preview-label">浏览器预览</span>}
        <button className="icon-button theme-button" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} aria-label={theme === 'dark' ? '切换到浅色模式' : '切换到深色模式'} title={theme === 'dark' ? '切换到浅色模式' : '切换到深色模式'}>
          {theme === 'dark'
            ? <Moon size={18} strokeWidth={1.5} className="theme-icon theme-icon-moon" />
            : <Sun size={18} strokeWidth={1.5} className="theme-icon theme-icon-sun" />}
        </button>
        <span className="topbar-divider" />
        <a className="github-link" href={PROJECT_URL} target="_blank" rel="noreferrer" aria-label="在 GitHub 查看项目"><GithubIcon size={17} /><span>GitHub</span><ArrowUpRight size={12} /></a>
      </div>
    </header>
  );
}

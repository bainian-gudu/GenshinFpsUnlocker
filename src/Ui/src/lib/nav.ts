import { BookOpen, Info, LayoutGrid, SlidersHorizontal, SquareTerminal } from 'lucide-react';
import type { Page } from './config';

/** 导航与页面标题常量（App 外壳、侧栏、快捷键共用）。 */
export const PAGE_NAMES: Record<Page, string> = { overview: '游戏概览', settings: '游戏设置', logs: '运行日志', guide: '使用指南', about: '关于项目' };

/** 原神 / 崩坏：星穹铁道各自一份的页面：顶部游戏切换器只在这些页面出现。 */
export const GAME_NAV_ITEMS = [
  { page: 'overview', label: '游戏概览', icon: LayoutGrid },
  { page: 'settings', label: '游戏设置', icon: SlidersHorizontal },
  { page: 'guide', label: '使用指南', icon: BookOpen },
] as const;

/** 与具体游戏无关的页面：不显示游戏切换器。 */
export const SHARED_NAV_ITEMS = [
  { page: 'logs', label: '运行日志', icon: SquareTerminal },
  { page: 'about', label: '关于项目', icon: Info },
] as const;

/** 判断页面是否属于「每个游戏一份」，决定顶栏是否显示游戏切换器。 */
export function isGamePage(page: Page): boolean {
  return GAME_NAV_ITEMS.some((item) => item.page === page);
}

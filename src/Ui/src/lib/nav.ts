import { BookOpen, Info, LayoutGrid, SlidersHorizontal, SquareTerminal } from 'lucide-react';
import type { Page } from './config';

/** 导航与页面标题常量（App 外壳、侧栏、快捷键共用）。 */
export const PAGE_NAMES: Record<Page, string> = { overview: '游戏概览', settings: '游戏设置', logs: '运行日志', guide: '使用指南', about: '关于项目' };
export const NAV_ITEMS = [{ page: 'overview', label: '游戏概览', icon: LayoutGrid }, { page: 'settings', label: '游戏设置', icon: SlidersHorizontal }, { page: 'logs', label: '运行日志', icon: SquareTerminal }] as const;
export const SECONDARY_NAV = [{ page: 'guide', label: '使用指南', icon: BookOpen }, { page: 'about', label: '关于项目', icon: Info }] as const;

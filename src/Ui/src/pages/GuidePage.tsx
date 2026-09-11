import { ArrowRight, ArrowUpRight, ChevronDown, ShieldCheck } from 'lucide-react';
import { AnimatePresence, motion } from 'framer-motion';
import { useState } from 'react';
import type { Page } from '../lib/config';
import { PROJECT_URL } from '../lib/config';
import { PageHeading } from '../components/ui';

const FAQ = [
  { q: '为什么设置了 120 FPS，游戏还是只有 60 FPS？', a: '先确认游戏内「设置 → 图像 → 垂直同步」已关闭，再检查解锁服务总开关与帧率解锁开关是否同时开启。设置的是帧率上限，实际帧率仍取决于设备性能。网页预览不会改变真实游戏帧率。' },
  { q: '关闭窗口后，解锁器还会运行吗？', a: '在原生桌面版中，关闭窗口会将应用隐藏到系统托盘，后台监视继续运行。需要完全退出时，请从托盘菜单选择退出。网页预览在关闭标签页后不会继续运行，但保存的偏好会保留。' },
  { q: '支持国服与国际服吗？', a: '支持。国服请选择 YuanShen.exe，国际服请选择 GenshinImpact.exe。请使用游戏本体的完整路径，不要选择启动器或下载器。' },
  { q: '如何将这里的设置用到桌面版？', a: '前往「游戏设置 → 高级设置」导出 config.json。退出桌面版，备份 %LocalAppData%\\GenshinFpsUnlocker\\config.json 后，再使用导出文件替换。请先核实游戏路径，并保留与你的安装环境有关的配置。建议先导入原有配置，再调整和导出。' },
  { q: '使用这个工具会有账号风险吗？', a: '有风险。本工具通过第三方模块注入调整帧率，并非官方功能，可能违反游戏服务条款。项目无法保证账号安全；是否使用由你自行决定。建议先完整阅读安全声明。' },
];

export function GuidePage({ navigate, onSafety }: { navigate: (page: Page) => void; onSafety: () => void }) {
  const [expanded, setExpanded] = useState<number | null>(0);
  const steps = [
    { title: '定位你的游戏', text: '选择 YuanShen.exe 或 GenshinImpact.exe，确认完整安装路径。', action: '设置游戏路径', page: 'settings' as Page },
    { title: '关闭垂直同步', text: '进入游戏「设置 → 图像」，关闭垂直同步（V-Sync）。' },
    { title: '选择适合的帧率', text: '推荐从 120 FPS 开始，根据显示器刷新率与设备性能进行调整。', action: '调整目标帧率', page: 'overview' as Page },
    { title: '让每一帧，自在流动', text: '开启自动解锁，桌面版会在检测到游戏启动后应用设置。网页中可以体验启动演示。' },
  ];
  return <>
    <PageHeading title="使用指南" description="简单几步，把流畅还给你的冒险。"><a className="text-button muted" href={PROJECT_URL} target="_blank" rel="noreferrer">完整项目文档<ArrowUpRight size={15} /></a></PageHeading>
    <div className="guide-layout"><section className="getting-started"><div className="section-kicker">QUICK START</div><h2>下一段旅程，更顺畅一点。</h2><div className="guide-steps">{steps.map((step, index) => <div className="guide-step" key={step.title}><span className="step-number">0{index + 1}</span><div><h3>{step.title}</h3><p>{step.text}</p>{step.action && <button className="text-button" onClick={() => navigate(step.page!)}>{step.action}<ArrowRight size={14} /></button>}</div></div>)}</div></section>
      <div className="guide-art"><img src="/images/teyvat-landscape.jpg" alt="碧水群山之间的璃月风格亭台" /><div><span>BEYOND THE FRAME</span><p>不止是更高的帧率，<br />更是沉浸的每一刻。</p></div></div>
    </div>
    <section className="faq-section"><h2>你可能想知道</h2><div className="faq-list">{FAQ.map((faq, index) => <div className={`faq-item ${expanded === index ? 'is-open' : ''}`} key={faq.q}><button aria-expanded={expanded === index} aria-controls={`faq-${index}`} onClick={() => setExpanded(expanded === index ? null : index)}>{faq.q}<ChevronDown size={17} /></button><AnimatePresence initial={false}>{expanded === index && <motion.div id={`faq-${index}`} initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }} transition={{ duration: 0.2 }}><p>{faq.a}</p></motion.div>}</AnimatePresence></div>)}</div></section>
    <div className="guide-safety"><ShieldCheck size={20} /><div><strong>保持知情，安心选择</strong><p>第三方工具存在使用风险，在桌面版中使用前请仔细阅读安全声明。</p></div><button className="text-button" onClick={onSafety}>阅读安全声明<ArrowRight size={15} /></button></div>
  </>;
}
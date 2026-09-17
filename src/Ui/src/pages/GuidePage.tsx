/** 使用指南页：入门步骤、FAQ 手风琴与底部安全声明入口。 */
import { ArrowRight, ArrowUpRight, ChevronDown, ShieldCheck } from 'lucide-react';
import { AnimatePresence, motion } from 'framer-motion';
import { useState } from 'react';
import type { Page } from '../lib/config';
import { PROJECT_URL } from '../lib/config';
import { PageHeading } from '../components/ui';

/** FAQ 文案：桌面版（WebView2 宿主）——含提权、托盘驻留等宿主能力说明。 */
const FAQ_NATIVE = [
  { q: '为什么设置了 120 FPS，游戏还是只有 60 FPS？', a: '先确认游戏内「设置 → 图像 → 垂直同步」已关闭，再检查「解锁服务总开关」与「帧率解锁」是否同时开启。设置的是帧率上限，实际帧率仍取决于设备性能与显示器刷新率。' },
  { q: '提示需要管理员权限 / 注入失败怎么办？', a: '向游戏进程注入模块时，通常需要管理员权限（OpenProcess）。在概览页或「游戏设置 → 启动与行为」点击「以管理员重新启动」，在 UAC 中选「是」即可。想让登录后就直接以管理员权限运行，请同时开启「开机自启动」与「启动时自动以管理员权限运行」：登录自启会登记成最高权限计划任务，登录时不会弹 UAC；只开前者时登录自启以标准权限运行，需要解锁时再手动提权一次。' },
  { q: '关闭窗口后，解锁器还会运行吗？', a: '会。关闭或最小化窗口会将应用隐藏到系统托盘，后台监视继续运行。需要完全退出时，请从托盘菜单选择「退出」。也可在状态栏点击「驻留托盘」。' },
  { q: '支持国服与国际服吗？', a: '支持。国服请选择 YuanShen.exe，国际服请选择 GenshinImpact.exe。请使用游戏本体的完整路径，不要选择启动器或下载器。可用「自动查找」或「浏览本地文件」。' },
  { q: '配置保存在哪里？', a: '配置文件：%LocalAppData%\\GenshinFpsUnlocker\\config.json。可在「游戏设置 → 高级设置」导入/导出。日志在同一目录下的 logs 文件夹。' },
  { q: '使用这个工具会有账号风险吗？', a: '有风险。本工具通过第三方模块注入调整帧率，并非官方功能，可能违反游戏服务条款。项目无法保证账号安全；是否使用由你自行决定。请先完整阅读用户协议与安全声明。' },
];

/** FAQ 文案：网页预览——不含宿主能力，措辞相应调整（如导出配置导入桌面版）。 */
const FAQ_WEB = [
  { q: '为什么设置了 120 FPS，游戏还是只有 60 FPS？', a: '先确认游戏内「设置 → 图像 → 垂直同步」已关闭，再检查解锁服务总开关与帧率解锁开关是否同时开启。网页预览不会改变真实游戏帧率。' },
  { q: '关闭窗口后，解锁器还会运行吗？', a: '在原生桌面版中，关闭窗口会将应用隐藏到系统托盘。网页预览在关闭标签页后不会继续运行，但保存的偏好会保留。' },
  { q: '支持国服与国际服吗？', a: '支持。国服请选择 YuanShen.exe，国际服请选择 GenshinImpact.exe。请使用游戏本体的完整路径，不要选择启动器或下载器。' },
  { q: '如何将这里的设置用到桌面版？', a: '前往「游戏设置 → 高级设置」导出 config.json。退出桌面版，备份 %LocalAppData%\\GenshinFpsUnlocker\\config.json 后，再使用导出文件替换。' },
  { q: '使用这个工具会有账号风险吗？', a: '有风险。本工具通过第三方模块注入调整帧率，并非官方功能，可能违反游戏服务条款。项目无法保证账号安全；是否使用由你自行决定。' },
];

/** 使用指南页组件：`isNative` 决定 FAQ 与末步文案版本；FAQ 一次只展开一条。 */
export function GuidePage({ navigate, onSafety, isNative }: { navigate: (page: Page) => void; onSafety: () => void; isNative?: boolean }) {
  const [expanded, setExpanded] = useState<number | null>(0);
  const faq = isNative ? FAQ_NATIVE : FAQ_WEB;
  // 入门四步；带 action 的步骤可点击跳转到对应功能页
  const steps = [
    { title: '定位你的游戏', text: '选择 YuanShen.exe 或 GenshinImpact.exe，确认完整安装路径。', action: '设置游戏路径', page: 'settings' as Page },
    { title: '关闭垂直同步', text: '进入游戏「设置 → 图像」，关闭垂直同步（V-Sync）。' },
    { title: '选择适合的帧率', text: '推荐从 120 FPS 开始，根据显示器刷新率与设备性能进行调整。', action: '调整目标帧率', page: 'overview' as Page },
    {
      title: '让每一帧，自在流动',
      text: isNative
        ? '开启自动解锁后，检测到游戏启动将自动应用帧率。关闭窗口会驻留托盘，后台继续监视。'
        : '开启自动解锁，桌面版会在检测到游戏启动后应用设置。网页中可以体验启动演示。',
    },
  ];
  return <>
    <PageHeading title="使用指南" description="简单几步，把流畅还给你的冒险。"><a className="text-button muted" href={PROJECT_URL} target="_blank" rel="noreferrer">完整项目文档<ArrowUpRight size={15} /></a></PageHeading>
    <div className="guide-layout"><section className="getting-started"><div className="section-kicker">QUICK START</div><h2>下一段旅程，更顺畅一点。</h2><div className="guide-steps">{steps.map((step, index) => <div className="guide-step" key={step.title}><span className="step-number">0{index + 1}</span><div><h3>{step.title}</h3><p>{step.text}</p>{step.action && <button className="text-button" onClick={() => navigate(step.page!)}>{step.action}<ArrowRight size={14} /></button>}</div></div>)}</div></section>
      <div className="guide-art"><img src="/images/teyvat-landscape.jpg" alt="碧水群山之间的璃月风格亭台" /><div><span>BEYOND THE FRAME</span><p>不止是更高的帧率，<br />更是沉浸的每一刻。</p></div></div>
    </div>
    <section className="faq-section"><h2>你可能想知道</h2><div className="faq-list">{faq.map((item, index) => <div className={`faq-item ${expanded === index ? 'is-open' : ''}`} key={item.q}><button aria-expanded={expanded === index} aria-controls={`faq-${index}`} onClick={() => setExpanded(expanded === index ? null : index)}>{item.q}<ChevronDown size={17} /></button><AnimatePresence initial={false}>{expanded === index && <motion.div id={`faq-${index}`} initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }} transition={{ duration: 0.2 }}><p>{item.a}</p></motion.div>}</AnimatePresence></div>)}</div></section>
    <div className="guide-safety"><ShieldCheck size={20} /><div><strong>保持知情，安心选择</strong><p>第三方工具存在使用风险，使用前请仔细阅读用户协议与安全声明。</p></div><button className="text-button" onClick={onSafety}>阅读用户协议<ArrowRight size={15} /></button></div>
  </>;
}

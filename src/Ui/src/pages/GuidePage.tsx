/** 使用指南页：入门步骤、FAQ 手风琴与底部安全声明入口。内容按当前游戏生成。 */
import { ArrowRight, ArrowUpRight, ChevronDown, ShieldCheck } from 'lucide-react';
import { AnimatePresence, motion } from 'framer-motion';
import { useState } from 'react';
import type { GameId, GameMeta, Page } from '../lib/config';
import { GAME_META, PROJECT_URL, gamePathHint } from '../lib/config';
import { PageHeading } from '../components/ui';

/** FAQ 文案：桌面版（WebView2 宿主）——含提权、托盘驻留等宿主能力说明。 */
function faqNative(meta: GameMeta) {
  return [
    { q: '为什么设置了 120 FPS，游戏还是只有 60 FPS？', a: '先确认游戏内「设置 → 图像 → 垂直同步」已关闭，再检查「解锁服务总开关」与「帧率解锁」是否同时开启。设置的是帧率上限，实际帧率仍取决于设备性能与显示器刷新率。' },
    { q: '提示需要管理员权限 / 注入失败怎么办？', a: '向游戏进程注入模块时，通常需要管理员权限（OpenProcess）。在概览页或「游戏设置 → 启动与行为」点击「以管理员重新启动」，在 UAC 中选「是」即可。想让登录后就直接以管理员权限运行，请同时开启「开机自启动」与「启动时自动以管理员权限运行」：登录自启会登记成最高权限计划任务，登录时不会弹 UAC；只开前者时登录自启以标准权限运行，需要解锁时再手动提权一次。' },
    { q: '关闭窗口后，解锁器还会运行吗？', a: '会。关闭或最小化窗口会将应用隐藏到系统托盘，后台监视继续运行。需要完全退出时，请从托盘菜单选择「退出」。也可在状态栏点击「驻留托盘」。' },
    { q: `支持${meta.name}的哪些版本？`, a: `支持国服与国际服。请使用 ${gamePathHint(meta.id)} 的完整路径，不要选择启动器或下载器，可用「自动查找」或「浏览本地文件」。` },
    { q: '配置保存在哪里？', a: `配置文件：%LocalAppData%\\GenshinFpsUnlocker\\config.json。${meta.name}的目标帧率、画面效果与安装路径都记录在里面，可在「游戏设置 → 高级设置」导入/导出。日志在同一目录下的 logs 文件夹。` },
    { q: '使用这个工具会有账号风险吗？', a: '有风险。本工具通过第三方模块注入调整帧率，并非官方功能，可能违反游戏服务条款。项目无法保证账号安全；是否使用由你自行决定。请先完整阅读用户协议与安全声明。' },
  ];
}

/** FAQ 文案：网页预览——不含宿主能力，措辞相应调整（如导出配置导入桌面版）。 */
function faqWeb(meta: GameMeta) {
  return [
    { q: '为什么设置了 120 FPS，游戏还是只有 60 FPS？', a: '先确认游戏内「设置 → 图像 → 垂直同步」已关闭，再检查解锁服务总开关与帧率解锁开关是否同时开启。网页预览不会改变真实游戏帧率。' },
    { q: '关闭窗口后，解锁器还会运行吗？', a: '在原生桌面版中，关闭窗口会将应用隐藏到系统托盘。网页预览在关闭标签页后不会继续运行，但保存的偏好会保留。' },
    { q: `支持${meta.name}的哪些版本？`, a: `支持国服与国际服。请使用 ${gamePathHint(meta.id)} 的完整路径，不要选择启动器或下载器。` },
    { q: '如何将这里的设置用到桌面版？', a: '前往「游戏设置 → 高级设置」导出 config.json。退出桌面版，备份 %LocalAppData%\\GenshinFpsUnlocker\\config.json 后，再使用导出文件替换。' },
    { q: '使用这个工具会有账号风险吗？', a: '有风险。本工具通过第三方模块注入调整帧率，并非官方功能，可能违反游戏服务条款。项目无法保证账号安全；是否使用由你自行决定。' },
  ];
}

/** 星穹铁道专属条目：说明与原神的差异，避免用户在原神页面上找不到对应场景。 */
function faqStarRail(meta: GameMeta) {
  return [
    { q: '星穹铁道的帧率是怎么解锁的？', a: '不走内存注入，直接改写注册表里的画面设置，并且只支持 120 FPS。开启「帧率解锁」时会先检查注册表：已经是 120 FPS 就不覆盖，否则写入 120。关闭开关不会回写，改完需要重启游戏生效。' },
    { q: '崩坏：星穹铁道支持哪些画面效果？', a: '反角色虚化与隐藏 UID 都可以开启；「移除水下马赛克」是原神水下场景专属效果，星穹铁道没有该场景，因此该项在这里不可用。' },
    { q: `原神与${meta.name}的配置会互相影响吗？`, a: '不会。目标帧率、画面效果与安装路径分别保存，用顶栏的切换按钮切换后，看到的就是当前游戏自己的配置。' },
  ];
}

/** 使用指南页组件：`isNative` 决定 FAQ 与末步文案版本；FAQ 一次只展开一条。 */
export function GuidePage({ game, navigate, onSafety, isNative }: { game: GameId; navigate: (page: Page) => void; onSafety: () => void; isNative?: boolean }) {
  const [expanded, setExpanded] = useState<number | null>(0);
  const meta = GAME_META[game];
  const faq = [
    ...(isNative ? faqNative(meta) : faqWeb(meta)),
    ...(game === 'starRail' ? faqStarRail(meta) : []),
  ];
  // 入门四步；带 action 的步骤可点击跳转到对应功能页
  const steps = [
    { title: '定位你的游戏', text: `选择 ${gamePathHint(game)}，确认 ${meta.name}的完整安装路径。`, action: '设置游戏路径', page: 'settings' as Page },
    { title: '关闭垂直同步', text: '进入游戏「设置 → 图像」，关闭垂直同步（V-Sync）。' },
    meta.fpsLock
      ? { title: '写入 120 FPS', text: '星穹铁道走注册表解锁，固定 120 FPS、不注入游戏进程；开启后会自动检查注册表，必要时写入 120。', action: '查看帧率设置', page: 'overview' as Page }
      : { title: '选择适合的帧率', text: '推荐从 120 FPS 开始，根据显示器刷新率与设备性能进行调整。', action: '调整目标帧率', page: 'overview' as Page },
    {
      title: '让每一帧，自在流动',
      text: isNative
        ? '开启自动解锁后，检测到游戏启动将自动应用帧率。关闭窗口会驻留托盘，后台继续监视。'
        : '开启自动解锁，桌面版会在检测到游戏启动后应用设置。网页中可以体验启动演示。',
    },
  ];
  return <>
    <PageHeading title="使用指南" description={`简单几步，把流畅还给你的${game === 'genshin' ? '冒险' : '旅程'}。`}><a className="text-button muted" href={PROJECT_URL} target="_blank" rel="noreferrer">完整项目文档<ArrowUpRight size={15} /></a></PageHeading>
    <div className="guide-layout"><section className="getting-started"><div className="section-kicker">{meta.hero.guideKicker}</div><h2>{meta.hero.guideHeadline}</h2><div className="guide-steps">{steps.map((step, index) => <div className="guide-step" key={step.title}><span className="step-number">0{index + 1}</span><div><h3>{step.title}</h3><p>{step.text}</p>{step.action && <button className="text-button" onClick={() => navigate(step.page!)}>{step.action}<ArrowRight size={14} /></button>}</div></div>)}</div></section>
      <div className={`guide-art is-${game}`}>
        {/* 同概览页：星穹铁道配图先复用原神那张，素材到位后换 src。 */}
        <img src="/images/teyvat-landscape.webp" alt={game === 'genshin' ? '碧水群山之间的璃月风格亭台' : '配图占位（星穹铁道素材待替换）'} />
        <div><span>BEYOND THE FRAME</span><p>不止是更高的帧率，<br />更是沉浸的每一刻。</p></div>
      </div>
    </div>
    <section className="faq-section"><h2>你可能想知道</h2><div className="faq-list">{faq.map((item, index) => <div className={`faq-item ${expanded === index ? 'is-open' : ''}`} key={item.q}><button aria-expanded={expanded === index} aria-controls={`faq-${index}`} onClick={() => setExpanded(expanded === index ? null : index)}>{item.q}<ChevronDown size={17} /></button><AnimatePresence initial={false}>{expanded === index && <motion.div id={`faq-${index}`} initial={{ height: 0, opacity: 0 }} animate={{ height: 'auto', opacity: 1 }} exit={{ height: 0, opacity: 0 }} transition={{ duration: 0.2 }}><p>{item.a}</p></motion.div>}</AnimatePresence></div>)}</div></section>
    <div className="guide-safety"><ShieldCheck size={20} /><div><strong>保持知情，安心选择</strong><p>第三方工具存在使用风险，使用前请仔细阅读用户协议与安全声明。</p></div><button className="text-button" onClick={onSafety}>阅读用户协议<ArrowRight size={15} /></button></div>
  </>;
}

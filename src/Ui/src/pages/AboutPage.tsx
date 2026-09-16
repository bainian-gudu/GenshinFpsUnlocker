import { ArrowUpRight, Code2, Heart, ShieldCheck, Trash2 } from 'lucide-react';
import { BrandMark, GithubIcon as Github } from '../components/Brand';
import { PageHeading } from '../components/ui';
import { PROJECT_URL } from '../lib/config';

export function AboutPage({ onSafety, version = '1.0.0', isNative, onUninstall }: { onSafety: () => void; version?: string; isNative?: boolean; onUninstall?: () => void }) {
  return <>
    <PageHeading title="关于项目" description="源于热爱，保持开放。" />
    <section className="about-intro"><BrandMark className="about-brand-mark" /><div><span className="section-kicker">LESS LIMITS. MORE ADVENTURE.</span><h2>Genshin<br />FPS Unlocker<span className="about-version">v{version}</span></h2><p>一个轻量、开源的原神帧率解锁工具。<br />自定义目标帧率，让你的硬件潜力与冒险一起释放。</p><div className="about-actions"><a className="button button-primary" href={PROJECT_URL} target="_blank" rel="noreferrer"><Github size={17} />访问 GitHub<ArrowUpRight size={15} /></a><a className="button button-secondary" href={`${PROJECT_URL}/releases`} target="_blank" rel="noreferrer">查看发行版本<ArrowUpRight size={15} /></a></div></div></section>
    <div className="about-details"><section><h3><Code2 size={18} />为轻量而构建</h3><dl><div><dt>项目作者</dt><dd><a href="https://github.com/bainian-gudu" target="_blank" rel="noreferrer">bainian-gudu<ArrowUpRight size={12} /></a></dd></div><div><dt>支持平台</dt><dd>Windows 10 (1607+) / Windows 11 · x64</dd></div><div><dt>桌面运行时</dt><dd>.NET Desktop Runtime 9 · WebView2</dd></div><div><dt>项目性质</dt><dd>个人自用 · 代码与文档主要由 AI 生成 · UI 由 gpt-6-astra-max 设计</dd></div><div><dt>开源许可</dt><dd><a href={`${PROJECT_URL}/blob/main/LICENSE`} target="_blank" rel="noreferrer">MIT License<ArrowUpRight size={12} /></a></dd></div></dl></section><section><h3><Heart size={18} />开放，让体验更好</h3><p>项目代码公开透明。欢迎提交问题、分享建议，或用一次 Pull Request 让它变得更好。</p><div className="about-text-links"><a href={`${PROJECT_URL}/issues`} target="_blank" rel="noreferrer">反馈问题<ArrowUpRight size={14} /></a><a href={`${PROJECT_URL}/pulls`} target="_blank" rel="noreferrer">参与贡献<ArrowUpRight size={14} /></a></div></section></div>
    <div className="about-disclaimer"><ShieldCheck size={18} /><p>本项目为个人自用的第三方工具，代码与文档主要由 AI 生成；与米哈游 / HoYoverse 无关联，未获授权或背书。使用风险自负。<button className="text-button" onClick={onSafety}>查看用户协议</button></p></div>
    {isNative && onUninstall && (
      <div className="about-uninstall"><div><span className="row-title">卸载本软件</span><p>启动安装器（Kachina）的卸载向导，清理程序文件、快捷方式、开机自启动（注册表项与管理员计划任务）与卸载登记项。</p></div><button className="button button-danger" onClick={onUninstall}><Trash2 size={15} />卸载本软件</button></div>
    )}
  </>;
}

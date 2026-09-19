/** 品牌标识组件：星标 SVG、文字标与 GitHub 图标，供侧栏 / 关于页使用。 */
import { BRAND_NAME, BRAND_SUB } from '../lib/config';

/** 星标图形：双层四角星 + 右上角装饰小星；纯装饰，对无障碍树隐藏。 */
export function BrandMark({ className = '' }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 48 48" fill="none" aria-hidden="true">
      <path d="M24 3L29.6 18.4L45 24L29.6 29.6L24 45L18.4 29.6L3 24L18.4 18.4L24 3Z" fill="currentColor" />
      <path d="M24 14L27 21L34 24L27 27L24 34L21 27L14 24L21 21L24 14Z" fill="var(--sidebar-bg, #15161d)" />
      <path d="M37.5 4L39 8L43 9.5L39 11L37.5 15L36 11L32 9.5L36 8L37.5 4Z" fill="currentColor" opacity=".6" />
    </svg>
  );
}

/** 品牌区：星标 + 双语文字标；`large` 用于关于页等大尺寸展示位。 */
export function Brand({ large = false }: { large?: boolean }) {
  return (
    <div className={`brand ${large ? 'brand-large' : ''}`}>
      <BrandMark className="brand-mark" />
      <div className="brand-wordmark">
        <strong>{BRAND_NAME}</strong>
        <span>{BRAND_SUB}</span>
      </div>
    </div>
  );
}

/** GitHub 仓库图标（octocat 矢量路径），按 `size` 缩放。 */
export function GithubIcon({ size = 20, className = '' }: { size?: number; className?: string }) {
  return <svg width={size} height={size} className={className} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 .75a11.25 11.25 0 0 0-3.56 21.92c.56.1.77-.24.77-.54v-2.1c-3.13.68-3.79-1.33-3.79-1.33-.51-1.3-1.25-1.65-1.25-1.65-1.02-.7.08-.68.08-.68 1.13.08 1.73 1.16 1.73 1.16 1 1.72 2.63 1.22 3.27.93.1-.73.39-1.22.71-1.5-2.5-.29-5.12-1.25-5.12-5.56 0-1.23.44-2.24 1.16-3.03-.12-.28-.5-1.43.11-2.98 0 0 .95-.3 3.1 1.16a10.8 10.8 0 0 1 5.63 0c2.15-1.46 3.09-1.16 3.09-1.16.62 1.55.24 2.7.12 2.98.72.79 1.15 1.8 1.15 3.03 0 4.32-2.63 5.27-5.14 5.55.4.35.77 1.03.77 2.09v3.09c0 .3.2.65.78.54A11.25 11.25 0 0 0 12 .75Z" /></svg>;
}

/** 八重神子主题：狐面 + 樱花品牌标 */
export function BrandMark({ className = '' }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 48 48" fill="none" aria-hidden="true">
      <defs>
        <linearGradient id="yaeBrandGrad" x1="8" y1="4" x2="42" y2="44" gradientUnits="userSpaceOnUse">
          <stop stopColor="#ff9ad5" />
          <stop offset=".55" stopColor="#e56bff" />
          <stop offset="1" stopColor="#a855f7" />
        </linearGradient>
      </defs>
      <rect x="2" y="2" width="44" height="44" rx="12" fill="url(#yaeBrandGrad)" opacity=".22" />
      <path
        d="M24 8c2.2 1.2 8.5 6.2 8.5 15.2 0 5.4-3.4 9.2-8.5 9.2S15.5 28.6 15.5 23.2C15.5 14.2 21.8 9.2 24 8Z"
        fill="url(#yaeBrandGrad)"
      />
      <path d="M16.2 14.5 12 6.2 20.8 12.4" fill="#ffb6e0" />
      <path d="M31.8 14.5 36 6.2 27.2 12.4" fill="#ffb6e0" />
      <path d="M24 8c-1.2 3.5-2 7.8-2 12.2 0 4.2.7 7.2 2 8.6 1.3-1.4 2-4.4 2-8.6 0-4.4-.8-8.7-2-12.2Z" fill="#fff" opacity=".92" />
      <circle cx="20.6" cy="22.2" r="1.35" fill="#5b1d7a" />
      <circle cx="27.4" cy="22.2" r="1.35" fill="#5b1d7a" />
      <path d="M21.5 26.2c1.4 1.2 3.6 1.2 5 0" stroke="#e070b0" strokeWidth="1.2" strokeLinecap="round" />
      <path
        d="M33.2 30.5c.2-2.2 2.4-3.3 4.2-2.6 1.1 2.1-.1 4.4-2.3 4.9-1.5.3-2.1-1-1.9-2.3Z"
        fill="#ffc2e4"
      />
      <path
        d="M34.8 29.2c.55-.15 1.1.2 1.2.7-.35.55-.95.75-1.45.55-.35-.15-.4-.55.25-1.25Z"
        fill="#fff"
        opacity=".7"
      />
    </svg>
  );
}

export function Brand({ large = false }: { large?: boolean }) {
  return (
    <div className={`brand ${large ? 'brand-large' : ''}`}>
      <BrandMark className="brand-mark" />
      <div className="brand-wordmark">
        <strong>Genshin</strong>
        <span>FPS UNLOCKER</span>
        <em className="brand-tagline">解锁帧率 · 畅享提瓦特</em>
      </div>
    </div>
  );
}

export function GithubIcon({ size = 20, className = '' }: { size?: number; className?: string }) {
  return <svg width={size} height={size} className={className} viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 .75a11.25 11.25 0 0 0-3.56 21.92c.56.1.77-.24.77-.54v-2.1c-3.13.68-3.79-1.33-3.79-1.33-.51-1.3-1.25-1.65-1.25-1.65-1.02-.7.08-.68.08-.68 1.13.08 1.73 1.16 1.73 1.16 1 1.72 2.63 1.22 3.27.93.1-.73.39-1.22.71-1.5-2.5-.29-5.12-1.25-5.12-5.56 0-1.23.44-2.24 1.16-3.03-.12-.28-.5-1.43.11-2.98 0 0 .95-.3 3.1 1.16a10.8 10.8 0 0 1 5.63 0c2.15-1.46 3.09-1.16 3.09-1.16.62 1.55.24 2.7.12 2.98.72.79 1.15 1.8 1.15 3.03 0 4.32-2.63 5.27-5.14 5.55.4.35.77 1.03.77 2.09v3.09c0 .3.2.65.78.54A11.25 11.25 0 0 0 12 .75Z" /></svg>;
}
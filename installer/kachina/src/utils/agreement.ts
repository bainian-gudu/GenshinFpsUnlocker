import DOMPurify from 'dompurify';
import type { AgreementConfig } from '../types';

/**
 * 用户协议渲染：把配置里内联的协议正文（text / markdown / html）转成可安全
 * v-html 的字符串。协议正文来自打包时的本地文件，但仍旧统一走 DOMPurify，
 * 避免任何情况下把脚本注入安装器界面。
 */

const SANITIZE_OPTIONS = {
  USE_PROFILES: { html: true },
  ADD_ATTR: ['target', 'rel'],
};

/** HTML 转义 */
export function escapeHtml(input: string): string {
  return input
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/** 行内标记：代码、粗体、斜体、删除线、链接 */
function renderInline(text: string): string {
  let out = escapeHtml(text);
  out = out.replace(/`([^`]+)`/g, '<code>$1</code>');
  out = out.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  out = out.replace(/(^|[^*\w])\*([^*\n]+)\*/g, '$1<em>$2</em>');
  out = out.replace(/~~([^~]+)~~/g, '<del>$1</del>');
  // 只放行 http(s) 链接；escapeHtml 之后引号已变成 &quot;，这里按未转义形式匹配
  out = out.replace(
    /\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g,
    '<a href="$2" target="_blank" rel="noreferrer">$1</a>',
  );
  return out;
}

/**
 * 极简 Markdown 渲染（标题 / 段落 / 列表 / 引用 / 分隔线 / 代码块），
 * 够协议这类纯文档使用，不引入额外依赖。
 */
export function renderMarkdown(source: string): string {
  const lines = source.replace(/\r\n/g, '\n').split('\n');
  const out: string[] = [];
  let listType: 'ul' | 'ol' | null = null;
  let paragraph: string[] = [];
  let code: string[] | null = null;

  const flushParagraph = () => {
    if (!paragraph.length) return;
    out.push(`<p>${paragraph.map(renderInline).join('<br />')}</p>`);
    paragraph = [];
  };
  const flushList = () => {
    if (!listType) return;
    out.push(`</${listType}>`);
    listType = null;
  };

  for (const raw of lines) {
    const line = raw.replace(/\s+$/, '');

    if (code) {
      if (/^\s*```/.test(line)) {
        out.push(`<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`);
        code = null;
      } else {
        code.push(raw);
      }
      continue;
    }
    if (/^\s*```/.test(line)) {
      flushParagraph();
      flushList();
      code = [];
      continue;
    }
    if (!line.trim()) {
      flushParagraph();
      flushList();
      continue;
    }

    const heading = /^(#{1,6})\s+(.*)$/.exec(line);
    if (heading) {
      flushParagraph();
      flushList();
      // # 映射到 h2，避免与弹窗自身的标题层级冲突
      const level = Math.min(heading[1].length + 1, 6);
      out.push(`<h${level}>${renderInline(heading[2])}</h${level}>`);
      continue;
    }
    if (/^\s*(?:-{3,}|\*{3,}|_{3,})\s*$/.test(line)) {
      flushParagraph();
      flushList();
      out.push('<hr />');
      continue;
    }

    const item = /^\s*(?:[-*+]|\d+[.)])\s+(.*)$/.exec(line);
    if (item) {
      flushParagraph();
      const want: 'ul' | 'ol' = /^\s*\d+[.)]\s+/.test(line) ? 'ol' : 'ul';
      if (listType !== want) {
        flushList();
        out.push(`<${want}>`);
        listType = want;
      }
      out.push(`<li>${renderInline(item[1])}</li>`);
      continue;
    }

    const quote = /^\s*>\s?(.*)$/.exec(line);
    if (quote) {
      flushParagraph();
      flushList();
      out.push(`<blockquote>${renderInline(quote[1])}</blockquote>`);
      continue;
    }

    flushList();
    paragraph.push(line.trim());
  }

  if (code) {
    out.push(`<pre><code>${escapeHtml(code.join('\n'))}</code></pre>`);
  }
  flushParagraph();
  flushList();
  return out.join('\n');
}

/** 协议是否有可展示的内容 */
export function hasAgreementContent(
  agreement?: AgreementConfig | null,
): boolean {
  return Boolean(agreement?.content?.trim());
}

/** 渲染协议为经过净化的 HTML 片段 */
export function renderAgreement(agreement?: AgreementConfig | null): string {
  if (!hasAgreementContent(agreement)) return '';
  const content = agreement!.content;
  const format = (agreement!.format ?? 'text').trim().toLowerCase();

  let html: string;
  if (format === 'html' || format === 'htm') {
    html = content;
  } else if (format === 'markdown' || format === 'md') {
    html = renderMarkdown(content);
  } else {
    // 纯文本：保留原始换行与缩进
    html = `<div class="agreement-plain">${escapeHtml(content)}</div>`;
  }
  return DOMPurify.sanitize(html, SANITIZE_OPTIONS);
}

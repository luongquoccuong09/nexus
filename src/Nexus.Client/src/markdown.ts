/**
 * Strict XSS-safe Markdown subset renderer (M2.9 / M6.27 / SEC-10).
 * Escapes all HTML first, allows only http/https links (never javascript:/data:/vbscript:),
 * and supports code blocks, inline code, unordered/ordered lists, bold, italic,
 * strikethrough, auto-links, and Unicode emoji (including ZWJ, skin tones, and flags).
 */
export function escapeHtml(raw: string): string {
  return raw
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

export function isSafeHttpUrl(url: string): boolean {
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'http:' || parsed.protocol === 'https:';
  } catch {
    return false;
  }
}

const UNORDERED_LIST_ITEM_RE = /^[ \t]{0,3}[-*]\s+(.+)$/;
const ORDERED_LIST_ITEM_RE = /^[ \t]{0,3}\d+\.\s+(.+)$/;

export function renderSafeMarkdown(input: string): string {
  const tokens: string[] = [];
  const stash = (html: string) => {
    const idx = tokens.length;
    tokens.push(html);
    return `\u0000T${idx}\u0000`;
  };

  const formatInline = (line: string): string => {
    let out = line;

    // Inline code `...`
    out = out.replace(/`([^`\n]+)`/g, (_m, code: string) =>
      stash(`<code>${code}</code>`));

    // Markdown links [label](url) — strictly allow http/https only (supports 1 level of balanced parens)
    out = out.replace(/\[([^\]]+)\]\(((?:[^()\s]+|\([^()\s]*\))+)\)/g, (_m, label: string, rawUrl: string) => {
      const decodedUrl = rawUrl.replace(/&amp;/g, '&');
      if (!isSafeHttpUrl(decodedUrl)) {
        return label;
      }
      return stash(`<a href="${escapeHtml(decodedUrl)}" target="_blank" rel="noopener noreferrer">${label}</a>`);
    });

    // Auto-links (http:// or https://)
    out = out.replace(/\bhttps?:\/\/[^\s<>"')]+/g, (rawUrl: string) => {
      const decodedUrl = rawUrl.replace(/&amp;/g, '&');
      if (!isSafeHttpUrl(decodedUrl)) {
        return rawUrl;
      }
      return stash(`<a href="${escapeHtml(decodedUrl)}" target="_blank" rel="noopener noreferrer">${escapeHtml(decodedUrl)}</a>`);
    });

    // Inline emphasis formatting
    out = out.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
    out = out.replace(/~~([^~\n]+)~~/g, '<del>$1</del>');
    out = out.replace(/\*([^*\n]+)\*/g, '<em>$1</em>');
    return out;
  };

  let text = escapeHtml(input.replace(/\u0000/g, '').replace(/\r\n?/g, '\n'));

  // 1. Fenced code blocks ```...``` (stashed before list/inline parsing)
  text = text.replace(/```(?:\w+)?\n?([\s\S]*?)```/g, (_m, code: string) =>
    stash(`<pre class="nx-code-block"><code>${code.replace(/\n$/, '')}</code></pre>`));

  // 2. Group unordered (- / *) and ordered (1. ) list blocks while keeping regular lines intact
  const lines = text.split('\n');
  const segments: Array<{ kind: 'line' | 'list'; html: string }> = [];
  let idx = 0;

  while (idx < lines.length) {
    const line = lines[idx];
    const ulMatch = UNORDERED_LIST_ITEM_RE.exec(line);
    if (ulMatch) {
      const items: string[] = [];
      while (idx < lines.length) {
        const m = UNORDERED_LIST_ITEM_RE.exec(lines[idx]);
        if (!m) break;
        items.push(`<li>${formatInline(m[1])}</li>`);
        idx++;
      }
      segments.push({ kind: 'list', html: `<ul class="nx-md-list">${items.join('')}</ul>` });
      continue;
    }

    const olMatch = ORDERED_LIST_ITEM_RE.exec(line);
    if (olMatch) {
      const items: string[] = [];
      while (idx < lines.length) {
        const m = ORDERED_LIST_ITEM_RE.exec(lines[idx]);
        if (!m) break;
        items.push(`<li>${formatInline(m[1])}</li>`);
        idx++;
      }
      segments.push({ kind: 'list', html: `<ol class="nx-md-list">${items.join('')}</ol>` });
      continue;
    }

    segments.push({ kind: 'line', html: formatInline(line) });
    idx++;
  }

  let assembled = '';
  for (let i = 0; i < segments.length; i++) {
    const seg = segments[i];
    if (i > 0 && segments[i - 1].kind === 'line' && seg.kind === 'line') {
      assembled += '<br>';
    }
    assembled += seg.html;
  }

  // 3. Restore stashed HTML tokens
  return assembled.replace(/\u0000T(\d+)\u0000/g, (_m, tokenIdx: string) => tokens[Number(tokenIdx)] ?? '');
}

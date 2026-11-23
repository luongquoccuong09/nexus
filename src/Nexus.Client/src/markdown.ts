/**
 * Strict XSS-safe Markdown subset renderer (M2.9 / SEC-10).
 * Escapes all HTML first, allows only http/https links (never javascript:/data:/vbscript:),
 * and supports code blocks, inline code, bold, italic, strikethrough, auto-links, and emoji.
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

export function renderSafeMarkdown(input: string): string {
  const tokens: string[] = [];
  const stash = (html: string) => {
    const idx = tokens.length;
    tokens.push(html);
    return `\u0000T${idx}\u0000`;
  };

  let text = escapeHtml(input.replace(/\u0000/g, ''));

  // 1. Fenced code blocks ```...```
  text = text.replace(/```(?:\w+)?\n?([\s\S]*?)```/g, (_m, code: string) =>
    stash(`<pre class="nx-code-block"><code>${code.replace(/\n$/, '')}</code></pre>`));

  // 2. Inline code `...`
  text = text.replace(/`([^`\n]+)`/g, (_m, code: string) =>
    stash(`<code>${code}</code>`));

  // 3. Markdown links [label](url) — strictly allow http/https only (supports 1 level of balanced parens)
  text = text.replace(/\[([^\]]+)\]\(((?:[^()\s]+|\([^()\s]*\))+)\)/g, (_m, label: string, rawUrl: string) => {
    const decodedUrl = rawUrl.replace(/&amp;/g, '&');
    if (!isSafeHttpUrl(decodedUrl)) {
      return label;
    }
    return stash(`<a href="${escapeHtml(decodedUrl)}" target="_blank" rel="noopener noreferrer">${label}</a>`);
  });

  // 4. Auto-links (http:// or https://)
  text = text.replace(/\bhttps?:\/\/[^\s<>"')]+/g, (rawUrl: string) => {
    const decodedUrl = rawUrl.replace(/&amp;/g, '&');
    if (!isSafeHttpUrl(decodedUrl)) {
      return rawUrl;
    }
    return stash(`<a href="${escapeHtml(decodedUrl)}" target="_blank" rel="noopener noreferrer">${escapeHtml(decodedUrl)}</a>`);
  });

  // 5. Formatting
  text = text.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
  text = text.replace(/~~([^~\n]+)~~/g, '<del>$1</del>');
  text = text.replace(/\*([^*\n]+)\*/g, '<em>$1</em>');
  text = text.replace(/\n/g, '<br>');

  // 6. Restore stashed HTML tokens
  return text.replace(/\u0000T(\d+)\u0000/g, (_m, idx: string) => tokens[Number(idx)] ?? '');
}

/**
 * Pure text-transformation helpers for Nexus chat & community composers (M6.27).
 * Preserves selection/caret ranges, rejects unsafe URL schemes, and respects IME composition.
 */

export interface TextRange {
  value: string;
  selectionStart: number;
  selectionEnd: number;
}

export type FormatAction =
  | 'bold'
  | 'italic'
  | 'inlineCode'
  | 'codeBlock'
  | 'bulletList'
  | 'numberedList';

export type ComposerShortcutAction = 'bold' | 'italic' | 'inlineCode' | 'link';

export type InsertLinkResult =
  | { ok: true; next: TextRange }
  | { ok: false; error: string };

const UNORDERED_PREFIX_RE = /^[-*]\s+/;
const ORDERED_PREFIX_RE = /^\d+\.\s+/;

function clampRange(state: TextRange): { value: string; start: number; end: number } {
  const value = state.value;
  const len = value.length;
  const rawStart = Number.isFinite(state.selectionStart) ? state.selectionStart : len;
  const rawEnd = Number.isFinite(state.selectionEnd) ? state.selectionEnd : rawStart;
  const start = Math.max(0, Math.min(len, Math.min(rawStart, rawEnd)));
  const end = Math.max(start, Math.min(len, Math.max(rawStart, rawEnd)));
  return { value, start, end };
}

function countTrailingChar(text: string, ch: string): number {
  let count = 0;
  for (let i = text.length - 1; i >= 0 && text[i] === ch; i--) {
    count++;
  }
  return count;
}

function countLeadingChar(text: string, ch: string): number {
  let count = 0;
  for (let i = 0; i < text.length && text[i] === ch; i++) {
    count++;
  }
  return count;
}

function isWrappedOutside(before: string, after: string, marker: string): boolean {
  if (marker === '*') {
    const left = countTrailingChar(before, '*');
    const right = countLeadingChar(after, '*');
    return left % 2 === 1 && right % 2 === 1;
  }
  if (marker === '**') {
    return countTrailingChar(before, '*') >= 2 && countLeadingChar(after, '*') >= 2;
  }
  return before.endsWith(marker) && after.startsWith(marker);
}

function isWrappedInside(selected: string, marker: string): boolean {
  if (selected.length < marker.length * 2) return false;
  if (marker === '*') {
    if (!selected.startsWith('*') || !selected.endsWith('*')) return false;
    const left = countLeadingChar(selected, '*');
    const right = countTrailingChar(selected, '*');
    return left % 2 === 1 && right % 2 === 1;
  }
  return selected.startsWith(marker) && selected.endsWith(marker);
}

export function toggleInlineWrap(state: TextRange, marker: string, placeholder: string): TextRange {
  const { value, start, end } = clampRange(state);
  const before = value.slice(0, start);
  const selected = value.slice(start, end);
  const after = value.slice(end);
  const mLen = marker.length;

  // Toggle OFF when markers sit immediately outside the selection
  if (isWrappedOutside(before, after, marker)) {
    const nextValue = before.slice(0, before.length - mLen) + selected + after.slice(mLen);
    return {
      value: nextValue,
      selectionStart: start - mLen,
      selectionEnd: end - mLen
    };
  }

  // Toggle OFF when the selected text itself includes the matching markers
  if (selected.length > mLen * 2 && isWrappedInside(selected, marker)) {
    const inner = selected.slice(mLen, selected.length - mLen);
    const nextValue = before + inner + after;
    return {
      value: nextValue,
      selectionStart: start,
      selectionEnd: start + inner.length
    };
  }

  // Wrap selection (or insert placeholder and select it)
  const content = selected.length > 0 ? selected : placeholder;
  const nextValue = `${before}${marker}${content}${marker}${after}`;
  const innerStart = start + mLen;
  return {
    value: nextValue,
    selectionStart: innerStart,
    selectionEnd: innerStart + content.length
  };
}

export function applyBold(state: TextRange): TextRange {
  return toggleInlineWrap(state, '**', 'bold text');
}

export function applyItalic(state: TextRange): TextRange {
  return toggleInlineWrap(state, '*', 'italic text');
}

export function applyInlineCode(state: TextRange): TextRange {
  return toggleInlineWrap(state, '`', 'code');
}

export function applyCodeBlock(state: TextRange): TextRange {
  const { value, start, end } = clampRange(state);
  const before = value.slice(0, start);
  const selected = value.slice(start, end);
  const after = value.slice(end);

  // Toggle OFF if immediately surrounded by fenced markers
  if (before.endsWith('```\n') && after.startsWith('\n```')) {
    const nextValue = before.slice(0, before.length - 4) + selected + after.slice(4);
    return {
      value: nextValue,
      selectionStart: start - 4,
      selectionEnd: end - 4
    };
  }

  // Toggle OFF if selection itself is a fenced block
  const fencedMatch = /^```(?:\w+)?\n([\s\S]*?)\n```$/.exec(selected);
  if (fencedMatch) {
    const inner = fencedMatch[1];
    return {
      value: before + inner + after,
      selectionStart: start,
      selectionEnd: start + inner.length
    };
  }

  const leadingNl = start > 0 && before[before.length - 1] !== '\n' ? '\n' : '';
  const trailingNl = end < value.length && after[0] !== '\n' ? '\n' : '';
  const content = selected.length > 0 ? selected : 'code';
  const openFence = `${leadingNl}\`\`\`\n`;
  const closeFence = `\n\`\`\`${trailingNl}`;
  const nextValue = `${before}${openFence}${content}${closeFence}${after}`;
  const innerStart = start + openFence.length;
  return {
    value: nextValue,
    selectionStart: innerStart,
    selectionEnd: innerStart + content.length
  };
}

function transformSelectedLines(state: TextRange, mode: 'bullet' | 'numbered'): TextRange {
  const { value, start, end } = clampRange(state);
  const lineStart = value.lastIndexOf('\n', Math.max(0, start - 1)) + 1;
  const nextNl = value.indexOf('\n', end);
  const lineEnd = nextNl === -1 ? value.length : nextNl;

  const block = value.slice(lineStart, lineEnd);
  const lines = block.split('\n');
  const hasNonEmpty = lines.some(line => line.trim().length > 0);
  const isTarget = (line: string) => lines.length === 1 || !hasNonEmpty || line.trim().length > 0;
  const targeted = lines.filter(isTarget);

  let transformed: string[];
  if (mode === 'bullet') {
    const allBulleted = targeted.length > 0 && targeted.every(line => UNORDERED_PREFIX_RE.test(line));
    transformed = lines.map(line => {
      if (!isTarget(line)) return line;
      if (allBulleted) return line.replace(UNORDERED_PREFIX_RE, '');
      if (ORDERED_PREFIX_RE.test(line)) return line.replace(ORDERED_PREFIX_RE, '- ');
      if (UNORDERED_PREFIX_RE.test(line)) return line;
      return `- ${line}`;
    });
  } else {
    const allNumbered = targeted.length > 0 && targeted.every(line => ORDERED_PREFIX_RE.test(line));
    let itemNum = 1;
    transformed = lines.map(line => {
      if (!isTarget(line)) return line;
      if (allNumbered) return line.replace(ORDERED_PREFIX_RE, '');
      const prefix = `${itemNum++}. `;
      if (UNORDERED_PREFIX_RE.test(line)) return line.replace(UNORDERED_PREFIX_RE, prefix);
      if (ORDERED_PREFIX_RE.test(line)) return line.replace(ORDERED_PREFIX_RE, prefix);
      return `${prefix}${line}`;
    });
  }

  const nextBlock = transformed.join('\n');
  const nextValue = value.slice(0, lineStart) + nextBlock + value.slice(lineEnd);

  if (start === end && lines.length === 1) {
    const delta = nextBlock.length - block.length;
    const caret = Math.max(lineStart, start + delta);
    return { value: nextValue, selectionStart: caret, selectionEnd: caret };
  }

  return {
    value: nextValue,
    selectionStart: lineStart,
    selectionEnd: lineStart + nextBlock.length
  };
}

export function applyBulletList(state: TextRange): TextRange {
  return transformSelectedLines(state, 'bullet');
}

export function applyNumberedList(state: TextRange): TextRange {
  return transformSelectedLines(state, 'numbered');
}

export function applyFormatAction(state: TextRange, action: FormatAction): TextRange {
  switch (action) {
    case 'bold':
      return applyBold(state);
    case 'italic':
      return applyItalic(state);
    case 'inlineCode':
      return applyInlineCode(state);
    case 'codeBlock':
      return applyCodeBlock(state);
    case 'bulletList':
      return applyBulletList(state);
    case 'numberedList':
      return applyNumberedList(state);
  }
}

export function normalizeSafeHttpUrl(rawUrl: string): string | null {
  const trimmed = rawUrl.trim();
  if (!trimmed || /\s|[\u0000-\u001F\u007F]/.test(trimmed)) {
    return null;
  }
  try {
    const parsed = new URL(trimmed);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
      return null;
    }
    if (!parsed.hostname) {
      return null;
    }
    return trimmed;
  } catch {
    return null;
  }
}

function hasBalancedParens(url: string): boolean {
  let depth = 0;
  for (const ch of url) {
    if (ch === '(') depth++;
    else if (ch === ')') {
      depth--;
      if (depth < 0) return false;
    }
  }
  return depth === 0;
}

export function insertLink(state: TextRange, rawUrl: string, rawLabel?: string): InsertLinkResult {
  const safeUrl = normalizeSafeHttpUrl(rawUrl);
  if (!safeUrl) {
    return { ok: false, error: 'Enter a valid http:// or https:// URL.' };
  }

  const { value, start, end } = clampRange(state);
  const selected = value.slice(start, end);
  const chosenLabel = (rawLabel ?? '').trim() || selected.trim() || safeUrl;
  const cleanLabel = chosenLabel.replace(/\r?\n/g, ' ').replace(/\]/g, ')');
  const cleanUrl = hasBalancedParens(safeUrl)
    ? safeUrl
    : safeUrl.replace(/\(/g, '%28').replace(/\)/g, '%29');

  const markdownLink = `[${cleanLabel}](${cleanUrl})`;
  const nextValue = value.slice(0, start) + markdownLink + value.slice(end);
  const caret = start + markdownLink.length;

  return {
    ok: true,
    next: {
      value: nextValue,
      selectionStart: caret,
      selectionEnd: caret
    }
  };
}

export function insertEmoji(state: TextRange, emoji: string): TextRange {
  const { value, start, end } = clampRange(state);
  const nextValue = value.slice(0, start) + emoji + value.slice(end);
  const caret = start + emoji.length;
  return {
    value: nextValue,
    selectionStart: caret,
    selectionEnd: caret
  };
}

export function resolveComposerShortcut(
  event: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey' | 'shiftKey' | 'isComposing' | 'keyCode'>,
  compositionActive: boolean
): ComposerShortcutAction | null {
  if (compositionActive || event.isComposing || event.keyCode === 229) {
    return null;
  }
  if ((!event.ctrlKey && !event.metaKey) || event.altKey || event.shiftKey) {
    return null;
  }
  switch (event.key.toLowerCase()) {
    case 'b':
      return 'bold';
    case 'i':
      return 'italic';
    case 'e':
      return 'inlineCode';
    case 'k':
      return 'link';
    default:
      return null;
  }
}

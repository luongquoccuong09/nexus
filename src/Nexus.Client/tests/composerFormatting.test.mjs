import test from 'node:test';
import assert from 'node:assert/strict';
import {
  applyBold,
  applyBulletList,
  applyCodeBlock,
  applyFormatAction,
  applyInlineCode,
  applyItalic,
  applyNumberedList,
  insertEmoji,
  insertLink,
  normalizeSafeHttpUrl,
  resolveComposerShortcut
} from '../src/composerFormatting.ts';
import { isSafeHttpUrl, renderSafeMarkdown } from '../src/markdown.ts';

test('M6.27: bold, italic, and inline code wrap, unwrap, and preserve selection ranges', () => {
  // Wrap selection
  const bolded = applyBold({ value: 'Hello world', selectionStart: 6, selectionEnd: 11 });
  assert.deepEqual(bolded, { value: 'Hello **world**', selectionStart: 8, selectionEnd: 13 });

  // Toggle off when surrounding markers exist
  const unbolded = applyBold(bolded);
  assert.deepEqual(unbolded, { value: 'Hello world', selectionStart: 6, selectionEnd: 11 });

  // Toggle off when selection includes the markers
  const unboldedOuter = applyBold({ value: 'Hello **world**', selectionStart: 6, selectionEnd: 15 });
  assert.deepEqual(unboldedOuter, { value: 'Hello world', selectionStart: 6, selectionEnd: 11 });

  // Empty selection inserts placeholder and selects it
  const placeholderBold = applyBold({ value: 'Say ', selectionStart: 4, selectionEnd: 4 });
  assert.deepEqual(placeholderBold, { value: 'Say **bold text**', selectionStart: 6, selectionEnd: 15 });

  // Italic does not accidentally strip bold markers; adds/removes single star around bold cleanly
  const boldAndItalic = applyItalic(bolded);
  assert.deepEqual(boldAndItalic, { value: 'Hello ***world***', selectionStart: 9, selectionEnd: 14 });
  const backToBold = applyItalic(boldAndItalic);
  assert.deepEqual(backToBold, { value: 'Hello **world**', selectionStart: 8, selectionEnd: 13 });

  // Inline code wrap & toggle off
  const coded = applyInlineCode({ value: 'run npm test', selectionStart: 4, selectionEnd: 12 });
  assert.deepEqual(coded, { value: 'run `npm test`', selectionStart: 5, selectionEnd: 13 });
  assert.deepEqual(applyInlineCode(coded), { value: 'run npm test', selectionStart: 4, selectionEnd: 12 });
});

test('M6.27: code block wraps with clean newlines and toggles off', () => {
  const empty = applyCodeBlock({ value: '', selectionStart: 0, selectionEnd: 0 });
  assert.deepEqual(empty, { value: '```\ncode\n```', selectionStart: 4, selectionEnd: 8 });
  assert.deepEqual(applyCodeBlock(empty), { value: 'code', selectionStart: 0, selectionEnd: 4 });

  const inline = applyCodeBlock({ value: 'See const x = 1; end', selectionStart: 4, selectionEnd: 16 });
  assert.equal(inline.value, 'See \n```\nconst x = 1;\n```\n end');
  assert.equal(inline.value.slice(inline.selectionStart, inline.selectionEnd), 'const x = 1;');
});

test('M6.27: bullet and numbered lists prefix selected lines, switch modes, and toggle off', () => {
  const base = { value: 'Alpha\nBeta\nGamma', selectionStart: 0, selectionEnd: 16 };
  const bulleted = applyBulletList(base);
  assert.equal(bulleted.value, '- Alpha\n- Beta\n- Gamma');
  assert.equal(bulleted.selectionStart, 0);
  assert.equal(bulleted.selectionEnd, bulleted.value.length);

  // Switch bullet -> numbered
  const numbered = applyNumberedList(bulleted);
  assert.equal(numbered.value, '1. Alpha\n2. Beta\n3. Gamma');

  // Toggle numbered off
  const plain = applyNumberedList(numbered);
  assert.equal(plain.value, 'Alpha\nBeta\nGamma');

  // Collapsed caret on single line adjusts caret accurately
  const single = applyBulletList({ value: 'Item', selectionStart: 4, selectionEnd: 4 });
  assert.deepEqual(single, { value: '- Item', selectionStart: 6, selectionEnd: 6 });
  assert.deepEqual(applyBulletList(single), { value: 'Item', selectionStart: 4, selectionEnd: 4 });

  // Dispatcher covers all actions
  assert.equal(applyFormatAction({ value: 'hi', selectionStart: 0, selectionEnd: 2 }, 'bold').value, '**hi**');
});

test('SEC-10 + M6.27: insertLink validates http/https only and rejects unsafe or invalid URLs', () => {
  for (const bad of [
    '',
    '   ',
    'javascript:alert(1)',
    'JAVASCRIPT:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'vbscript:msgbox(1)',
    'file:///etc/passwd',
    'not-a-url',
    'https://example.com/has space'
  ]) {
    assert.equal(normalizeSafeHttpUrl(bad), null, `Expected reject for ${bad}`);
    const res = insertLink({ value: 'hello', selectionStart: 0, selectionEnd: 5 }, bad, 'label');
    assert.equal(res.ok, false);
  }

  const goodUrl = 'https://docs.example.com/guide?a=1&b=2';
  assert.equal(isSafeHttpUrl(goodUrl), true);

  // Explicit label replaces selection
  const withLabel = insertLink(
    { value: 'Read the docs please', selectionStart: 9, selectionEnd: 13 },
    goodUrl,
    'Guide'
  );
  assert.equal(withLabel.ok, true);
  assert.equal(withLabel.next.value, `Read the [Guide](${goodUrl}) please`);
  assert.equal(withLabel.next.selectionStart, 9 + `[Guide](${goodUrl})`.length);
  assert.equal(withLabel.next.selectionEnd, withLabel.next.selectionStart);

  // Falls back to selected text when label omitted
  const withSelection = insertLink(
    { value: 'Read docs', selectionStart: 5, selectionEnd: 9 },
    'http://example.com'
  );
  assert.equal(withSelection.ok, true);
  assert.equal(withSelection.next.value, 'Read [docs](http://example.com)');

  // Falls back to URL when neither label nor selection present
  const bare = insertLink({ value: 'Link: ', selectionStart: 6, selectionEnd: 6 }, 'https://example.com');
  assert.equal(bare.ok, true);
  assert.equal(bare.next.value, 'Link: [https://example.com](https://example.com)');
  assert.match(renderSafeMarkdown(bare.next.value), /<a href="https:\/\/example\.com" target="_blank" rel="noopener noreferrer">https:\/\/example\.com<\/a>/);
});

test('M6.27: insertEmoji inserts at exact caret/selection and handles multi-code-unit ZWJ emoji', () => {
  const zwj = '👩‍💻';
  const res = insertEmoji({ value: 'Hi team!', selectionStart: 3, selectionEnd: 7 }, zwj);
  assert.equal(res.value, `Hi ${zwj}!`);
  assert.equal(res.selectionStart, 3 + zwj.length);
  assert.equal(res.selectionEnd, 3 + zwj.length);
});

test('M6.27 + M6.19: composer shortcuts match Ctrl/Cmd+B/I/E/K and ignore IME composition', () => {
  const base = { ctrlKey: true, metaKey: false, altKey: false, shiftKey: false, isComposing: false, keyCode: 66 };
  assert.equal(resolveComposerShortcut({ ...base, key: 'b' }, false), 'bold');
  assert.equal(resolveComposerShortcut({ ...base, ctrlKey: false, metaKey: true, key: 'I' }, false), 'italic');
  assert.equal(resolveComposerShortcut({ ...base, key: 'e' }, false), 'inlineCode');
  assert.equal(resolveComposerShortcut({ ...base, key: 'k' }, false), 'link');
  assert.equal(resolveComposerShortcut({ ...base, shiftKey: true, key: 'b' }, false), null);
  assert.equal(resolveComposerShortcut({ ...base, altKey: true, key: 'b' }, false), null);
  assert.equal(resolveComposerShortcut({ ...base, key: 'b', isComposing: true }, false), null);
  assert.equal(resolveComposerShortcut({ ...base, key: 'b' }, true), null);
  assert.equal(resolveComposerShortcut({ ...base, key: 'b', keyCode: 229 }, false), null);
});

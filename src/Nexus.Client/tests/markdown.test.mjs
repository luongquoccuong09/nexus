import test from 'node:test';
import assert from 'node:assert/strict';
import { escapeHtml, isSafeHttpUrl, renderSafeMarkdown } from '../src/markdown.ts';

test('SEC-10: escapes raw HTML and inline event handlers', () => {
  const html = renderSafeMarkdown('<script>alert(1)</script> <img src=x onerror=alert(1)>');
  assert.doesNotMatch(html, /<script/i);
  assert.doesNotMatch(html, /<img/i);
  assert.match(html, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
  assert.match(html, /&lt;img src=x onerror=alert\(1\)&gt;/);
});

test('SEC-10: blocks javascript:, data:, and vbscript: links', () => {
  const jsLink = renderSafeMarkdown('[click](javascript:alert(1))');
  assert.doesNotMatch(jsLink, /<a\b/i);
  assert.doesNotMatch(jsLink, /javascript:/i);
  assert.equal(jsLink, 'click');

  const dataLink = renderSafeMarkdown('[payload](data:text/html,<script>alert(1)</script>)');
  assert.doesNotMatch(dataLink, /<a\b/i);

  assert.equal(isSafeHttpUrl('javascript:alert(1)'), false);
  assert.equal(isSafeHttpUrl('data:text/html,hi'), false);
  assert.equal(isSafeHttpUrl('vbscript:msgbox(1)'), false);
  assert.equal(isSafeHttpUrl('https://nexus.local/path?a=1&b=2'), true);
});

test('M2.9: renders safe Markdown subset and http/https links with rel=noopener noreferrer', () => {
  const rendered = renderSafeMarkdown(
    '**bold** *italic* ~~strike~~ `inline <b>code</b>` [docs](https://example.com/a?x=1&y=2)\n```\nconst a = "<tag>";\n```'
  );
  assert.match(rendered, /<strong>bold<\/strong>/);
  assert.match(rendered, /<em>italic<\/em>/);
  assert.match(rendered, /<del>strike<\/del>/);
  assert.match(rendered, /<code>inline &lt;b&gt;code&lt;\/b&gt;<\/code>/);
  assert.match(
    rendered,
    /<a href="https:\/\/example\.com\/a\?x=1&amp;y=2" target="_blank" rel="noopener noreferrer">docs<\/a>/
  );
  assert.match(rendered, /<pre class="nx-code-block"><code>const a = &quot;&lt;tag&gt;&quot;;<\/code><\/pre>/);
  assert.equal(escapeHtml(`"a'&<>`), '&quot;a&#39;&amp;&lt;&gt;');
});

test('M6.27: renders unordered (- and *) and ordered (1.) lists safely', () => {
  const ulDash = renderSafeMarkdown('Checklist:\n- **First** item\n- `code` and [link](https://example.com)\nDone');
  assert.equal(
    ulDash,
    'Checklist:<ul class="nx-md-list"><li><strong>First</strong> item</li><li><code>code</code> and <a href="https://example.com" target="_blank" rel="noopener noreferrer">link</a></li></ul>Done'
  );

  const ulStar = renderSafeMarkdown('* Alpha\n* *Beta*');
  assert.equal(ulStar, '<ul class="nx-md-list"><li>Alpha</li><li><em>Beta</em></li></ul>');

  const ol = renderSafeMarkdown('1. Prepare release\n2. Run tests\n3. Deploy');
  assert.equal(
    ol,
    '<ol class="nx-md-list"><li>Prepare release</li><li>Run tests</li><li>Deploy</li></ol>'
  );

  // Switching between unordered and ordered lists closes the previous list cleanly
  const mixed = renderSafeMarkdown('- Bullet\n1. Numbered');
  assert.equal(mixed, '<ul class="nx-md-list"><li>Bullet</li></ul><ol class="nx-md-list"><li>Numbered</li></ol>');

  // List syntax inside fenced code blocks must stay literal code
  const fenced = renderSafeMarkdown('```\n- not a list\n1. also code\n```');
  assert.doesNotMatch(fenced, /<ul|<ol|<li/i);
  assert.match(fenced, /<pre class="nx-code-block"><code>- not a list\n1\. also code<\/code><\/pre>/);
});

test('SEC-10 + M6.27: escapes HTML and blocks unsafe links inside list items', () => {
  const rendered = renderSafeMarkdown(
    '- <script>alert(1)</script>\n- [bad](javascript:alert(1))\n1. <img src=x onerror=alert(1)> [data](data:text/html,hi)'
  );
  assert.doesNotMatch(rendered, /<script/i);
  assert.doesNotMatch(rendered, /<img/i);
  assert.doesNotMatch(rendered, /<a\b/i);
  assert.doesNotMatch(rendered, /javascript:/i);
  assert.match(rendered, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
  assert.match(rendered, /<li>bad<\/li>/);
  assert.match(rendered, /&lt;img src=x onerror=alert\(1\)&gt;/);
});

test('M6.27: preserves complex Unicode emoji (ZWJ, Fitzpatrick skin tones, variation selectors, flags)', () => {
  const sample = 'Shipped! 🚀 👩‍💻 👨‍👩‍👧‍👦 👍🏽 👋🏿 ✌️ 🇺🇸 🇻🇳 🏳️‍🌈';
  const rendered = renderSafeMarkdown(`- **${sample}**`);
  assert.equal(rendered, `<ul class="nx-md-list"><li><strong>${sample}</strong></li></ul>`);
});

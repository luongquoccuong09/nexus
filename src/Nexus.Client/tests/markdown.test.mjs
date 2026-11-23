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

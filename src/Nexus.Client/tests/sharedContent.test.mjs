import test from 'node:test';
import assert from 'node:assert/strict';
import {
  buildSharedContentUrl,
  formatSharedHost,
  mergeSharedItems,
  resolveSafeSharedHref
} from '../src/shared/sharedContentHelpers.ts';

test('SEC-10 + M6.32: resolveSafeSharedHref allows http/https and rejects unsafe schemes', () => {
  assert.equal(resolveSafeSharedHref('https://docs.example.com/guide?a=1'), 'https://docs.example.com/guide?a=1');
  assert.equal(resolveSafeSharedHref('http://intranet.local/page'), 'http://intranet.local/page');
  assert.equal(resolveSafeSharedHref('javascript:alert(1)'), null);
  assert.equal(resolveSafeSharedHref('data:text/html,<script>alert(1)</script>'), null);
  assert.equal(resolveSafeSharedHref('vbscript:msgbox(1)'), null);
  assert.equal(resolveSafeSharedHref('file:///etc/passwd'), null);
  assert.equal(resolveSafeSharedHref('   '), null);
  assert.equal(resolveSafeSharedHref(null), null);
});

test('M6.32: buildSharedContentUrl formats personal and organization scoped URLs with 50 cap and trimmed q', () => {
  const personalUrl = buildSharedContentUrl(
    { kind: 'personal' },
    'conv-1',
    'links',
    { before: '2026-11-24T00:00:00Z', beforeId: 'item-1' },
    '  roadmap  '
  );
  assert.equal(
    personalUrl,
    '/api/personal/conversations/conv-1/shared?tab=links&limit=50&before=2026-11-24T00%3A00%3A00Z&beforeId=item-1&q=roadmap'
  );

  const orgUrl = buildSharedContentUrl(
    { kind: 'organization', organizationId: 'org-1' },
    'chan-2',
    'media',
    null,
    ''
  );
  assert.equal(orgUrl, '/api/organizations/org-1/channels/chan-2/shared?tab=media&limit=50');
});

test('M6.32: mergeSharedItems deduplicates by id and formatSharedHost resolves hostnames', () => {
  const a = { id: '1', conversationId: 'c', senderUserId: 'u', senderDisplayName: 'Alice', createdAt: '2026-11-24T00:00:00Z', url: 'https://nexus.example.test/docs' };
  const b = { id: '2', conversationId: 'c', senderUserId: 'u', senderDisplayName: 'Bob', createdAt: '2026-11-23T00:00:00Z', host: 'custom.host' };
  const aUpdated = { ...a, senderDisplayName: 'Alice Updated' };

  const appended = mergeSharedItems([a], [aUpdated, b], true);
  assert.equal(appended.length, 2);
  assert.equal(appended[0].senderDisplayName, 'Alice Updated');
  assert.equal(appended[1].id, '2');

  const replaced = mergeSharedItems([a, b], [b], false);
  assert.equal(replaced.length, 1);
  assert.equal(replaced[0].id, '2');

  assert.equal(formatSharedHost(a), 'nexus.example.test');
  assert.equal(formatSharedHost(b), 'custom.host');
});

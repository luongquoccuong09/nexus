import test from 'node:test';
import assert from 'node:assert/strict';
import { buildContentUrl, encodeContentLink, extractContentLinkToken } from '../src/contentLinks.ts';

test('M6.25: encodes opaque message, post, and channel links without raw titles or content', () => {
  const msgToken = encodeContentLink({
    kind: 'message',
    organizationId: null,
    conversationId: '11111111-1111-1111-1111-111111111111',
    messageId: '22222222-2222-2222-2222-222222222222'
  });
  assert.match(msgToken, /^[A-Za-z0-9_-]+$/);
  const decodedMsg = JSON.parse(Buffer.from(msgToken, 'base64url').toString('utf8'));
  assert.deepEqual(decodedMsg, {
    k: 'm',
    o: null,
    c: '11111111-1111-1111-1111-111111111111',
    t: '22222222-2222-2222-2222-222222222222'
  });

  const postToken = encodeContentLink({
    kind: 'post',
    organizationId: '33333333-3333-3333-3333-333333333333',
    conversationId: '44444444-4444-4444-4444-444444444444',
    postId: '55555555-5555-5555-5555-555555555555'
  });
  const url = buildContentUrl(postToken, 'https://nexus.example.test');
  assert.equal(extractContentLinkToken(url, 'https://nexus.example.test'), postToken);
  assert.equal(extractContentLinkToken(url, 'https://evil.example.test'), null);

  const chanToken = encodeContentLink({
    kind: 'channel',
    organizationId: '33333333-3333-3333-3333-333333333333',
    conversationId: '44444444-4444-4444-4444-444444444444'
  });
  const decodedChan = JSON.parse(Buffer.from(chanToken, 'base64url').toString('utf8'));
  assert.deepEqual(decodedChan, {
    k: 'c',
    o: '33333333-3333-3333-3333-333333333333',
    c: '44444444-4444-4444-4444-444444444444',
    t: null
  });
});

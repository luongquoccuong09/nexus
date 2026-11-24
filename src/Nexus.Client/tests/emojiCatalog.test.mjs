import test from 'node:test';
import assert from 'node:assert/strict';
import {
  EMOJI_CATALOG,
  EMOJI_CATEGORIES,
  EMOJIS_BY_CATEGORY,
  SKIN_TONES,
  applySkinTone,
  searchEmojis
} from '../src/emojiCatalog.ts';

test('M6.27: emoji catalog has >= 320 native Unicode entries across all categories', () => {
  assert.ok(EMOJI_CATALOG.length >= 320, `Expected >= 320 emojis, found ${EMOJI_CATALOG.length}`);
  assert.equal(SKIN_TONES.length, 6);
  for (const cat of EMOJI_CATEGORIES) {
    assert.ok(EMOJIS_BY_CATEGORY[cat.id].length >= 25, `Category ${cat.id} should be populated`);
  }
});

test('M6.27: applySkinTone handles simple, FE0F, ZWJ, and non-tone-capable emojis cleanly', () => {
  // Simple tone-capable emoji
  assert.equal(applySkinTone('👍', 'medium', true), '👍🏽');
  assert.equal(applySkinTone('👍', 'default', true), '👍');

  // Non-tone-capable emoji stays untouched
  assert.equal(applySkinTone('😀', 'dark', false), '😀');

  // Variation selector-16 (U+FE0F) stripped before Fitzpatrick modifier per UTS #51
  assert.equal(applySkinTone('✌️', 'medium-dark', true), '\u270C\u{1F3FE}');

  // ZWJ sequence places modifier after the first base code point
  assert.equal(applySkinTone('👩‍💻', 'light', true), '👩🏻‍💻');
  assert.equal(applySkinTone('🤷‍♀️', 'dark', true), '🤷🏿‍♀️');
});

test('M6.27: searchEmojis matches multi-term names and keywords case-insensitively', () => {
  const rocketHits = searchEmojis('deploy launch');
  assert.ok(rocketHits.some(e => e.emoji === '🚀'));

  const devHits = searchEmojis('developer laptop');
  assert.ok(devHits.some(e => e.emoji === '👩‍💻'));

  const flagHits = searchEmojis('vietnam');
  assert.ok(flagHits.some(e => e.emoji === '🇻🇳'));

  assert.equal(searchEmojis('xyznonexistentemoji123').length, 0);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import {
  RESERVED_TAG_SLUGS,
  slugifyTagName,
  validateTagSlug,
  insertTagMention,
  formatTagApiError
} from '../src/tags/tagHelpers.ts';

test('slugifyTagName generates clean lowercase handles from display names', () => {
  assert.equal(slugifyTagName('On-Call Engineering'), 'on-call-engineering');
  assert.equal(slugifyTagName('  Design   Systems!!  '), 'design-systems');
  assert.equal(slugifyTagName('Café & Bistro Ops'), 'cafe-bistro-ops');
  assert.equal(slugifyTagName('___QA_Leads___'), 'qa_leads');
  assert.equal(slugifyTagName('A very long team name that exceeds thirty two characters easily'), 'a-very-long-team-name-that-excee');
  assert.equal(slugifyTagName('!!!'), '');
});

test('validateTagSlug accepts valid handles and rejects invalid or reserved ones', () => {
  assert.deepEqual(validateTagSlug('on-call'), { valid: true, slug: 'on-call' });
  assert.deepEqual(validateTagSlug('  QA_Team2 '), { valid: true, slug: 'qa_team2' });
  assert.deepEqual(validateTagSlug('a1'), { valid: true, slug: 'a1' });

  assert.equal(validateTagSlug('').valid, false);
  assert.equal(validateTagSlug('a').valid, false);
  assert.equal(validateTagSlug('-leading-hyphen').valid, false);
  assert.equal(validateTagSlug('_leading-underscore').valid, false);
  assert.equal(validateTagSlug('has space').valid, false);
  assert.equal(validateTagSlug('has.dot').valid, false);
  assert.equal(validateTagSlug('a'.repeat(33)).valid, false);

  for (const reserved of RESERVED_TAG_SLUGS) {
    const res = validateTagSlug(reserved);
    assert.equal(res.valid, false, `Expected "${reserved}" to be rejected`);
    if (!res.valid) {
      assert.match(res.error, /reserved/i);
    }
  }
  assert.equal(validateTagSlug('EVERYONE').valid, false);
});

test('insertTagMention inserts @slug at caret with clean spacing and caret position', () => {
  const atEmpty = insertTagMention('', 0, 0, 'on-call');
  assert.deepEqual(atEmpty, {
    value: '@on-call ',
    selectionStart: 9,
    selectionEnd: 9
  });

  const midText = insertTagMention('Please notify about outage', 13, 13, 'on-call');
  assert.deepEqual(midText, {
    value: 'Please notify @on-call about outage',
    selectionStart: 23,
    selectionEnd: 23
  });

  const replacingSelection = insertTagMention('Ping [team] now', 5, 11, 'design');
  assert.deepEqual(replacingSelection, {
    value: 'Ping @design now',
    selectionStart: 13,
    selectionEnd: 13
  });

  const gluedWord = insertTagMention('Hello', 5, 5, 'qa');
  assert.deepEqual(gluedWord, {
    value: 'Hello @qa ',
    selectionStart: 10,
    selectionEnd: 10
  });

  const stripsLeadingAt = insertTagMention('Hi ', 3, 3, '@Backend');
  assert.deepEqual(stripsLeadingAt, {
    value: 'Hi @backend ',
    selectionStart: 12,
    selectionEnd: 12
  });
});

test('formatTagApiError returns clear messages for 409, 403, 404, 400', () => {
  assert.match(formatTagApiError(409, 'tag_slug_exists'), /already exists/i);
  assert.match(formatTagApiError(403), /permission/i);
  assert.equal(formatTagApiError(400, null, 'Reserved handle.'), 'Reserved handle.');
});

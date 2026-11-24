import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MAX_VOICE_NOTE_BYTES,
  MAX_VOICE_NOTE_DURATION_MS,
  selectSupportedAudioMimeType,
  normalizeAudioMimeType,
  voiceNoteFileExtension,
  buildVoiceNoteFilename,
  formatVoiceDuration,
  mapMicrophoneError
} from '../src/voice/voiceNoteHelpers.ts';

test('M6.34: voice note limits match D08 interim policy (120s, 5 MB)', () => {
  assert.equal(MAX_VOICE_NOTE_DURATION_MS, 120_000);
  assert.equal(MAX_VOICE_NOTE_BYTES, 5 * 1024 * 1024);
});

test('M6.34: selectSupportedAudioMimeType picks best supported format in priority order', () => {
  assert.equal(
    selectSupportedAudioMimeType(m => m === 'audio/webm;codecs=opus' || m === 'audio/ogg'),
    'audio/webm;codecs=opus'
  );
  assert.equal(
    selectSupportedAudioMimeType(m => m === 'audio/ogg;codecs=opus' || m === 'audio/mp4'),
    'audio/ogg;codecs=opus'
  );
  assert.equal(
    selectSupportedAudioMimeType(m => m === 'audio/mp4'),
    'audio/mp4'
  );
  assert.equal(selectSupportedAudioMimeType(() => false), null);
  assert.equal(selectSupportedAudioMimeType(undefined), null);
});

test('M6.34: normalizeAudioMimeType, voiceNoteFileExtension, and buildVoiceNoteFilename', () => {
  assert.equal(normalizeAudioMimeType('audio/webm;codecs=opus'), 'audio/webm');
  assert.equal(normalizeAudioMimeType(' AUDIO/MP4 ; codecs=mp4a.40.2 '), 'audio/mp4');
  assert.equal(voiceNoteFileExtension('audio/webm;codecs=opus'), '.webm');
  assert.equal(voiceNoteFileExtension('audio/ogg;codecs=opus'), '.ogg');
  assert.equal(voiceNoteFileExtension('audio/mp4'), '.m4a');

  const sampleDate = new Date('2026-11-24T09:05:03Z');
  assert.equal(buildVoiceNoteFilename('audio/webm;codecs=opus', sampleDate), 'voice-note-20261124-090503.webm');
  assert.equal(buildVoiceNoteFilename('audio/mp4', sampleDate), 'voice-note-20261124-090503.m4a');
});

test('M6.34: formatVoiceDuration formats milliseconds cleanly as M:SS', () => {
  assert.equal(formatVoiceDuration(0), '0:00');
  assert.equal(formatVoiceDuration(-500), '0:00');
  assert.equal(formatVoiceDuration(null), '0:00');
  assert.equal(formatVoiceDuration(14_000), '0:14');
  assert.equal(formatVoiceDuration(65_000), '1:05');
  assert.equal(formatVoiceDuration(119_400), '1:59');
  assert.equal(formatVoiceDuration(120_000), '2:00');
});

test('M6.34: mapMicrophoneError produces clear plain-English guidance', () => {
  assert.match(mapMicrophoneError({ name: 'NotAllowedError' }), /permission/i);
  assert.match(mapMicrophoneError({ name: 'PermissionDeniedError' }), /permission/i);
  assert.match(mapMicrophoneError({ name: 'NotFoundError' }), /no microphone/i);
  assert.match(mapMicrophoneError({ name: 'NotReadableError' }), /another app/i);
  assert.match(mapMicrophoneError({ name: 'SecurityError' }), /HTTPS/i);
  assert.match(mapMicrophoneError({ name: 'UnsupportedBrowser' }), /not supported/i);
  assert.match(mapMicrophoneError(new Error('unknown')), /unable to access/i);
});

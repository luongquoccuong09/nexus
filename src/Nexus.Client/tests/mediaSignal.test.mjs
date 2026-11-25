import assert from 'node:assert/strict';
import test from 'node:test';
import { mediaSignalUrl } from '../src/meetings/mediaSignal.ts';

test('M6.12 meeting signaling uses the app origin and upgrades HTTPS to WSS', () => {
  assert.equal(mediaSignalUrl('https://nexus.example:8443', '/api/media'), 'wss://nexus.example:8443/api/media');
});

test('M6.12 meeting signaling allows HTTP on loopback for local development', () => {
  assert.equal(mediaSignalUrl('http://127.0.0.1:5080', '/api/media'), 'ws://127.0.0.1:5080/api/media');
  assert.equal(mediaSignalUrl('http://[::1]:5080', '/api/media'), 'ws://[::1]:5080/api/media');
  assert.throws(() => mediaSignalUrl('http://nexus.example', '/api/media'));
});

test('M6.12 meeting signaling accepts only the Nexus gateway path', () => {
  for (const path of ['https://nexus.example/api/media', '//media.example/api/media', '/api/media/../other', '/api/media?token=unexpected']) {
    assert.throws(() => mediaSignalUrl('https://nexus.example', path));
  }
});

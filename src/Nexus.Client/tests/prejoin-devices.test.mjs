import assert from 'node:assert/strict';
import test from 'node:test';
import {
  acquirePreviewStream, microphoneLevel, permissionFromCapture, permissionFromQuery, permissionStatus, previewConstraints,
  previewDeviceOptions, previewError
} from '../src/meetings/preJoinCapture.ts';

function captureFixture() {
  const tracks = Array.from({ length: 2 }, () => ({ stopCount: 0, stop() { this.stopCount++; } }));
  return { tracks, stream: { getTracks: () => tracks } };
}

test('pre-join stops tracks that resolve after the user left the preview', async () => {
  const { tracks, stream } = captureFixture();
  const controller = new AbortController();
  let acquired;
  const result = acquirePreviewStream(() => new Promise(resolve => { acquired = resolve; }), controller.signal);
  controller.abort();
  acquired(stream);
  assert.equal(await result, null);
  assert.deepEqual(tracks.map(track => track.stopCount), [1, 1]);
});

test('replacing a preview releases the old capture and a dismissed preview never requests a device', async () => {
  const first = captureFixture();
  const firstRequest = new AbortController();
  assert.equal(await acquirePreviewStream(async () => first.stream, firstRequest.signal), first.stream);
  firstRequest.abort();
  firstRequest.abort();
  assert.deepEqual(first.tracks.map(track => track.stopCount), [1, 1]);
  let requested = false;
  assert.equal(await acquirePreviewStream(async () => { requested = true; }, firstRequest.signal), null);
  assert.equal(requested, false);
});

test('pre-join selects one input at a time and omits inaccessible and duplicate device IDs', () => {
  assert.deepEqual(previewConstraints('camera', 'camera-id'), { audio: false, video: { deviceId: { exact: 'camera-id' } } });
  assert.deepEqual(previewConstraints('microphone', ''), { audio: true, video: false });
  const microphone = (deviceId, label) => ({ deviceId, label, kind: 'audioinput' });
  assert.deepEqual(previewDeviceOptions([
    microphone('', ''), microphone('default', 'System default'), microphone('one', ''),
    microphone('one', 'Duplicate'), microphone('two', 'USB headset'),
    { deviceId: 'camera', kind: 'videoinput', label: 'Camera' }
  ], 'audioinput'), [{ id: 'one', label: 'Microphone 1' }, { id: 'two', label: 'USB headset' }]);
});

test('pre-join reports actionable permission errors and measures microphone activity from zero to full level', () => {
  assert.match(previewError('camera', new DOMException('', 'NotAllowedError')), /permission was denied/);
  assert.match(previewError('microphone', { name: 'NotFoundError' }), /No microphone was detected/);
  assert.equal(microphoneLevel(new Uint8Array([128, 128, 128])), 0);
  assert.ok(microphoneLevel(new Uint8Array([96, 160, 128])) > 0);
  assert.equal(microphoneLevel(new Uint8Array([0, 255])), 100);
});

test('pre-join reports permission state from the query or from a user-started capture', () => {
  assert.equal(permissionFromQuery('granted'), 'granted');
  assert.equal(permissionFromQuery('prompt'), 'prompt');
  assert.equal(permissionFromQuery(undefined), 'unknown');
  assert.equal(permissionFromCapture(null), 'granted');
  assert.equal(permissionFromCapture(new DOMException('', 'NotAllowedError')), 'denied');
  assert.equal(permissionFromCapture({ name: 'NotFoundError' }), null);
  assert.equal(permissionStatus('camera', 'granted'), 'Camera access: Allowed');
  assert.match(permissionStatus('microphone', 'denied'), /^Microphone access: Blocked\. Allow microphone access/);
  assert.match(permissionStatus('camera', 'prompt'), /ask when you start the preview/);
  assert.match(permissionStatus('microphone', 'unknown'), /Checked when you start the test/);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const worker = readFileSync(new URL('../public/nexus-sw.js', import.meta.url), 'utf8');

function loadWorker(clients = {}) {
  const handlers = new Map();
  const notifications = [];
  runInNewContext(worker, {
    URL,
    self: {
      location: { origin: 'https://nexus.example.test' },
      addEventListener: (event, handler) => handlers.set(event, handler),
      registration: {
        showNotification: async (title, options) => notifications.push({ title, ...options })
      },
      clients
    }
  });
  return { handlers, notifications };
}

test('push shows a generic coalesced alert without reading the payload', async () => {
  const { handlers, notifications } = loadWorker();
  let completion;
  handlers.get('push')({
    get data() { throw new Error('The worker should never read a private payload.'); },
    waitUntil: promise => { completion = promise; }
  });
  await completion;
  assert.deepEqual(notifications, [{
    title: 'Nexus', body: 'You have new activity.', tag: 'nexus-activity', icon: '/icons/nexus-192.png'
  }]);
});

test('click dismisses the alert and reuses a same-origin window for Activity', async () => {
  let closed = false;
  const actions = [];
  const { handlers } = loadWorker({
    matchAll: async () => [
      { url: 'https://external.example.test/' },
      {
        url: 'https://nexus.example.test/',
        navigate: async url => {
          actions.push(url);
          return { focus: async () => actions.push('focus') };
        }
      }
    ],
    openWindow: async () => assert.fail('An existing Nexus window should be reused.')
  });
  let completion;
  handlers.get('notificationclick')({
    notification: { close: () => { closed = true; } },
    waitUntil: promise => { completion = promise; }
  });
  await completion;
  assert.equal(closed, true);
  assert.deepEqual(actions, ['https://nexus.example.test/?view=activity', 'focus']);
});

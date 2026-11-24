self.addEventListener('push', event => {
  event.waitUntil(self.registration.showNotification('Nexus', {
    body: 'You have new activity.',
    tag: 'nexus-activity',
    icon: '/icons/nexus-192.png'
  }));
});

self.addEventListener('notificationclick', event => {
  event.notification.close();
  event.waitUntil((async () => {
    const target = new URL('/?view=activity', self.location.origin);
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const existing = windows.find(client => new URL(client.url).origin === target.origin);
    if (existing) {
      const updated = await existing.navigate(target.href);
      if (updated) {
        await updated.focus();
        return;
      }
    }
    await self.clients.openWindow(target.href);
  })());
});

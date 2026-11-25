export function mediaSignalUrl(origin: string, path: string) {
  if (path !== '/api/media') throw new Error('Meeting signaling is unavailable.');
  const url = new URL(path, origin);
  if (url.protocol === 'https:') {
    url.protocol = 'wss:';
  } else if (url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)) {
    url.protocol = 'ws:';
  } else {
    throw new Error('Meetings require a secure connection.');
  }
  return url.toString();
}

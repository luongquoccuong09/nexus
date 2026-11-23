import { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';

function App() {
  const [status, setStatus] = useState('Connecting…');

  useEffect(() => {
    const controller = new AbortController();
    async function checkHost() {
      try {
        const response = await fetch('/api/version', { signal: controller.signal });
        if (!response.ok) throw new Error('API unavailable');
        const data = await response.json();
        if (typeof data.version !== 'string') throw new Error('Missing version');
        setStatus(`Connected · API v${data.version}`);
      } catch {
        if (!controller.signal.aborted) setStatus('Unable to connect to the API.');
      }
    }
    void checkHost();
    return () => controller.abort();
  }, []);

  return <main><h1>Nexus</h1><p role="status">{status}</p></main>;
}

createRoot(document.getElementById('root')!).render(<App />);

import { useEffect, useRef } from 'react';
import { Card } from '../ui';
import { count, decimal, memory } from './types';

export type Range = '5m' | '15m' | '1h';
const windows: Record<Range, number> = { '5m': 300, '15m': 900, '1h': 3600 };

export interface PerformancePoint {
  timestamp: string;
  processCpuCores: number;
  processMemoryBytes: number;
  uptimeSeconds: number;
  threads: number;
  queuedWork: number;
  gcPauseMs: number;
  onlineAccounts: number;
  sockets: number;
  requestsPerSecond: number;
  http4xx: number;
  http429: number;
  http5xx: number;
  meanMs: number | null;
  p50Ms: number | null;
  p95Ms: number | null;
  p99Ms: number | null;
  dbP95Ms: number | null;
  poolUsed: number | null;
  poolIdle: number | null;
  messages: number;
  posts: number;
  comments: number;
  reactions: number;
  uploads: number;
  fanoutP95Ms: number | null;
}
export interface SandboxPoint {
  timestamp: string;
  rps: number;
  users: number;
  sockets: number;
  cpuPercent: number | null;
  memoryBytes: number | null;
  limitBytes: number | null;
  failed: number;
  throttled: number;
}
type Point = { timestamp: string; value: number | null };

function Trend({ title, value, points, range }: { title: string; value: string; points: Point[]; range: Range }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const element = canvas.current;
    if (!element) return;
    function draw() {
      if (!element) return;
      const width = element.clientWidth;
      const height = element.clientHeight;
      const scale = window.devicePixelRatio || 1;
      element.width = width * scale;
      element.height = height * scale;
      const context = element.getContext('2d');
      if (!context || !width || !height) return;
      context.scale(scale, scale);
      const style = getComputedStyle(element);
      context.strokeStyle = style.getPropertyValue('--border-soft').trim();
      context.beginPath();
      for (const mark of [.25, .5, .75]) {
        context.moveTo(0, height * mark);
        context.lineTo(width, height * mark);
      }
      context.stroke();
      const max = Math.max(1, ...points.map(point => point.value ?? 0));
      const end = Date.now();
      const start = end - windows[range] * 1000;
      context.strokeStyle = style.color;
      context.fillStyle = style.color;
      context.lineWidth = 2;
      let lastAt = 0;
      context.beginPath();
      for (const point of points) {
        const at = Date.parse(point.timestamp);
        if (at < start || point.value == null) { lastAt = 0; continue; }
        const x = Math.max(2, Math.min(width - 2, (at - start) * width / (end - start)));
        const y = Math.max(2, height - point.value * (height - 8) / max - 4);
        if (lastAt && at - lastAt < 20000) context.lineTo(x, y);
        else {
          context.moveTo(x, y);
          context.fillRect(x - 1, y - 1, 2, 2);
        }
        lastAt = at;
      }
      context.stroke();
    }
    const size = new ResizeObserver(draw);
    size.observe(element);
    const theme = new MutationObserver(draw);
    theme.observe(document.documentElement, { attributes: true });
    theme.observe(document.body, { attributes: true });
    draw();
    return () => { size.disconnect(); theme.disconnect(); };
  }, [points, range]);
  return <figure className="nx-monitor-trend">
    <figcaption><span>{title}</span><strong>{value}</strong></figcaption>
    {points.some(point => point.value != null) && <canvas ref={canvas} aria-hidden="true" />}
  </figure>;
}

export function Metrics({ data }: { data: [string, string][] }) {
  return <dl className="nx-monitor-metrics">{data.map(([title, value]) =>
    <div key={title}><dt>{title}</dt><dd>{value}</dd></div>
  )}</dl>;
}

export function Performance({ points, sandbox, range }: { points: PerformancePoint[]; sandbox: SandboxPoint[] | null; range: Range }) {
  const since = Date.now() - windows[range] * 1000;
  const samples = sandbox?.filter(point => Date.parse(point.timestamp) >= since);
  if (samples) {
    const latest = samples.at(-1);
    function trend(title: string, metric: 'cpuPercent' | 'memoryBytes' | 'rps' | 'sockets', value: string) {
      return <Trend title={title} value={value} range={range} points={samples!.map(point => ({ timestamp: point.timestamp, value: point[metric] }))} />;
    }
    return <div className="nx-monitor-cards">
      <Card title="System resources">
        {trend('Web container CPU', 'cpuPercent', decimal(latest?.cpuPercent, '%'))}
        {trend('Web container memory', 'memoryBytes', memory(latest?.memoryBytes))}
        <Metrics data={ [['Web memory limit', memory(latest?.limitBytes)]] } />
      </Card>
      <Card title="Application performance">
        {trend('HTTP requests per second', 'rps', decimal(latest?.rps))}
        <Metrics data={ [['Failed requests', count(latest?.failed)], ['HTTP 429', count(latest?.throttled)]] } />
      </Card>
      <Card title="Realtime and messaging">
        {trend('SignalR connections', 'sockets', count(latest?.sockets))}
        <Metrics data={ [['Active virtual users', count(latest?.users)]] } />
      </Card>
    </div>;
  }

  const latest = points.at(-1);
  function trend(title: string, metric: Exclude<keyof PerformancePoint, 'timestamp'>, value: string) {
    return <Trend title={title} value={value} points={points.map(point =>
      ({ timestamp: point.timestamp, value: point[metric] }))} range={range} />;
  }
  function total(kind: 'messages' | 'posts' | 'comments' | 'reactions' | 'uploads') {
    return points.length ? count(points.reduce((sum, point) => sum + point[kind], 0)) : 'Unavailable';
  }
  return <div className="nx-monitor-cards">
    <Card title="System resources">
      {trend('Process CPU', 'processCpuCores', decimal(latest?.processCpuCores, ' cores'))}
      {trend('Process memory', 'processMemoryBytes', memory(latest?.processMemoryBytes))}
      <Metrics data={ [
        ['Process uptime', decimal(latest?.uptimeSeconds, ' s')], ['Thread pool threads', count(latest?.threads)],
        ['Thread pool queue', count(latest?.queuedWork)], ['GC pause in last sample', decimal(latest?.gcPauseMs, ' ms')],
        ['Host network', 'Unavailable'], ['Host disk', 'Unavailable']
      ] } />
    </Card>
    <Card title="Application performance">
      {trend('HTTP requests per second', 'requestsPerSecond', decimal(latest?.requestsPerSecond))}
      {trend('HTTP p95 in last sample', 'p95Ms', decimal(latest?.p95Ms, ' ms'))}
      <Metrics data={ [
        ['Mean', decimal(latest?.meanMs, ' ms')], ['p50', decimal(latest?.p50Ms, ' ms')], ['p99', decimal(latest?.p99Ms, ' ms')],
        ['HTTP 4xx', count(latest?.http4xx)], ['HTTP 429', count(latest?.http429)], ['HTTP 5xx', count(latest?.http5xx)],
        ['Database p95', decimal(latest?.dbP95Ms, ' ms')], ['Pool used', count(latest?.poolUsed)], ['Pool idle', count(latest?.poolIdle)]
      ] } />
    </Card>
    <Card title="Realtime and messaging">
      {trend('SignalR connections', 'sockets', count(latest?.sockets))}
      <Metrics data={ [
        ['Online accounts', count(latest?.onlineAccounts)], ['Message fanout p95', decimal(latest?.fanoutP95Ms, ' ms')],
        ['Client reconnects', 'Unavailable'], ['Client sync failures', 'Unavailable']
      ] } />
    </Card>
    <Card title="Business activity">
      {trend('Messages per sample', 'messages', count(latest?.messages))}
      <Metrics data={ [
        ['Messages in window', total('messages')], ['Posts in window', total('posts')],
        ['Comments in window', total('comments')], ['Reactions in window', total('reactions')],
        ['Uploads in window', total('uploads')], ['DAU', 'Unavailable'], ['WAU', 'Unavailable']
      ] } />
    </Card>
  </div>;
}

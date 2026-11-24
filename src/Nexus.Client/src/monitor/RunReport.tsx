import { ChartNoAxesCombined, Square } from 'lucide-react';
import { Badge, Button, Card } from '../ui';
import { Metrics } from './Performance';
import { Run, RunDetail, Snapshot, count, decimal, label, memory } from './types';

export function RunReport({ detail, stale, canStop, busy, stop, showPerformance }: {
  detail: RunDetail | null; stale: boolean; canStop: boolean; busy: boolean;
  stop: (id: string) => void; showPerformance: () => void;
}) {
  const run = detail?.run;
  if (!run) return <Card title="Run status"><p className="nx-monitor-note">No run selected.</p></Card>;
  const report = '/api/monitor/runs/' + run.id + '/report?format=';
  return <Card title="Run status">
    <div className="nx-monitor-actions">
      <Badge variant="role">{label(run.status)}</Badge>
      {run.stopRequested && run.active && <Badge variant="role">Stop requested</Badge>}
      {run.result && <Badge variant={run.result === 'Pass' ? 'success' : 'role'}>{run.result}</Badge>}
      {run.active && canStop && <Button size="sm" variant="danger" disabled={busy || run.stopRequested}
        onClick={() => stop(run.id)}><Square size={13} aria-hidden="true" /> Stop</Button>}
    </div>
    {run.active && <progress aria-label="Test time completed" max={run.configuration.durationMinutes * 60}
      value={run.snapshot?.elapsedSeconds ?? 0} />}
    {run.snapshot && <RunMetrics snapshot={run.snapshot} run={run} />}
    {run.snapshot?.reason && <p className="nx-monitor-note">{label(run.snapshot.reason)}</p>}
    {(stale || run.active && run.heartbeatAt && Date.now() - Date.parse(run.heartbeatAt) > 15000) &&
      <p className="nx-monitor-note">Runner updates are stale.</p>}
    {run.cleaned === false && <p className="nx-monitor-error">The sandbox still needs cleanup.</p>}
    {!!detail?.events.length && <ul className="nx-monitor-events" aria-label="Run events">
      {detail.events.map((event, index) => <li key={event.occurredAt + index}>
        <time dateTime={event.occurredAt}>{new Date(event.occurredAt).toLocaleTimeString()}</time> {label(event.action)}
      </li>)}
    </ul>}
    <div className="nx-monitor-actions">
      <a className="nx-btn sm" href={report + 'json'}>Export JSON</a>
      <a className="nx-btn sm" href={report + 'html'}>Export HTML</a>
      <Button size="sm" onClick={showPerformance}><ChartNoAxesCombined size={15} aria-hidden="true" /> Sandbox performance</Button>
    </div>
  </Card>;
}

function RunMetrics({ snapshot, run }: { snapshot: Snapshot; run: Run }) {
  const total = snapshot.successes + snapshot.failures;
  return <>
    <Metrics data={ [
      ['Elapsed', decimal(snapshot.elapsedSeconds, ' s')], ['Active / target users', snapshot.activeUsers + ' / ' + run.configuration.users],
      ['SignalR connections', count(snapshot.sockets)], ['HTTP requests / s', decimal(snapshot.requestsPerSecond)],
      ['Successful requests', count(snapshot.successes)], ['Failed requests', count(snapshot.failures)],
      ['HTTP 429', count(snapshot.throttled)], ['Error rate', total ? decimal(100 * snapshot.failures / total, '%') : 'Unavailable'],
      ['Messages', count(snapshot.messages)], ['Posts', count(snapshot.posts)], ['Comments', count(snapshot.comments)],
      ['Uploads', count(snapshot.uploads)],
      ['Web CPU', decimal(snapshot.cpuPercent, '%')], ['Web memory', memory(snapshot.memoryBytes)],
      ['Peak Web CPU', decimal(snapshot.peakCpuPercent, '%')], ['Peak Web memory', memory(snapshot.peakMemoryBytes)],
      ['Network received', memory(snapshot.networkInBytes)], ['Network sent', memory(snapshot.networkOutBytes)],
      ['Disk read', memory(snapshot.diskReadBytes)], ['Disk written', memory(snapshot.diskWriteBytes)],
      ['Reconnects', count(snapshot.reconnects)], ['Sync failures', count(snapshot.syncFailures)],
      ['Realtime deliveries', count(snapshot.delivered)], ['Recovered', count(snapshot.recovered)], ['Missing', count(snapshot.missing)],
      ['Duplicate events', count(snapshot.duplicateEvents)], ['Duplicate messages', count(snapshot.duplicateMessages)]
    ] } />
    {!!snapshot.latencies.length && <div className="nx-monitor-table"><table>
      <caption>Successful operation latency (ms)</caption>
      <thead><tr><th>Operation</th><th>Samples</th><th>Mean</th><th>p50</th><th>p95</th><th>p99</th></tr></thead>
      <tbody>{snapshot.latencies.map(item => <tr key={item.operation}>
        <td>{label(item.operation)}</td><td>{count(item.count)}</td><td>{decimal(item.meanMs)}</td>
        <td>{decimal(item.p50Ms)}</td><td>{decimal(item.p95Ms)}</td><td>{decimal(item.p99Ms)}</td>
      </tr>)}</tbody>
    </table></div>}
  </>;
}

export function Comparison({ runs }: { runs: Run[] }) {
  const operationNames = Array.from(new Set(runs.flatMap(run => run.snapshot?.latencies.map(entry => entry.operation) ?? [])));
  const sameConfig = JSON.stringify(runs[0].configuration) === JSON.stringify(runs[1].configuration);
  const rate = (run: Run) => {
    const data = run.snapshot;
    const total = data ? data.steadyFailures + data.steadySuccesses : 0;
    return total > 0 && data ? decimal(data.steadyFailures * 100 / total, '%') : 'Unavailable';
  };
  const throughput = (run: Run) => {
    const data = run.snapshot;
    const seconds = data ? data.elapsedSeconds - run.configuration.rampMinutes * 60 : 0;
    return seconds > 0 && data ? decimal((data.steadySuccesses + data.steadyFailures) / seconds, ' RPS') : 'Unavailable';
  };
  return <Card title="Compare runs">
    {!sameConfig && <p className="nx-monitor-note">These runs used different workload settings.</p>}
    <div className="nx-monitor-table"><table><thead><tr><th>Metric</th>{runs.map(run => <th key={run.id}>{new Date(run.createdAt).toLocaleString()}</th>)}</tr></thead>
      <tbody>
        <tr><th>Build</th>{runs.map(run => <td key={run.id}>{run.snapshot?.commit?.slice(0, 8) ?? run.snapshot?.version ?? 'Unavailable'}</td>)}</tr>
        <tr><th>Profile</th>{runs.map(run => <td key={run.id}>{run.configuration.profile === 'sandbox-capacity' ? 'Capacity' : 'Policy fidelity'}</td>)}</tr>
        <tr><th>Steady throughput</th>{runs.map(run => <td key={run.id}>{throughput(run)}</td>)}</tr>
        <tr><th>Steady error rate</th>{runs.map(run => <td key={run.id}>{rate(run)}</td>)}</tr>
        <tr><th>Peak Web CPU</th>{runs.map(run => <td key={run.id}>{decimal(run.snapshot?.peakCpuPercent, '%')}</td>)}</tr>
        <tr><th>Peak Web memory</th>{runs.map(run => <td key={run.id}>{memory(run.snapshot?.peakMemoryBytes)}</td>)}</tr>
        <tr><th>Missing deliveries</th>{runs.map(run => <td key={run.id}>{count(run.snapshot?.missing)}</td>)}</tr>
        {operationNames.map(name => <tr key={name}><th>{label(name)} p95</th>
          {runs.map(run => <td key={run.id}>{decimal(run.snapshot?.latencies.find(item => item.operation === name)?.p95Ms, ' ms')}</td>)}</tr>)}
      </tbody></table></div>
  </Card>;
}

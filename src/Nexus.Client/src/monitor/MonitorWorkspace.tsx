import { KeyboardEvent, useEffect, useRef, useState } from 'react';
import { Activity, FlaskConical } from 'lucide-react';
import { Badge, Button, Card, Field } from '../ui';
import { protectedRequest } from '../scope';
import { Performance, PerformancePoint, Range, SandboxPoint } from './Performance';
import { Estimate, LoadControls } from './LoadControls';
import { Comparison, RunReport } from './RunReport';
import { Capabilities, Run, RunDetail, RunPage, defaultConfiguration, label } from './types';
import './monitor.css';

type Tab = 'performance' | 'load';
const root = '/api/monitor';

function usePoll<T>(path: string | null, onDenied: () => void, revision: number, delay = 5000) {
  const [response, setResponse] = useState<{ path: string; value: T } | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  useEffect(() => {
    if (!path) return;
    const url = path;
    const controller = new AbortController();
    let timer: number | undefined;
    async function refresh() {
      try {
        const result = await window.fetch(url, { signal: controller.signal });
        if (result.status === 401 || result.status === 403 || result.status === 404 && url === root + '/capabilities') {
          onDenied();
          return;
        }
        if (!result.ok) throw new Error('Monitor request failed');
        const data: T = await result.json();
        if (!controller.signal.aborted) {
          setResponse({ path: url, value: data });
          setFailed(null);
        }
      } catch {
        if (!controller.signal.aborted) setFailed(url);
      }
      if (!controller.signal.aborted) timer = window.setTimeout(refresh, delay);
    }
    void refresh();
    return () => { controller.abort(); window.clearTimeout(timer); };
  }, [path, onDenied, revision, delay]);
  return { value: response?.path === path ? response?.value ?? null : null, stale: Boolean(path && failed === path) };
}

function changeTab(event: KeyboardEvent<HTMLButtonElement>, setTab: (tab: Tab) => void) {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
  event.preventDefault();
  const tab = event.key === 'Home' ? 'performance' : event.key === 'End' ? 'load' :
    event.currentTarget.id === 'nx-monitor-tab-performance' ? 'load' : 'performance';
  setTab(tab);
  document.getElementById('nx-monitor-tab-' + tab)?.focus();
}

export default function MonitorWorkspace({ onDenied }: { onDenied: () => void }) {
  const [tab, setTab] = useState<Tab>('performance');
  const [range, setRange] = useState<Range>('5m');
  const [target, setTarget] = useState('deployment');
  const [runId, setRunId] = useState<string | null>(null);
  const [config, setConfig] = useState(defaultConfiguration);
  const [revision, setRevision] = useState(0);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [olderPage, setOlderPage] = useState<RunPage | null>(null);
  const [comparison, setComparison] = useState<string[]>([]);
  const pending = useRef<{ payload: string; id: string } | null>(null);
  const capabilities = usePoll<Capabilities>(root + '/capabilities', onDenied, revision, 15000);
  const history = usePoll<RunPage>(root + '/runs', onDenied, revision);
  const active = history.value?.items.find(item => item.active);
  const selected = runId ?? active?.id ?? history.value?.items[0]?.id;
  const detail = usePoll<RunDetail>(selected && (tab === 'load' || target === 'sandbox') ? root + '/runs/' + selected : null, onDenied, revision);
  const performance = usePoll<{ samples: PerformancePoint[] }>(
    tab === 'performance' && target === 'deployment' ? root + '/performance?range=' + range : null, onDenied, revision
  );
  const sandbox = usePoll<{ samples: SandboxPoint[] }>(
    tab === 'performance' && target === 'sandbox' && selected ? root + '/runs/' + selected + '/performance' : null, onDenied, revision
  );
  const items = Array.from(new Map([...history.value?.items ?? [], ...olderPage?.items ?? []].map(item => [item.id, item])).values());
  const comparisonRuns = comparison.map(id => items.find(item => item.id === id)).filter((item): item is Run => Boolean(item));
  const nextCursor = olderPage ? olderPage.nextCursor : history.value?.nextCursor;

  async function dispatch(stopId?: string) {
    setBusy(true);
    setError('');
    try {
      const payload = JSON.stringify(config);
      if (!stopId && pending.current?.payload !== payload) pending.current = { payload, id: crypto.randomUUID() };
      const response = stopId
        ? await protectedRequest(root + '/runs/' + stopId + '/stop', 'POST')
        : await protectedRequest(root + '/runs', 'POST', { requestId: pending.current?.id, configuration: config });
      if (!response.ok) {
        const status: { code?: string } = await response.json().catch(() => ({}));
        if (response.status < 500 && !stopId) pending.current = null;
        setError(response.status === 403 ? 'Your session does not have permission for this action.' :
          status.code === 'active_run_exists' ? 'A sandbox run is already active.' :
          status.code === 'runner_not_configured' ? 'The load runner is unavailable.' :
          'Nexus could not submit the request. Refresh the run status before retrying.');
        return;
      }
      if (!stopId) {
        const result: { id: string } = await response.json();
        pending.current = null;
        setRunId(result.id);
        setTarget('sandbox');
      }
      setRevision(current => current + 1);
    } catch {
      setError('The request could not reach Nexus.');
    } finally {
      setBusy(false);
    }
  }

  async function older(cursor: string) {
    try {
      const response = await window.fetch(root + '/runs?before=' + cursor);
      if (!response.ok) throw new Error('History unavailable');
      const received: RunPage = await response.json();
      setOlderPage(current => ({ items: [...current?.items ?? [], ...received.items], nextCursor: received.nextCursor }));
    } catch {
      setError('Older runs are temporarily unavailable.');
    }
  }

  return <section className="nx-monitor" aria-label="Monitor">
    <div className="nx-monitor-tabs" role="tablist" aria-label="Monitor views">
      <button id="nx-monitor-tab-performance" type="button" role="tab" aria-selected={tab === 'performance'}
        aria-controls="nx-monitor-performance" tabIndex={tab === 'performance' ? 0 : -1}
        onKeyDown={event => changeTab(event, setTab)} onClick={() => setTab('performance')}>
        <Activity size={16} aria-hidden="true" /> Performance
      </button>
      <button id="nx-monitor-tab-load" type="button" role="tab" aria-selected={tab === 'load'}
        aria-controls="nx-monitor-load" tabIndex={tab === 'load' ? 0 : -1}
        onKeyDown={event => changeTab(event, setTab)} onClick={() => setTab('load')}>
        <FlaskConical size={16} aria-hidden="true" /> Load Test
      </button>
    </div>

    {tab === 'performance' && <section id="nx-monitor-performance" role="tabpanel" aria-labelledby="nx-monitor-tab-performance">
      <div className="nx-monitor-toolbar">
        <Field label="Environment" htmlFor="nx-monitor-environment">
          <select id="nx-monitor-environment" value={target} onChange={event => setTarget(event.target.value)}>
            <option value="deployment">{capabilities.value?.environment ?? 'This deployment'}</option>
            <option value="sandbox" disabled={!selected}>Docker Sandbox</option>
          </select>
        </Field>
        <Field label="Time range" htmlFor="nx-monitor-range">
          <select id="nx-monitor-range" value={range} onChange={event => setRange(event.target.value as Range)}>
            <option value="5m">5 minutes</option><option value="15m">15 minutes</option><option value="1h">1 hour</option>
          </select>
        </Field>
        {target === 'sandbox' && detail.value && <Badge variant="role">{label(detail.value.run.status)}</Badge>}
        {(performance.stale || sandbox.stale) && <Badge variant="role">Connection interrupted</Badge>}
      </div>
      <Performance points={performance.value?.samples ?? []} sandbox={target === 'sandbox' ? sandbox.value?.samples ?? [] : null} range={range} />
    </section>}

    {tab === 'load' && <section id="nx-monitor-load" role="tabpanel" aria-labelledby="nx-monitor-tab-load">
      {active && selected !== active.id && <div className="nx-monitor-toolbar">
        <Badge variant="role">A sandbox run is active</Badge>
        <Button size="sm" onClick={() => setRunId(active.id)}>View active run</Button>
      </div>}
      <div className={'nx-monitor-load' + (active ? ' running' : '')}>
        {capabilities.value?.canRun && <LoadControls config={config} update={setConfig} capabilities={capabilities.value}
          busy={busy || !!active || detail.value?.run.active === true} start={() => void dispatch()} />}
        <div className="nx-monitor-column">
          {!active && capabilities.value?.canRun && <Estimate config={config} />}
          <RunReport detail={detail.value} stale={detail.stale} canStop={!!capabilities.value?.canRun && capabilities.value.mfaVerified}
            busy={busy} stop={id => void dispatch(id)} showPerformance={() => { setTarget('sandbox'); setTab('performance'); }} />
          {active && capabilities.value?.canRun && <Estimate config={config} />}
        </div>
      </div>
      {error && <p className="nx-monitor-error" role="alert">{error}</p>}
      <Card title="Test history">
        <div className="nx-monitor-table"><table><thead><tr>
          <th>Run</th><th>Users</th><th>Status</th><th>Build</th><th>Compare</th>
        </tr></thead><tbody>
          {items.map(item => <tr key={item.id}>
            <td><button type="button" className="nx-monitor-link" onClick={() => setRunId(item.id)}>{new Date(item.createdAt).toLocaleString()}</button></td>
            <td>{item.configuration.users}</td><td>{item.result ?? label(item.status)}</td>
            <td>{item.snapshot?.commit?.slice(0, 8) ?? item.snapshot?.version ?? 'Unavailable'}</td>
            <td><input type="checkbox" aria-label={'Compare run ' + new Date(item.createdAt).toLocaleString()}
              checked={comparison.includes(item.id)} disabled={item.active || comparison.length === 2 && !comparison.includes(item.id)}
              onChange={event => setComparison(current => event.target.checked ? [...current.slice(0, 1), item.id] : current.filter(id => id !== item.id))} /></td>
          </tr>)}
          {items.length === 0 && <tr><td colSpan={5}>{history.stale ? 'History unavailable' : 'No test runs recorded'}</td></tr>}
        </tbody></table></div>
        {nextCursor && <Button onClick={() => void older(nextCursor)} size="sm">Load older runs</Button>}
      </Card>
      {comparisonRuns.length === 2 && <Comparison runs={comparisonRuns} />}
    </section>}
  </section>;
}

import { Play } from 'lucide-react';
import { Button, Card, Field } from '../ui';
import { Metrics } from './Performance';
import { Capabilities, Configuration, count, decimal, workload } from './types';

const presets = [10, 30, 60, 100, 200, 300];
const settings = [
  ['users', 'Concurrent users', 1, 300], ['durationMinutes', 'Duration (minutes)', 1, 60],
  ['rampMinutes', 'Ramp-up (minutes)', 0, 10], ['messagesPerMinute', 'Messages / user / min', 0, 20],
  ['postsPerHour', 'Posts / user / hour', 0, 20], ['commentsPerHour', 'Comments / user / hour', 0, 60],
  ['readsPerMinute', 'Reads / user / min', 0, 30]
] as const;
const behaviors = [
  ['directMessages', 'Direct messages'], ['groupChat', 'Group Chat'], ['communityPosts', 'Community posts'],
  ['comments', 'Comments'], ['reactions', 'Reactions'], ['uploads', 'Synthetic uploads'],
  ['typing', 'Typing indicators'], ['signalR', 'SignalR connections'], ['reconnect', 'Disconnect and reconnect'],
  ['offlineSync', 'Offline synchronization']
] as const;

export function LoadControls({ config, update, capabilities, busy, start }: {
  config: Configuration;
  update: (config: Configuration) => void;
  capabilities: Capabilities;
  busy: boolean;
  start: () => void;
}) {
  const configError = config.rampMinutes >= config.durationMinutes ? 'Ramp-up must be shorter than the test duration.' :
    settings.some(([key, , min, max]) => !Number.isInteger(config[key]) || config[key] < min || config[key] > max) ?
      'Use whole numbers within the displayed ranges.' : '';

  function updateBoolean(key: typeof behaviors[number][0], checked: boolean) {
    const next = { ...config, [key]: checked };
    if (!next.signalR) next.typing = next.reconnect = next.offlineSync = false;
    if (!next.communityPosts) next.comments = next.reactions = false;
    if (!next.directMessages && !next.groupChat) next.uploads = next.offlineSync = false;
    update(next);
  }

  return <Card title="Configure a run">
    <div className="nx-monitor-presets" aria-label="User presets">
      {presets.map(users => <Button key={users} size="sm" aria-pressed={users === config.users}
        onClick={() => update({ ...config, users })}>{users} users</Button>)}
    </div>
    <Field label="Sandbox profile" htmlFor="nx-monitor-profile">
      <select id="nx-monitor-profile" value={config.profile} onChange={event => update({ ...config, profile: event.target.value })}>
        <option value="sandbox-capacity">Docker Sandbox · Capacity</option>
        <option value="sandbox-policy">Docker Sandbox · Policy fidelity</option>
      </select>
    </Field>
    <div className="nx-monitor-sliders">
      {settings.map(([key, name, min, max]) => <Field key={key} label={name} htmlFor={'nx-monitor-' + key}>
        <div className="nx-monitor-slider">
          <input type="range" aria-label={name + ' slider'} min={min} max={max} value={config[key]}
            onChange={event => update({ ...config, [key]: event.target.valueAsNumber })} />
          <input id={'nx-monitor-' + key} type="number" min={min} max={max} step={1}
            value={Number.isNaN(config[key]) ? '' : config[key]}
            onChange={event => update({ ...config, [key]: event.target.valueAsNumber })} />
        </div>
      </Field>)}
    </div>
    <fieldset className="nx-monitor-checks">
      <legend>Workload behaviors</legend>
      {behaviors.map(([key, name]) => <label key={key}><input type="checkbox" checked={config[key]}
        disabled={(['comments', 'reactions'].includes(key) && !config.communityPosts) ||
          (['typing', 'reconnect', 'offlineSync'].includes(key) && !config.signalR) ||
          (['uploads', 'offlineSync'].includes(key) && !config.directMessages && !config.groupChat)}
        onChange={event => updateBoolean(key, event.target.checked)} />{name}</label>)}
    </fieldset>
    <p className="nx-monitor-note">Web container limit 2 vCPU and 768 MiB. Sandbox resources are removed after the run.</p>
    {!capabilities.mfaVerified && <p className="nx-monitor-note">Sign in with verified MFA to run or stop tests.</p>}
    {!capabilities.runnerConfigured && <p className="nx-monitor-note">The load runner has not been configured.</p>}
    {configError && <p className="nx-monitor-error" role="status">{configError}</p>}
    <div className="nx-monitor-actions">
      <Button variant="primary" disabled={busy || !capabilities.mfaVerified || !capabilities.runnerConfigured || !!configError} onClick={start}>
        <Play size={16} aria-hidden="true" /> Run Load Test
      </Button>
    </div>
  </Card>;
}

export function Estimate({ config }: { config: Configuration }) {
  const estimate = workload(config);
  const valid = Object.values(config).every(value => typeof value !== 'number' || Number.isFinite(value));
  return <Card title="Estimated workload">
    <Metrics data={ [
      ['Messages', count(valid ? estimate.messages : null)], ['Posts', count(valid ? estimate.posts : null)],
      ['Comments', count(valid ? estimate.comments : null)], ['Reads', count(valid ? estimate.reads : null)],
      ['HTTP requests', count(valid ? estimate.requests : null)], ['Average workload RPS', decimal(valid ? estimate.requestsPerSecond : null)]
    ] } />
    <p className="nx-monitor-note">HTTP estimate includes sign-ins and linear ramp-up. Reconnects, retries and synchronization depend on the run.</p>
  </Card>;
}

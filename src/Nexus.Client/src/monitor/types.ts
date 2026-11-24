export interface Configuration {
  profile: string;
  users: number;
  durationMinutes: number;
  rampMinutes: number;
  messagesPerMinute: number;
  postsPerHour: number;
  commentsPerHour: number;
  readsPerMinute: number;
  directMessages: boolean;
  groupChat: boolean;
  communityPosts: boolean;
  comments: boolean;
  reactions: boolean;
  uploads: boolean;
  typing: boolean;
  signalR: boolean;
  reconnect: boolean;
  offlineSync: boolean;
}

export const defaultConfiguration: Configuration = {
  profile: 'sandbox-capacity', users: 60, durationMinutes: 15, rampMinutes: 3,
  messagesPerMinute: 4, postsPerHour: 2, commentsPerHour: 6, readsPerMinute: 8,
  directMessages: true, groupChat: true, communityPosts: true, comments: true, signalR: true,
  reactions: false, uploads: false, typing: false, reconnect: false, offlineSync: false
};

export interface Latency {
  operation: string;
  count: number;
  meanMs: number;
  p50Ms: number;
  p95Ms: number;
  p99Ms: number;
}

export interface Snapshot {
  reason?: string | null;
  elapsedSeconds: number;
  activeUsers: number;
  sockets: number;
  requestsPerSecond: number;
  successes: number;
  failures: number;
  throttled: number;
  messages: number;
  posts: number;
  comments: number;
  uploads?: number;
  reconnects: number;
  syncFailures: number;
  delivered: number;
  recovered: number;
  missing: number;
  duplicateEvents: number;
  duplicateMessages: number;
  cpuPercent: number | null;
  memoryBytes: number | null;
  memoryLimitBytes: number | null;
  peakCpuPercent: number | null;
  peakMemoryBytes: number | null;
  version: string | null;
  commit: string | null;
  imageId: string | null;
  steadySuccesses: number;
  steadyFailures: number;
  steadyThrottled: number;
  steadyWorkload: number;
  targetReachedSeconds: number;
  networkInBytes: number | null;
  networkOutBytes: number | null;
  diskReadBytes: number | null;
  diskWriteBytes: number | null;
  latencies: Latency[];
}

export interface Run {
  id: string;
  createdAt: string;
  heartbeatAt?: string | null;
  status: string;
  stopRequested: boolean;
  active: boolean;
  cleaned: boolean | null;
  configuration: Configuration;
  snapshot: Snapshot | null;
  result: string | null;
}

export interface RunDetail {
  run: Run;
  events: { occurredAt: string; action: string }[];
}
export interface RunPage { items: Run[]; nextCursor: string | null }
export interface Capabilities { canRun: boolean; mfaVerified: boolean; runnerConfigured: boolean; environment: string }

export function workload(config: Configuration) {
  const minutes = config.users * Math.max(0, config.durationMinutes - config.rampMinutes / 2);
  const messages = Math.round(config.directMessages || config.groupChat ? minutes * config.messagesPerMinute : 0);
  const posts = Math.round(config.communityPosts ? minutes * config.postsPerHour / 60 : 0);
  const comments = Math.round(config.comments ? minutes * config.commentsPerHour / 60 : 0);
  const reads = Math.round(minutes * config.readsPerMinute);
  const operations = messages + posts + comments + reads +
    (config.reactions ? Math.round(minutes * 2) : 0) + (config.uploads ? Math.round(minutes / 2) * 2 : 0);
  return { messages, posts, comments, reads, requests: operations + config.users,
    requestsPerSecond: operations / Math.max(1, config.durationMinutes * 60) };
}

export const label = (value: string) => value.replaceAll('_', ' ').replace(/^./, letter => letter.toUpperCase());
export const count = (value?: number | null) => value == null ? 'Unavailable' : value.toLocaleString();
export const decimal = (value?: number | null, unit = '') =>
  value == null ? 'Unavailable' : value.toLocaleString(undefined, { maximumFractionDigits: 1 }) + unit;
export const memory = (value?: number | null) => value == null ? 'Unavailable' : decimal(value / 1048576, ' MiB');

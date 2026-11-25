import { orgApiPath, personalApiPath, protectedRequest } from '../scope';
import type { DirectCall, DirectCallChange, DirectCallKind } from './callState';

type CallScope = Pick<DirectCallChange, 'organizationId'>;
export type CallTarget = Pick<DirectCallChange, 'id' | 'organizationId'>;
export type CallConversation = Pick<DirectCallChange, 'conversationId' | 'organizationId'>;
export type CallAction = 'accept' | 'decline' | 'cancel' | 'end';

export class CallRequestError extends Error {
  constructor(message: string, readonly status: number) { super(message); }
}

function scoped({ organizationId }: CallScope, path: string) {
  return organizationId ? orgApiPath(organizationId, path) : personalApiPath(path);
}

function callPath(call: CallTarget) {
  return scoped(call, `/calls/${call.id}`);
}

async function readCall(response: Response): Promise<DirectCall> {
  if (response.ok) return await response.json();
  let message = 'Unable to update the call. Try again.';
  if (response.status === 404 || response.status === 403) message = 'This call is unavailable or you no longer have access.';
  if (response.status === 409) message = 'The call changed on another device.';
  if (response.status === 429) message = 'Too many call attempts. Try again in a moment.';
  if (response.status === 503) message = 'Calls are temporarily unavailable.';
  throw new CallRequestError(message, response.status);
}

export async function startDirectCall(conversation: CallConversation, kind: DirectCallKind, clientCallId: string, signal: AbortSignal) {
  const path = scoped(conversation, `/conversations/${conversation.conversationId}/calls`);
  return readCall(await protectedRequest(path, 'POST', { kind, clientCallId }, signal));
}

export async function getDirectCall(call: CallTarget, signal: AbortSignal) {
  return readCall(await fetch(callPath(call), { signal, cache: 'no-store' }));
}

export async function updateDirectCall(call: CallTarget, action: CallAction, signal: AbortSignal) {
  return readCall(await protectedRequest(`${callPath(call)}/${action}`, 'POST', undefined, signal));
}

export async function endDirectCall(call: CallTarget, signal: AbortSignal) {
  try {
    return await updateDirectCall(call, 'end', signal);
  } catch (cause) {
    if (cause instanceof CallRequestError && [403, 404, 409].includes(cause.status)) return null;
    throw cause;
  }
}

export async function listActiveCalls(cursor: string | null, signal: AbortSignal): Promise<{
  items: DirectCall[]; nextCursor: string | null; available?: boolean
}> {
  const params = new URLSearchParams({ limit: '50' });
  if (cursor) params.set('cursor', cursor);
  const response = await fetch(`/api/calls/active?${params}`, { signal, cache: 'no-store' });
  if (!response.ok) throw new CallRequestError('Unable to refresh active calls.', response.status);
  return await response.json();
}

import { useCallback, useEffect, useState } from 'react';
import { Building2, MapPin, MessageSquare, ShieldAlert, ShieldCheck, Users } from 'lucide-react';
import {
  AvailabilityDto,
  LiveAvailabilityEntry,
  normalizeAvailability,
  resolveUserAvailability
} from './availability';
import { chatFetch, orgFetch } from './scope';
import { Avatar, Badge, Button, Modal } from './ui';

export interface ProfileCardTarget {
  userId: string;
  organizationId: string | null;
  fallbackDisplayName?: string;
  fallbackUsername?: string;
  fallbackAvatarUrl?: string | null;
}

export interface ProfileCardDto {
  scope: 'organization' | 'personal';
  organizationId?: string | null;
  userId: string;
  username: string;
  displayName: string;
  avatarUrl?: string | null;
  customStatus?: string | null;
  isOnline: boolean;
  availability: AvailabilityDto;
  trustLevel?: string | null;
  isUnverified?: boolean;
  orgRole?: string | null;
  isOwner?: boolean;
  departmentId?: string | null;
  departmentName?: string | null;
  branchId?: string | null;
  branchName?: string | null;
  isContact?: boolean;
  sharedWorkspaceNames?: string[];
  isBlockedByMe: boolean;
  canStartDirectChat: boolean;
}

export interface ProfileCardModalProps {
  target: ProfileCardTarget | null;
  currentUserId: string;
  availabilityMap: Record<string, LiveAvailabilityEntry>;
  nowMs: number;
  avatarCacheKey?: number;
  onClose: () => void;
  onStartDirectChat: (userId: string) => Promise<void>;
}

export function ProfileCardModal({
  target,
  currentUserId,
  availabilityMap,
  nowMs,
  avatarCacheKey,
  onClose,
  onStartDirectChat
}: ProfileCardModalProps) {
  const [card, setCard] = useState<ProfileCardDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadState, setLoadState] = useState<'idle' | 'ready' | 'unavailable' | 'error'>('idle');
  const [errorMessage, setErrorMessage] = useState('');
  const [startingChat, setStartingChat] = useState(false);
  const [chatError, setChatError] = useState('');
  const [retryNonce, setRetryNonce] = useState(0);

  const loadCard = useCallback(async (activeTarget: ProfileCardTarget, signal: AbortSignal) => {
    setLoading(true);
    setLoadState('idle');
    setErrorMessage('');
    setChatError('');
    setCard(null);

    try {
      const path = `/api/people/${encodeURIComponent(activeTarget.userId)}/profile-card`;
      const response = activeTarget.organizationId
        ? await orgFetch(activeTarget.organizationId, path, { credentials: 'same-origin', signal })
        : await chatFetch(path, { credentials: 'same-origin', signal });

      if (signal.aborted) return;

      if (response.status === 403 || response.status === 404) {
        setLoadState('unavailable');
        setErrorMessage('This profile is unavailable.');
        return;
      }

      if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        setLoadState('error');
        setErrorMessage(typeof body.message === 'string' && body.message ? body.message : 'Unable to load profile.');
        return;
      }

      const data = await response.json();
      if (signal.aborted) return;

      const parsed: ProfileCardDto = {
        scope: data.scope === 'organization' ? 'organization' : 'personal',
        organizationId: data.organizationId ?? null,
        userId: data.userId,
        username: data.username ?? '',
        displayName: data.displayName ?? activeTarget.fallbackDisplayName ?? 'User',
        avatarUrl: data.avatarUrl ?? null,
        customStatus: data.customStatus ?? null,
        isOnline: Boolean(data.isOnline),
        availability: normalizeAvailability(data.availability),
        trustLevel: data.trustLevel ?? null,
        isUnverified: Boolean(data.isUnverified),
        orgRole: data.orgRole ?? null,
        isOwner: Boolean(data.isOwner),
        departmentId: data.departmentId ?? null,
        departmentName: data.departmentName ?? null,
        branchId: data.branchId ?? null,
        branchName: data.branchName ?? null,
        isContact: Boolean(data.isContact),
        sharedWorkspaceNames: Array.isArray(data.sharedWorkspaceNames) ? data.sharedWorkspaceNames : [],
        isBlockedByMe: Boolean(data.isBlockedByMe),
        canStartDirectChat: Boolean(data.canStartDirectChat)
      };

      setCard(parsed);
      setLoadState('ready');
    } catch {
      if (!signal.aborted) {
        setLoadState('error');
        setErrorMessage('Unable to load profile. Check your connection and try again.');
      }
    } finally {
      if (!signal.aborted) {
        setLoading(false);
      }
    }
  }, []);

  useEffect(() => {
    if (!target) {
      setCard(null);
      setLoading(false);
      setLoadState('idle');
      setErrorMessage('');
      setChatError('');
      return;
    }
    const controller = new AbortController();
    void loadCard(target, controller.signal);
    return () => controller.abort();
  }, [loadCard, retryNonce, target]);

  const resolvedAvailability = card
    ? resolveUserAvailability(card.userId, { isOnline: card.isOnline, availability: card.availability }, availabilityMap, nowMs)
    : null;

  const isSelf = Boolean(card && card.userId === currentUserId);
  const canMessage = Boolean(card && !isSelf && card.canStartDirectChat && !card.isBlockedByMe);

  async function handleStartChat() {
    if (!card || startingChat) return;
    setChatError('');
    setStartingChat(true);
    try {
      await onStartDirectChat(card.userId);
      onClose();
    } catch (failure) {
      setChatError(failure instanceof Error ? failure.message : 'Unable to start chat.');
    } finally {
      setStartingChat(false);
    }
  }

  return (
    <Modal
      open={Boolean(target)}
      onOpenChange={open => {
        if (!open && !startingChat) onClose();
      }}
      title={card?.displayName ?? target?.fallbackDisplayName ?? 'Profile'}
      footer={
        <>
          {canMessage && (
            <Button variant="primary" disabled={startingChat} onClick={() => void handleStartChat()}>
              <MessageSquare size={14} aria-hidden="true" /> {startingChat ? 'Opening…' : 'Message'}
            </Button>
          )}
          <Button disabled={startingChat} onClick={onClose}>
            Close
          </Button>
        </>
      }
    >
      {loading && (
        <div className="nx-profile-card-state" role="status">
          Loading profile…
        </div>
      )}

      {!loading && loadState === 'unavailable' && (
        <div className="nx-profile-card-state" role="status">
          <p className="nx-muted" style={{ margin: 0 }}>{errorMessage || 'This profile is unavailable.'}</p>
        </div>
      )}

      {!loading && loadState === 'error' && (
        <div className="nx-profile-card-state">
          <p className="nx-error" role="alert" style={{ marginTop: 0 }}>{errorMessage}</p>
          <Button size="sm" onClick={() => setRetryNonce(n => n + 1)}>
            Retry
          </Button>
        </div>
      )}

      {!loading && loadState === 'ready' && card && resolvedAvailability && (
        <div className="nx-profile-card">
          <div className="nx-profile-card-hero">
            <Avatar
              displayName={card.displayName}
              avatarUrl={card.avatarUrl}
              cacheKey={avatarCacheKey}
              size="lg"
              presence={resolvedAvailability.dotTone}
            />
            <div className="nx-profile-card-identity">
              <div className="nx-profile-card-name-row">
                <strong className="nx-profile-card-name">{card.displayName}</strong>
                {isSelf && <Badge variant="role">You</Badge>}
                {card.scope === 'organization' && card.isOwner && <Badge variant="role">Owner</Badge>}
                {card.scope === 'organization' && card.orgRole && !card.isOwner && (
                  <Badge variant="role">{card.orgRole}</Badge>
                )}
              </div>
              {card.username && <div className="nx-muted nx-profile-card-handle">@{card.username}</div>}

              <div className="nx-avail-badge" role="status">
                <span className={`nx-dot ${resolvedAvailability.dotTone}`} aria-hidden="true" />
                <span>{resolvedAvailability.label}</span>
                {resolvedAvailability.expiryText && (
                  <span className="nx-muted">· {resolvedAvailability.expiryText}</span>
                )}
              </div>
            </div>
          </div>

          {card.customStatus && (
            <div className="nx-profile-card-quote">“{card.customStatus}”</div>
          )}

          {card.scope === 'organization' && (
            <dl className="nx-profile-card-meta">
              {card.departmentName && (
                <div className="nx-profile-card-meta-row">
                  <dt><Building2 size={14} aria-hidden="true" /> Department</dt>
                  <dd>{card.departmentName}</dd>
                </div>
              )}
              {card.branchName && (
                <div className="nx-profile-card-meta-row">
                  <dt><MapPin size={14} aria-hidden="true" /> Branch</dt>
                  <dd>{card.branchName}</dd>
                </div>
              )}
              <div className="nx-profile-card-meta-row">
                <dt>
                  {card.isUnverified ? <ShieldAlert size={14} aria-hidden="true" /> : <ShieldCheck size={14} aria-hidden="true" />} Verification
                </dt>
                <dd>
                  {card.isUnverified ? (
                    <Badge variant="role">Unverified</Badge>
                  ) : (
                    <Badge variant="success">Verified member</Badge>
                  )}
                </dd>
              </div>
            </dl>
          )}

          {card.scope === 'personal' &&
            (card.isContact || (card.sharedWorkspaceNames && card.sharedWorkspaceNames.length > 0)) && (
              <dl className="nx-profile-card-meta">
              {card.isContact && (
                <div className="nx-profile-card-meta-row">
                  <dt><Users size={14} aria-hidden="true" /> Relationship</dt>
                  <dd>Contact</dd>
                </div>
              )}
              {card.sharedWorkspaceNames && card.sharedWorkspaceNames.length > 0 && (
                <div className="nx-profile-card-meta-row">
                  <dt><Building2 size={14} aria-hidden="true" /> Shared workspaces</dt>
                  <dd>{card.sharedWorkspaceNames.join(', ')}</dd>
                </div>
              )}
            </dl>
          )}

          {card.isBlockedByMe && (
            <p className="nx-muted" role="status" style={{ margin: 0 }}>
              You blocked this user. Direct messaging is unavailable until you unblock them.
            </p>
          )}

          {chatError && (
            <p className="nx-error" role="alert" style={{ margin: 0 }}>
              {chatError}
            </p>
          )}
        </div>
      )}
    </Modal>
  );
}

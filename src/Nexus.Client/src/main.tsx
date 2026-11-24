import { chatFetch as fetch, initialOrganizationId, orgFetch, personalContactLink, protectedRequest, workspaceInvitation } from './scope';
import { ContactControls, listOrganizations, OrganizationItem, WorkspaceModals, WorkspaceModalState } from './organizationControls';
import { ChangeEvent, FormEvent, KeyboardEvent, Suspense, lazy, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { Activity, Bell, Briefcase, Command, LayoutGrid, LogOut, MessageSquare, Moon, Search, Shield, Sun, Tag, Users } from 'lucide-react';
import {
  ChatJumpTarget,
  clearContentLinkFromUrl,
  CommunityJumpTarget,
  encodeContentLink,
  readInitialContentLink,
  resolveContentLink
} from './contentLinks';
import type { CommunityFeedDelta } from './chat/types';
import type { DmReadReceiptPreference } from './chat/dmReadReceipts';
import type { OrganizationMessageTarget } from './OrganizationMessageContext';
import QuickSwitcher, { type QuickSwitchTarget } from './QuickSwitcher';
import { isQuickSwitcherShortcut } from './quickSwitcherKeys';
import {
  AvailabilityChangedEvent,
  AvailabilityDto,
  LiveAvailabilityEntry,
  getEarliestExpiryDelayMs,
  isChimeSuppressed,
  normalizeAvailability,
  resolveEffectiveAvailability,
  resolveUserAvailability
} from './availability';
import { AvailabilityPicker, AvailabilityUpdateResult } from './AvailabilityPicker';
import type { ProfileCardTarget } from './ProfileCardModal';
import { clearChatWork } from './offlineQueue';
import { loadOrganizationDepartments, personKey, searchChatCandidates, searchPeopleScope } from './peopleDirectory';
import { ListPaging, useCursorList } from './cursorList';
import { Avatar, Badge, Button, Card, Field, Modal, ToastBar } from './ui';
import './ui/tokens.css';
import './ui/components.css';
import './styles/base-layout.css';
import './styles/chat.css';
import './styles/communities.css';
import './styles/overlays.css';

const ActivityWorkspace = lazy(() =>
  import('./activity').then(m => ({ default: m.ActivityWorkspace }))
);
const CommunitiesWorkspace = lazy(() => import('./communities'));
const ChatWorkspace = lazy(() => import('./chat').then(module => ({ default: module.ChatWorkspace })));
const MessageSearchDialog = lazy(() => import('./MessageSearchDialog'));
const OrganizationMessageContext = lazy(() => import('./OrganizationMessageContext'));
const MonitorWorkspace = lazy(() => import('./monitor/MonitorWorkspace'));
const BlockedUsersSettings = lazy(() =>
  import('./blockedUsers').then(m => ({ default: m.BlockedUsersSettings }))
);
const PasswordResetForm = lazy(() =>
  import('./passwordReset').then(m => ({ default: m.PasswordResetForm }))
);
const ProfileCardModal = lazy(() =>
  import('./ProfileCardModal').then(m => ({ default: m.ProfileCardModal }))
);
const PeopleTagsModal = lazy(() => import('./tags/PeopleTagsModal'));

interface UserSummary {
  id: string;
  username: string;
  displayName: string;
  status: string;
  isUnverified: boolean;
  orgRole: string;
  mfaEnabled: boolean;
  mfaRequired: boolean;
  mfaSetupRequired: boolean;
  customStatus?: string | null;
  avatarUrl?: string | null;
  availability?: AvailabilityDto;
  suppressChime?: boolean;
  canViewMonitor?: boolean;
  dmReadReceipts?: DmReadReceiptPreference;
}

interface DirectoryProfile {
  id: string;
  username: string;
  displayName: string;
  departmentId?: string | null;
  departmentName?: string | null;
  branchName?: string | null;
  customStatus?: string | null;
  avatarUrl?: string | null;
  isOnline?: boolean;
  availability?: AvailabilityDto | null;
}

interface DepartmentItem {
  id: string;
  code: string;
  name: string;
}

interface SessionItem {
  id: string;
  userAgent: string;
  ipAddress: string;
  createdAt: string;
  lastActivityAt: string;
  isCurrent: boolean;
}

interface RegistrationPolicy {
  mode: string;
}

interface HrImportReport {
  dryRun: boolean;
  totalRows: number;
  addedCount: number;
  updatedCount: number;
  unchangedCount: number;
  deactivatedCount: number;
  errors: string[];
  matchSuggestions: Array<{
    userId: string;
    username: string;
    displayName: string;
    employeeCode: string;
    employeeFullName: string;
    confidence: string;
    reason: string;
  }>;
}

function NewChatDialog({ currentUserId, candidateRevision, onClose, onOpenChat, onOpenContacts }: {
  currentUserId: string;
  candidateRevision: number;
  onClose: () => void;
  onOpenChat: (userId: string) => Promise<void>;
  onOpenContacts: () => void;
}) {
  const [query, setQuery] = useState('');
  const [openError, setOpenError] = useState('');
  const [opening, setOpening] = useState(false);
  const searchInput = useRef<HTMLInputElement>(null);
  const candidates = useCursorList(JSON.stringify([currentUserId, candidateRevision, query]),
    (cursor, signal) => searchChatCandidates(query, cursor, signal), personKey, query.trim() ? 180 : 0);
  const people = candidates.items.filter(item => item.id !== currentUserId);

  async function choose(userId: string) {
    setOpenError('');
    setOpening(true);
    try { await onOpenChat(userId); onClose(); }
    catch (failure) { setOpenError((failure as Error).message); }
    finally { setOpening(false); }
  }

  return (
    <Modal
      open
      onOpenChange={open => { if (!open && !opening) onClose(); }}
      title="New chat"
      description="Message workspace members or accepted contacts in one unified chat list."
      initialFocusRef={searchInput}
    >
      <Field label="Search people" htmlFor="new-chat-search">
        <input
          ref={searchInput}
          id="new-chat-search"
          type="search"
          placeholder="Name or username"
          value={query}
          onChange={event => {
            setOpenError('');
            setQuery(event.currentTarget.value);
          }}
        />
      </Field>
      {openError && <p className="nx-error" role="alert">{openError}</p>}
      {candidates.loading && people.length === 0 && <p className="nx-muted" role="status">Searching…</p>}
      {people.length > 0 ? (
        <ul className="nx-new-chat-results" aria-label="People you can message">
          {people.map(person => {
            const tag = [
              ...(person.workspaceNames ?? []),
              ...(person.isContact ? ['Contact'] : [])
            ].join(', ');
            return (
              <li key={person.id}>
                <Button variant="ghost" fullWidth disabled={opening} onClick={() => void choose(person.id)}>
                  <Avatar displayName={person.displayName} avatarUrl={person.avatarUrl} />
                  <span>
                    <strong>{person.displayName}</strong>{' '}
                    <span className="nx-muted">@{person.username}{tag ? ` · ${tag}` : ''}</span>
                  </span>
                </Button>
              </li>
            );
          })}
        </ul>
      ) : !candidates.loading && !candidates.error && <p className="nx-muted" role="status">No matching people yet.</p>}
      <ListPaging loading={candidates.loading} error={candidates.error} hasMore={candidates.next !== null}
        onMore={candidates.loadMore} onRetry={candidates.retry} moreLabel="Load more people" />
      <Button onClick={onOpenContacts}>Add an external contact</Button>
    </Modal>
  );
}

function App() {
  const [apiVersion, setApiVersion] = useState<string | null>(null);
  const [versionStale, setVersionStale] = useState(false);
  const [policy, setPolicy] = useState<RegistrationPolicy | null>(null);
  const [user, setUser] = useState<UserSummary | null>(null);
  const [pendingDmReceipts, setPendingDmReceipts] = useState<boolean | null>(null);
  const [dmReceiptsError, setDmReceiptsError] = useState('');
  const dmReceiptRequest = useRef(false);
  const [organizations, setOrganizations] = useState<OrganizationItem[]>([]);
  const [messageSearchOpen, setMessageSearchOpen] = useState(false);
  const [quickSwitcherOpen, setQuickSwitcherOpen] = useState(false);
  const [searchRecalledIds, setSearchRecalledIds] = useState(new Set<string>());
  const [organizationMessage, setOrganizationMessage] = useState<OrganizationMessageTarget | null>(null);
  const [workspaceModal, setWorkspaceModal] = useState<WorkspaceModalState>(null);
  const inviteAutoOpenedRef = useRef(false);

  const [authLoading, setAuthLoading] = useState(true);
  const [authTab, setAuthTab] = useState<'login' | 'register'>('login');
  const [resetOpen, setResetOpen] = useState(false);
  const loginTabRef = useRef<HTMLButtonElement | null>(null);
  const registerTabRef = useRef<HTMLButtonElement | null>(null);
  const loginUsernameRef = useRef<HTMLInputElement | null>(null);

  const [workspaceTab, setWorkspaceTab] = useState<'activity' | 'chat' | 'communities' | 'directory' | 'security' | 'hr' | 'monitor'>(() => {
    if (personalContactLink) return 'directory';
    if (initialOrganizationId) return 'communities';
    const view = new URLSearchParams(window.location.search).get('view');
    return view === 'security' || view === 'activity' ? view : 'chat';
  });
  const hideMonitor = useCallback(() => {
    setUser(current => current ? { ...current, canViewMonitor: false } : current);
    setWorkspaceTab('chat');
  }, []);
  const [communitiesVisited, setCommunitiesVisited] = useState(() => workspaceTab === 'communities');
  const [newChatOpen, setNewChatOpen] = useState(false);
  const [activeConvId, setActiveConvId] = useState<string | null>(null);
  const [requestedChannel, setRequestedChannel] = useState<{ id: string } | null>(null);
  const [pendingLinkToken, setPendingLinkToken] = useState<string | null>(() => readInitialContentLink());
  const [chatJumpTarget, setChatJumpTarget] = useState<ChatJumpTarget | null>(null);
  const [communityJumpTarget, setCommunityJumpTarget] = useState<CommunityJumpTarget | null>(null);
  const jumpNonceRef = useRef(0);
  const linkRequestRef = useRef<AbortController | null>(null);
  const [focusBlockedUsers, setFocusBlockedUsers] = useState(false);
  const blockedUsersRef = useRef<HTMLDivElement | null>(null);

  const communityFeedListenerRef = useRef<((conversationId?: string, delta?: CommunityFeedDelta | null) => void) | null>(null);
  const refreshOrgSubscriptionsRef = useRef<(() => Promise<void>) | null>(null);
  const reloadConversationsRef = useRef<(() => Promise<void>) | null>(null);

  const [connState, setConnState] = useState<'connected' | 'reconnecting' | 'offline'>('reconnecting');
  const [availabilityMap, setAvailabilityMap] = useState<Record<string, LiveAvailabilityEntry>>({});
  const [nowMs, setNowMs] = useState(() => Date.now());
  const [profileCardTarget, setProfileCardTarget] = useState<ProfileCardTarget | null>(null);
  const [orgTagsModalOpen, setOrgTagsModalOpen] = useState(false);
  const currentUserIdRef = useRef<string | null>(null);
  currentUserIdRef.current = user?.id ?? null;
  const [unreadTotal, setUnreadTotal] = useState(0);
  const [unreadMentionCount, setUnreadMentionCount] = useState(0);
  const refreshMentionCountRef = useRef<(() => void) | null>(null);
  const [darkMode, setDarkMode] = useState(() => localStorage.getItem('nexus-theme') === 'dark');

  const [sessions, setSessions] = useState<SessionItem[]>([]);
  const [candidateRevision, setCandidateRevision] = useState(0);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [avatarBust, setAvatarBust] = useState(0);

  // Login & Register state
  const [loginUsername, setLoginUsername] = useState('');
  const [loginPassword, setLoginPassword] = useState('');
  const [loginTotp, setLoginTotp] = useState('');
  const [mfaChallenge, setMfaChallenge] = useState(false);

  const [regUsername, setRegUsername] = useState('');
  const [regDisplayName, setRegDisplayName] = useState('');
  const [regPassword, setRegPassword] = useState('');
  const [regJoinCode, setRegJoinCode] = useState('');
  const [regTerms, setRegTerms] = useState(false);
  const [regHoneypot, setRegHoneypot] = useState('');

  // Profile, Password & MFA state
  const [profileOrgId, setProfileOrgId] = useState('');
  const [customStatusInput, setCustomStatusInput] = useState('');
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [mfaSecret, setMfaSecret] = useState<string | null>(null);
  const [mfaCode, setMfaCode] = useState('');
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);

  // People (Contacts + Multi-Workspace Directory) state
  const [peopleFilter, setPeopleFilter] = useState<'contacts' | string>(() =>
    personalContactLink ? 'contacts' : (initialOrganizationId ?? 'contacts')
  );
  const peopleFilterPickedRef = useRef(Boolean(personalContactLink || initialOrganizationId));
  const [dirQuery, setDirQuery] = useState('');
  const [appliedDirectoryQuery, setAppliedDirectoryQuery] = useState('');
  const [dirDeptId, setDirDeptId] = useState('');
  const [departments, setDepartments] = useState<DepartmentItem[]>([]);
  const directoryPage = useCursorList<DirectoryProfile>(
    user && workspaceTab === 'directory' ? JSON.stringify([user.id, peopleFilter, appliedDirectoryQuery, dirDeptId]) : null,
    (cursor, signal) => searchPeopleScope<DirectoryProfile>(peopleFilter, appliedDirectoryQuery, dirDeptId, cursor, signal),
    personKey
  );
  const { items: directory, loading: loadingDirectory, refresh: refreshDirectory } = directoryPage;
  const contactUsernameRef = useRef<HTMLInputElement | null>(null);

  // HR CSV Import state
  const hrOrganizations = useMemo(
    () => organizations.filter(org => org.role === 'HRAdmin'),
    [organizations]
  );
  const [hrOrgId, setHrOrgId] = useState('');
  const [hrCsv, setHrCsv] = useState('employee_code,full_name,email,department_code,department_name,branch_code,branch_name,is_active\n');
  const [hrReport, setHrReport] = useState<HrImportReport | null>(null);

  useEffect(() => {
    document.body.classList.toggle('dark', darkMode);
    localStorage.setItem('nexus-theme', darkMode ? 'dark' : 'light');
  }, [darkMode]);

  useEffect(() => {
    setUnreadMentionCount(0);
    if (!user?.id) return;

    const controller = new AbortController();
    let pending = false;
    const refresh = async () => {
      if (pending || document.visibilityState !== 'visible') return;
      pending = true;
      try {
        const response = await fetch('/api/notifications/mentions/unread', { signal: controller.signal });
        if (!response.ok) return;
        const { unreadMentionCount: count } = await response.json();
        if (!controller.signal.aborted && typeof count === 'number') setUnreadMentionCount(count);
      } catch {
        // Keep the previous count during a temporary connection failure.
      } finally {
        pending = false;
      }
    };

    void refresh();
    refreshMentionCountRef.current = () => void refresh();
    const timer = window.setInterval(() => void refresh(), 15000);
    document.addEventListener('visibilitychange', refresh);
    return () => {
      controller.abort();
      refreshMentionCountRef.current = null;
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', refresh);
    };
  }, [user?.id]);

  useEffect(() => {
    if (!notice) return;
    const timer = window.setTimeout(() => setNotice(''), 4000);
    return () => window.clearTimeout(timer);
  }, [notice]);

  // Prevent accidental file drops outside the chat dropzone from navigating WebView2/browser away.
  useEffect(() => {
    const preventFileNav = (ev: DragEvent) => {
      if (ev.dataTransfer?.types && Array.from(ev.dataTransfer.types).includes('Files')) {
        ev.preventDefault();
      }
    };
    window.addEventListener('dragover', preventFileNav);
    window.addEventListener('drop', preventFileNav);
    return () => {
      window.removeEventListener('dragover', preventFileNav);
      window.removeEventListener('drop', preventFileNav);
    };
  }, []);

  const loadSessions = useCallback(async () => {
    const res = await fetch('/api/auth/sessions', { credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setSessions(data.items ?? []);
    }
  }, []);

  const refreshChatCandidates = useCallback(() => setCandidateRevision(revision => revision + 1), []);

  const refreshSafetyViews = useCallback(() => {
    refreshChatCandidates();
    void refreshDirectory();
    void reloadConversationsRef.current?.();
  }, [refreshChatCandidates, refreshDirectory]);

  const openContentLink = useCallback(async (token: string) => {
    linkRequestRef.current?.abort();
    const controller = new AbortController();
    linkRequestRef.current = controller;
    setError('');
    try {
      const resolved = await resolveContentLink(token, controller.signal);
      if (controller.signal.aborted) return;
      const nonce = ++jumpNonceRef.current;
      if (resolved.kind === 'message') {
        if (resolved.organizationId) {
          setOrganizationMessage({ ...resolved, organizationId: resolved.organizationId });
          return;
        }
        setActiveConvId(resolved.conversationId);
        setChatJumpTarget({
          conversationId: resolved.conversationId,
          messageId: resolved.messageId,
          seq: resolved.seq,
          parentMessageId: resolved.parentMessageId,
          parentSeq: resolved.parentSeq,
          nonce
        });
        setWorkspaceTab('chat');
      } else {
        setCommunitiesVisited(true);
        setCommunityJumpTarget({
          organizationId: resolved.organizationId,
          communityId: resolved.communityId,
          conversationId: resolved.conversationId,
          postId: resolved.kind === 'post' ? resolved.postId : null,
          nonce
        });
        setWorkspaceTab('communities');
      }
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'This link is unavailable or you do not have access to it.');
      }
    } finally {
      if (linkRequestRef.current === controller) linkRequestRef.current = null;
    }
  }, []);

  useEffect(() => {
    if (!user || !pendingLinkToken) return;
    const token = pendingLinkToken;
    setPendingLinkToken(null);
    clearContentLinkFromUrl();
    void openContentLink(token);
  }, [openContentLink, pendingLinkToken, user]);

  useEffect(() => {
    if (workspaceTab !== 'security' || !focusBlockedUsers) return;
    blockedUsersRef.current?.focus();
    setFocusBlockedUsers(false);
  }, [focusBlockedUsers, workspaceTab]);

  const loadDepartments = useCallback(async (orgId: string, signal?: AbortSignal) => {
    if (!orgId || orgId === 'contacts') {
      setDepartments([]);
      return;
    }
    try {
      const items = await loadOrganizationDepartments<DepartmentItem>(orgId, signal);
      if (!signal?.aborted) setDepartments(items);
    } catch {
      if (!signal?.aborted) setDepartments([]);
    }
  }, []);

  const syncOrgProfile = useCallback(async (orgId: string) => {
    if (!orgId) return;
    const res = await orgFetch(orgId, '/api/me', { credentials: 'same-origin' });
    if (!res.ok) return;
    const profile = await res.json();
    setCustomStatusInput(profile.customStatus ?? '');
    setUser(prev => prev ? { ...prev, customStatus: profile.customStatus ?? prev.customStatus, avatarUrl: profile.avatarUrl ?? prev.avatarUrl } : prev);
  }, []);

  const refreshMe = useCallback(async (): Promise<boolean> => {
    const res = await fetch('/api/auth/me', { credentials: 'same-origin' });
    if (!res.ok) {
      setUser(null);
      setOrganizations([]);
      setSessions([]);
      return false;
    }

    const account: UserSummary = await res.json();
    const orgs = await listOrganizations().catch(() => [] as OrganizationItem[]);
    setOrganizations(orgs);

    let me = account;
    const activeProfileOrg = orgs.find(o => o.id === initialOrganizationId) ?? orgs[0] ?? null;
    if (activeProfileOrg) {
      setProfileOrgId(prev => (prev && orgs.some(o => o.id === prev) ? prev : activeProfileOrg.id));
      const profileRes = await orgFetch(activeProfileOrg.id, '/api/me', { credentials: 'same-origin' });
      if (profileRes.ok) {
        me = { ...account, ...(await profileRes.json()) };
      }
    } else {
      setProfileOrgId('');
    }

    const normalizedAvailability = normalizeAvailability(me.availability ?? account.availability);
    const resolvedSuppressChime =
      typeof account.suppressChime === 'boolean'
        ? account.suppressChime
        : isChimeSuppressed(normalizedAvailability);
    const hydratedMe: UserSummary = {
      ...me,
      availability: normalizedAvailability,
      suppressChime: resolvedSuppressChime
    };

    setUser(current => current?.id === hydratedMe.id && (current.dmReadReceipts?.version ?? -1) > (hydratedMe.dmReadReceipts?.version ?? -1)
      ? { ...hydratedMe, dmReadReceipts: current.dmReadReceipts } : hydratedMe);
    setCustomStatusInput(hydratedMe.customStatus ?? '');

    if (!peopleFilterPickedRef.current && orgs.length > 0) {
      setPeopleFilter(orgs[0].id);
    } else if (peopleFilter !== 'contacts' && !orgs.some(o => o.id === peopleFilter)) {
      setPeopleFilter(orgs[0]?.id ?? 'contacts');
    }

    if (workspaceInvitation && !inviteAutoOpenedRef.current) {
      inviteAutoOpenedRef.current = true;
      setWorkspaceModal({ mode: 'join', initialToken: workspaceInvitation });
    }

    await loadSessions();
    return true;
  }, [loadSessions, peopleFilter]);

  useEffect(() => {
    const controller = new AbortController();
    async function bootstrap() {
      try {
        const verRes = await fetch('/api/version', { signal: controller.signal });
        if (verRes.ok) {
          const ver = await verRes.json();
          setApiVersion(ver.version ?? null);
        }

        const polRes = await fetch('/api/auth/registration-policy', { signal: controller.signal });
        if (polRes.ok) setPolicy(await polRes.json());

        await refreshMe();
      } catch {
        // Offline or aborted.
      } finally {
        if (!controller.signal.aborted) setAuthLoading(false);
      }
    }
    void bootstrap();
    return () => controller.abort();
  }, [refreshMe]);

  useEffect(() => {
    if (!user || workspaceTab !== 'directory') return;
    const controller = new AbortController();
    void loadDepartments(peopleFilter, controller.signal);
    return () => controller.abort();
  }, [user, workspaceTab, peopleFilter, loadDepartments]);

  useEffect(() => {
    if (hrOrganizations.length > 0 && !hrOrganizations.some(o => o.id === hrOrgId)) {
      setHrOrgId(hrOrganizations[0].id);
    }
  }, [hrOrganizations, hrOrgId]);

  const handleOrganizationsChanged = useCallback(async (focusOrgId?: string) => {
    const nextOrgs = await listOrganizations().catch(() => [] as OrganizationItem[]);
    setOrganizations(nextOrgs);
    if (focusOrgId) {
      setProfileOrgId(focusOrgId);
      setCommunitiesVisited(true);
      setWorkspaceTab('communities');
      setNotice('Workspace ready.');
    } else if (peopleFilter !== 'contacts' && !nextOrgs.some(o => o.id === peopleFilter)) {
      setPeopleFilter(nextOrgs[0]?.id ?? 'contacts');
      setDirDeptId('');
    }
    refreshChatCandidates();
    await refreshOrgSubscriptionsRef.current?.();
  }, [refreshChatCandidates, peopleFilter]);

  const handleOrganizationAccessRevoked = useCallback((revokedOrgId: string) => {
    setMessageSearchOpen(false);
    setQuickSwitcherOpen(false);
    setSearchRecalledIds(new Set());
    setOrganizationMessage(current => current?.organizationId === revokedOrgId ? null : current);
    setOrganizations(prev => {
      const next = prev.filter(o => o.id !== revokedOrgId);
      if (peopleFilter === revokedOrgId) {
        setPeopleFilter(next[0]?.id ?? 'contacts');
        setDirDeptId('');
      }
      return next;
    });
    refreshChatCandidates();
    setNotice('Your workspace memberships were updated.');
  }, [refreshChatCandidates, peopleFilter]);

  const accountId = user?.id;

  const applyDmReceiptPreference = useCallback((preference: DmReadReceiptPreference) => {
    setUser(current => current && preference.version >= (current.dmReadReceipts?.version ?? -1)
      ? { ...current, dmReadReceipts: preference } : current);
  }, []);

  useEffect(() => {
    if (!accountId || connState !== 'connected') return;
    const controller = new AbortController();
    void fetch('/api/auth/me', { signal: controller.signal })
      .then(res => res.ok ? res.json() as Promise<UserSummary> : null)
      .then(account => {
        if (!controller.signal.aborted && account?.id === accountId && account.dmReadReceipts) {
          applyDmReceiptPreference(account.dmReadReceipts);
        }
      }).catch(() => {});
    return () => controller.abort();
  }, [accountId, applyDmReceiptPreference, connState]);

  async function updateDmReceiptPreference(enabled: boolean) {
    if (dmReceiptRequest.current) return;
    dmReceiptRequest.current = true;
    setPendingDmReceipts(enabled);
    setDmReceiptsError('');
    try {
      const res = await protectedRequest('/api/auth/me/dm-read-receipts', 'PUT', { enabled });
      if (!res.ok) throw new Error();
      applyDmReceiptPreference(await res.json() as DmReadReceiptPreference);
    } catch {
      setDmReceiptsError('Unable to update read receipts. Try again.');
    } finally {
      dmReceiptRequest.current = false;
      setPendingDmReceipts(null);
    }
  }
  useEffect(() => {
    if (!accountId) {
      setMessageSearchOpen(false);
      setQuickSwitcherOpen(false);
      setSearchRecalledIds(new Set());
      setOrganizationMessage(null);
    }
  }, [accountId]);

  useEffect(() => {
    if (!accountId) return;
    let composing = false;
    const startComposition = () => { composing = true; };
    const endComposition = () => { composing = false; };
    const openSwitcher = (event: globalThis.KeyboardEvent) => {
      if (!isQuickSwitcherShortcut(event, composing) || document.querySelector('[role="dialog"][data-state="open"]')) return;
      event.preventDefault();
      setQuickSwitcherOpen(true);
    };
    window.addEventListener('compositionstart', startComposition, true);
    window.addEventListener('compositionend', endComposition, true);
    window.addEventListener('blur', endComposition, true);
    window.addEventListener('keydown', openSwitcher);
    return () => {
      window.removeEventListener('compositionstart', startComposition, true);
      window.removeEventListener('compositionend', endComposition, true);
      window.removeEventListener('blur', endComposition, true);
      window.removeEventListener('keydown', openSwitcher);
    };
  }, [accountId]);
  const signOutChannelRef = useRef<BroadcastChannel | null>(null);
  useEffect(() => {
    if (!accountId || typeof BroadcastChannel === 'undefined') return;
    const channel = new BroadcastChannel(`nexus-sign-out:${accountId}`);
    signOutChannelRef.current = channel;
    channel.onmessage = () => {
      void clearChatWork(`${accountId}:personal`).catch(() => {}).finally(() => window.location.replace('/'));
    };
    return () => {
      signOutChannelRef.current = null;
      channel.close();
    };
  }, [accountId]);

  async function handleLogin(e: FormEvent) {
    e.preventDefault();
    setError('');
    setNotice('');
    const res = await fetch('/api/auth/login', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        username: loginUsername,
        password: loginPassword,
        totpCode: mfaChallenge ? loginTotp : null
      })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      if (data.requiresMfa) {
        setMfaChallenge(true);
        setError('Enter your 6-digit authenticator code to complete sign in.');
      } else {
        setError(data.message || 'Invalid username or password.');
      }
      return;
    }
    setMfaChallenge(false);
    setLoginPassword('');
    setLoginTotp('');
    if (!await refreshMe()) {
      setError('Sign-in succeeded, but your browser did not retain the session cookie.');
    }
  }

  async function handleRegister(e: FormEvent) {
    e.preventDefault();
    setError('');
    setNotice('');
    const res = await fetch('/api/auth/register', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        username: regUsername,
        displayName: regDisplayName,
        password: regPassword,
        termsAccepted: regTerms,
        joinCode: regJoinCode || null,
        honeypot: regHoneypot || null
      })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Registration failed.');
      return;
    }
    setRegPassword('');
    if (!await refreshMe()) {
      setError('Account created, but your browser did not retain the session cookie.');
    }
  }

  async function handleLogout() {
    const res = await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
    if (res.ok || res.status === 401) {
      signOutChannelRef.current?.postMessage('signed-out');
      if (user) await clearChatWork(`${user.id}:personal`).catch(() => {});
      window.location.replace('/');
    } else setError('Unable to sign out. Try again.');
  }

  async function openDm(targetUserId: string) {
    const res = await fetch('/api/conversations/dm', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ targetUserId })
    });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      if (res.status === 404) throw new Error('Connect as contacts or share a workspace before starting a new chat.');
      throw new Error(data.message || 'Unable to open chat.');
    }
    const dm = await res.json();
    setActiveConvId(dm.id);
    setWorkspaceTab('chat');
    void reloadConversationsRef.current?.();
  }

  async function handleOpenDm(targetUserId: string) {
    setError('');
    try { await openDm(targetUserId); } catch (failure) { setError((failure as Error).message); }
  }

  async function chooseQuickSwitch(target: QuickSwitchTarget) {
    if (target.kind === 'conversation') {
      setActiveConvId(target.id);
      setWorkspaceTab('chat');
    } else if (target.kind === 'person') {
      await openDm(target.id);
    } else if (target.kind === 'channel') {
      await openContentLink(encodeContentLink({ kind: 'channel', organizationId: target.organizationId, conversationId: target.id }));
    } else if (target.kind === 'action') {
      switch (target.action) {
        case 'search': setMessageSearchOpen(true); break;
        case 'new-chat': setNewChatOpen(true); break;
        case 'communities':
          setCommunitiesVisited(true);
          setWorkspaceTab('communities');
          break;
        case 'people': setWorkspaceTab('directory'); break;
        default: setWorkspaceTab(target.action);
      }
    }
  }

  async function handleUpdateCustomStatus(e: FormEvent) {
    e.preventDefault();
    const targetOrg = profileOrgId || organizations[0]?.id;
    if (!targetOrg) return;
    setError('');
    setNotice('');
    const res = await orgFetch(targetOrg, '/api/people/me/status', {
      method: 'PUT',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ customStatus: customStatusInput })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Unable to update custom status.');
      return;
    }
    setNotice('Custom status updated.');
    await syncOrgProfile(targetOrg);
  }

  async function handleAvatarChange(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    const targetOrg = profileOrgId || organizations[0]?.id;
    if (!file || !targetOrg) return;
    setError('');
    setNotice('');
    const res = await orgFetch(targetOrg, '/api/people/me/avatar', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': file.type || 'application/octet-stream' },
      body: file
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Unable to upload avatar.');
      return;
    }
    setAvatarBust(v => v + 1);
    setNotice('Avatar updated.');
    await syncOrgProfile(targetOrg);
  }

  async function handleChangePassword(e: FormEvent) {
    e.preventDefault();
    setError('');
    setNotice('');
    const res = await fetch('/api/auth/change-password', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ currentPassword, newPassword })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Unable to change password.');
      return;
    }
    setCurrentPassword('');
    setNewPassword('');
    setNotice('Password updated. Other devices have been signed out.');
    await loadSessions();
  }

  async function handleRevokeSession(sessionId: string) {
    const res = await fetch(`/api/auth/sessions/${encodeURIComponent(sessionId)}`, {
      method: 'DELETE',
      credentials: 'same-origin'
    });
    if (res.ok) await loadSessions();
  }

  async function handleStartMfa() {
    setError('');
    const res = await fetch('/api/auth/mfa/setup', { method: 'POST', credentials: 'same-origin' });
    if (res.ok) {
      const data = await res.json();
      setMfaSecret(data.sharedKey);
    }
  }

  async function handleEnableMfa(e: FormEvent) {
    e.preventDefault();
    setError('');
    const res = await fetch('/api/auth/mfa/enable', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ code: mfaCode })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || 'Invalid authenticator code.');
      return;
    }
    setMfaCode('');
    setMfaSecret(null);
    setRecoveryCodes(data.recoveryCodes ?? []);
    setNotice('Multi-factor authentication enabled.');
    await refreshMe();
  }

  async function handleHrImport(dryRun: boolean) {
    const targetOrg = hrOrgId || hrOrganizations[0]?.id;
    if (!targetOrg) return;
    setError('');
    setNotice('');
    const res = await orgFetch(targetOrg, `/api/org/employees/import?dryRun=${dryRun ? 'true' : 'false'}`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ csvContent: hrCsv })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      setError(data.message || (data.errors ? data.errors.join(' ') : 'Import failed.'));
      return;
    }
    setHrReport(data);
    setNotice(dryRun ? 'Dry-run diff generated.' : 'HR directory import applied.');
    if (!dryRun && peopleFilter === targetOrg) await loadDepartments(targetOrg);
  }

  function openContactsFromChat() {
    setNewChatOpen(false);
    peopleFilterPickedRef.current = true;
    setPeopleFilter('contacts');
    setDirDeptId('');
    setWorkspaceTab('directory');
    requestAnimationFrame(() => contactUsernameRef.current?.focus());
  }

  const handleVersionStale = useCallback(() => {
    setVersionStale(true);
  }, []);

  // Single O(1) local timeout across current user + live availability map (zero polling)
  useEffect(() => {
    const tracked: Array<AvailabilityDto | undefined> = [
      user?.availability,
      ...Object.values(availabilityMap).map(entry => entry.availability)
    ];
    const delay = getEarliestExpiryDelayMs(tracked, nowMs);
    if (delay === null) return;
    const timer = window.setTimeout(() => {
      setNowMs(Date.now());
    }, delay);
    return () => window.clearTimeout(timer);
  }, [availabilityMap, nowMs, user?.availability]);

  const handleAvailabilityUpdated = useCallback((next: AvailabilityUpdateResult) => {
    const normalized = normalizeAvailability(next.availability);
    const now = Date.now();
    setNowMs(now);
    setUser(prev =>
      prev
        ? { ...prev, availability: normalized, suppressChime: next.suppressChime }
        : prev
    );
    const uid = currentUserIdRef.current;
    if (uid) {
      setAvailabilityMap(prev => ({
        ...prev,
        [uid]: { isOnline: next.isOnline, availability: normalized, updatedAt: new Date(now).toISOString() }
      }));
    }
  }, []);

  const handleAvailabilityChanged = useCallback((evt: AvailabilityChangedEvent) => {
    const normalized = normalizeAvailability(evt.availability);
    const now = Date.now();
    setNowMs(now);
    setAvailabilityMap(prev => ({
      ...prev,
      [evt.userId]: {
        isOnline: evt.isOnline,
        availability: normalized,
        updatedAt: evt.updatedAt
      }
    }));
    if (evt.userId === currentUserIdRef.current) {
      setUser(prev =>
        prev
          ? {
              ...prev,
              availability: normalized,
              suppressChime: isChimeSuppressed(normalized, now)
            }
          : prev
      );
    }
  }, []);

  if (authLoading) {
    return (
      <div className="nx-empty-stage" style={{ minHeight: '100dvh' }}>
        <div className="nx-brand-mark">N</div>
        <p style={{ marginTop: 14 }}>Connecting to Nexus…</p>
      </div>
    );
  }

  if (!user) {
    const returnToSignIn = (passwordReset = false) => {
      setResetOpen(false);
      setAuthTab('login');
      setNotice(passwordReset ? 'Password reset. Sign in with your new password.' : '');
      requestAnimationFrame(() => loginUsernameRef.current?.focus());
    };

    const switchAuthTab = (next: 'login' | 'register', moveFocus = false) => {
      setAuthTab(next);
      setError('');
      if (moveFocus) {
        (next === 'login' ? loginTabRef : registerTabRef).current?.focus();
      }
    };

    const handleAuthTabKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
      if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
        e.preventDefault();
        switchAuthTab(authTab === 'login' ? 'register' : 'login', true);
      } else if (e.key === 'Home') {
        e.preventDefault();
        switchAuthTab('login', true);
      } else if (e.key === 'End') {
        e.preventDefault();
        switchAuthTab('register', true);
      }
    };

    return (
      <main className="nx-auth-page">
        <section className="nx-auth-hero">
          <div className="nx-auth-brand">
            <span className="nx-brand-mark">N</span>
            <strong className="nx-auth-brand-name">Nexus</strong>
          </div>

          <div>
            <h1>Workplace communication, simplified.</h1>
            <p>
              Message people across workspaces and contacts, and organize shared and private communities in one window.
            </p>
            <ul className="nx-auth-features">
              <li>● Instant messaging with offline queue &amp; reliable sync</li>
              <li>● Multi-workspace communities protected by strict membership controls</li>
              <li>● Vietnamese-aware directory search &amp; TOTP account security</li>
            </ul>
          </div>
        </section>

        <section className="nx-auth-panel">
          <div className="nx-auth-card">
            {workspaceInvitation && <p className="nx-muted">Sign in or create an account to accept your workspace invitation.</p>}
            {resetOpen ? (
              <Suspense fallback={<div className="nx-empty-side" role="status">Loading…</div>}>
                <PasswordResetForm onBack={() => returnToSignIn()} onReset={() => returnToSignIn(true)} />
              </Suspense>
            ) : (
            <>
            <div
              className="nx-auth-switcher"
              role="tablist"
              aria-label="Authentication mode"
              onKeyDown={handleAuthTabKeyDown}
            >
              <button
                ref={loginTabRef}
                id="auth-tab-login"
                type="button"
                role="tab"
                aria-selected={authTab === 'login'}
                aria-controls="auth-panel-login"
                tabIndex={authTab === 'login' ? 0 : -1}
                className={authTab === 'login' ? 'active' : ''}
                onClick={() => switchAuthTab('login')}
              >
                Sign in
              </button>
              <button
                ref={registerTabRef}
                id="auth-tab-register"
                type="button"
                role="tab"
                aria-selected={authTab === 'register'}
                aria-controls="auth-panel-register"
                tabIndex={authTab === 'register' ? 0 : -1}
                className={authTab === 'register' ? 'active' : ''}
                onClick={() => switchAuthTab('register')}
              >
                Create account
              </button>
            </div>

            {error && <p className="nx-error" role="alert">{error}</p>}
            {notice && <p className="nx-ok" role="status">{notice}</p>}

            <div
              role="tabpanel"
              id={authTab === 'login' ? 'auth-panel-login' : 'auth-panel-register'}
              aria-labelledby={authTab === 'login' ? 'auth-tab-login' : 'auth-tab-register'}
            >
              {authTab === 'login' ? (
                <form onSubmit={handleLogin}>
                  <Field label="Username" htmlFor="login-username">
                    <input ref={loginUsernameRef} id="login-username" type="text" autoComplete="username" required value={loginUsername} onChange={e => setLoginUsername(e.target.value)} />
                  </Field>
                  <Field label="Password" htmlFor="login-password">
                    <input id="login-password" type="password" autoComplete="current-password" required value={loginPassword} onChange={e => setLoginPassword(e.target.value)} />
                  </Field>
                  {mfaChallenge && (
                    <Field label="Authenticator code" htmlFor="login-totp">
                      <input id="login-totp" type="text" inputMode="numeric" required value={loginTotp} onChange={e => setLoginTotp(e.target.value)} />
                    </Field>
                  )}
                  <Button type="submit" variant="primary" fullWidth>
                    Sign in to Nexus
                  </Button>
                  <Button className="nx-auth-secondary" variant="ghost" fullWidth onClick={() => {
                    setResetOpen(true);
                    setLoginPassword('');
                    setLoginTotp('');
                    setMfaChallenge(false);
                    setError('');
                    setNotice('');
                  }}>Reset password</Button>
                </form>
              ) : (
                <form onSubmit={handleRegister}>
                  <div className="nx-hp" aria-hidden="true">
                    <label htmlFor="reg-website">Website</label>
                    <input id="reg-website" tabIndex={-1} autoComplete="off" value={regHoneypot} onChange={e => setRegHoneypot(e.target.value)} />
                  </div>
                  <Field label="Username (3–32 characters)" htmlFor="reg-username">
                    <input id="reg-username" type="text" autoComplete="username" required minLength={3} maxLength={32} value={regUsername} onChange={e => setRegUsername(e.target.value)} />
                  </Field>
                  <Field label="Display name" htmlFor="reg-display-name">
                    <input id="reg-display-name" type="text" autoComplete="name" required maxLength={80} value={regDisplayName} onChange={e => setRegDisplayName(e.target.value)} />
                  </Field>
                  <Field label="Password (at least 12 characters)" htmlFor="reg-password">
                    <input id="reg-password" type="password" autoComplete="new-password" required minLength={12} value={regPassword} onChange={e => setRegPassword(e.target.value)} />
                  </Field>
                  {policy?.mode === 'OpenWithJoinCode' && (
                    <Field label="Join code" htmlFor="reg-join-code">
                      <input id="reg-join-code" type="text" required value={regJoinCode} onChange={e => setRegJoinCode(e.target.value)} />
                    </Field>
                  )}
                  <div className="nx-field">
                    <label className="nx-terms-label">
                      <input type="checkbox" checked={regTerms} onChange={e => setRegTerms(e.target.checked)} required />
                      <span>I accept the terms of use.</span>
                    </label>
                  </div>
                  <Button type="submit" variant="primary" fullWidth>
                    Create Nexus account
                  </Button>
                </form>
              )}
            </div>
            </>
            )}
          </div>
        </section>
      </main>
    );
  }

  const activeProfileOrg = organizations.find(o => o.id === profileOrgId) ?? organizations[0] ?? null;
  const activePeopleOrg = peopleFilter === 'contacts' ? null : (organizations.find(o => o.id === peopleFilter) ?? null);
  const myEffectiveAvailability = resolveEffectiveAvailability(user.availability, nowMs);
  const myIsOnline = connState === 'connected';
  const mySuppressChime = isChimeSuppressed(myEffectiveAvailability, nowMs);

  const pageTitle =
    workspaceTab === 'activity' ? 'Activity' :
    workspaceTab === 'chat' ? 'Chat' :
    workspaceTab === 'communities' ? 'Communities' :
    workspaceTab === 'directory' ? 'People' :
    workspaceTab === 'security' ? 'Profile & security' :
    workspaceTab === 'monitor' ? 'Monitor' :
    'HR import';

  return (
    <div className="nx-app" data-suppress-chime={mySuppressChime ? 'true' : 'false'}>
      {/* Left App Rail */}
      <nav className="nx-rail" aria-label="Main navigation">
        <div className="nx-rail-logo" title="Nexus">
          <span className="nx-brand-mark sm">N</span>
        </div>

        <div className="nx-rail-nav">
          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'activity' ? 'active' : ''}`}
            aria-current={workspaceTab === 'activity' ? 'page' : undefined}
            onClick={() => setWorkspaceTab('activity')}
          >
            <Bell size={20} aria-hidden="true" />
            <span>Activity</span>
          </button>
          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'chat' ? 'active' : ''}`}
            aria-current={workspaceTab === 'chat' ? 'page' : undefined}
            onClick={() => setWorkspaceTab('chat')}
          >
            <MessageSquare size={20} aria-hidden="true" />
            <span>Chat</span>
            {unreadTotal > 0 && (
              <span className="nx-rail-badge" aria-label={`${unreadTotal} unread messages`}>
                {unreadTotal > 99 ? '99+' : unreadTotal}
              </span>
            )}
          </button>

          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'communities' ? 'active' : ''}`}
            aria-current={workspaceTab === 'communities' ? 'page' : undefined}
            onClick={() => {
              setCommunitiesVisited(true);
              setWorkspaceTab('communities');
            }}
          >
            <LayoutGrid size={20} aria-hidden="true" />
            <span>Communities</span>
          </button>

          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'directory' ? 'active' : ''}`}
            aria-current={workspaceTab === 'directory' ? 'page' : undefined}
            onClick={() => setWorkspaceTab('directory')}
          >
            <Users size={20} aria-hidden="true" />
            <span>People</span>
          </button>

          <button
            type="button"
            className={`nx-rail-btn ${workspaceTab === 'security' ? 'active' : ''}`}
            aria-current={workspaceTab === 'security' ? 'page' : undefined}
            onClick={() => setWorkspaceTab('security')}
          >
            <Shield size={20} aria-hidden="true" />
            <span>Profile &amp; security</span>
          </button>

          {user.canViewMonitor && (
            <button type="button" className={`nx-rail-btn ${workspaceTab === 'monitor' ? 'active' : ''}`}
              aria-current={workspaceTab === 'monitor' ? 'page' : undefined} onClick={() => setWorkspaceTab('monitor')}>
              <Activity size={20} aria-hidden="true" />
              <span>Monitor</span>
            </button>
          )}

          {hrOrganizations.length > 0 && (
            <button
              type="button"
              className={`nx-rail-btn ${workspaceTab === 'hr' ? 'active' : ''}`}
              aria-current={workspaceTab === 'hr' ? 'page' : undefined}
              onClick={() => setWorkspaceTab('hr')}
            >
              <Briefcase size={20} aria-hidden="true" />
              <span>HR import</span>
            </button>
          )}
        </div>

        <div className="nx-rail-footer">
          <button
            type="button"
            className="nx-rail-btn compact"
            title={darkMode ? 'Switch to light theme' : 'Switch to dark theme'}
            aria-label={darkMode ? 'Switch to light theme' : 'Switch to dark theme'}
            onClick={() => setDarkMode(v => !v)}
          >
            {darkMode ? <Sun size={18} aria-hidden="true" /> : <Moon size={18} aria-hidden="true" />}
          </button>

          <AvailabilityPicker
            variant="popover"
            popoverPlacement="rail"
            availability={myEffectiveAvailability}
            isOnline={myIsOnline}
            nowMs={nowMs}
            onUpdated={handleAvailabilityUpdated}
            displayName={user.displayName}
            username={user.username}
            avatarUrl={user.avatarUrl}
            avatarCacheKey={avatarBust}
            onOpenProfileSettings={() => setWorkspaceTab('security')}
          />

          <button
            type="button"
            className="nx-rail-btn compact"
            title="Sign out"
            aria-label="Sign out"
            onClick={handleLogout}
          >
            <LogOut size={18} aria-hidden="true" />
          </button>
        </div>
      </nav>

      {/* Main Workspace */}
      <div className="nx-workspace">
        <header className="nx-topbar">
          <div className="nx-topbar-left">
            <h1 className="nx-topbar-title">{pageTitle}</h1>
          </div>

          <div className="nx-topbar-right">
            <Button variant="ghost" size="sm" iconOnly aria-label="Quick switcher" title="Quick switcher (Ctrl+K or Cmd+K)"
              aria-keyshortcuts="Control+k Meta+k" onClick={() => setQuickSwitcherOpen(true)}>
              <Command size={16} aria-hidden="true" />
            </Button>
            <Button variant="ghost" size="sm" iconOnly aria-label="Search messages" title="Search messages"
              onClick={() => setMessageSearchOpen(true)}>
              <Search size={16} aria-hidden="true" />
            </Button>
            {unreadMentionCount > 0 && (
              <span role="status">
                <Badge variant="role" ariaLabel={`${unreadMentionCount} unread mentions`}>
                  @{unreadMentionCount > 99 ? '99+' : unreadMentionCount}
                </Badge>
              </span>
            )}
            {versionStale && (
              <Button variant="primary" size="sm" onClick={() => window.location.reload()}>
                Update available · Reload
              </Button>
            )}
            <span className="nx-conn-pill" role="status" title={apiVersion ? `API v${apiVersion}` : undefined}>
              <span className={`nx-dot ${connState}`} />
              <span>{connState === 'connected' ? 'Connected' : connState === 'reconnecting' ? 'Reconnecting…' : 'Offline'}</span>
            </span>
            <AvailabilityPicker
              variant="popover"
              popoverPlacement="topbar"
              availability={myEffectiveAvailability}
              isOnline={myIsOnline}
              nowMs={nowMs}
              onUpdated={handleAvailabilityUpdated}
              displayName={user.displayName}
              username={user.username}
              onOpenProfileSettings={() => setWorkspaceTab('security')}
            />
            <strong className="nx-user-name-wrap nx-topbar-user-name">{user.displayName}</strong>
          </div>
        </header>

        {error && <ToastBar variant="error" message={error} onDismiss={() => setError('')} />}
        {notice && <ToastBar variant="ok" message={notice} onDismiss={() => setNotice('')} />}

        <main className="nx-main-area">
          {workspaceTab === 'monitor' && user.canViewMonitor && (
            <Suspense fallback={<div role="status">Loading Monitor…</div>}>
              <MonitorWorkspace onDenied={hideMonitor} />
            </Suspense>
          )}
          {workspaceTab === 'activity' && (
            <Suspense fallback={<div className="nx-empty-side" role="status">Loading Activity…</div>}>
              <ActivityWorkspace
                onOpenChat={id => {
                  setActiveConvId(id);
                  setWorkspaceTab('chat');
                }}
                onOpenChannel={id => {
                  setRequestedChannel({ id });
                  setCommunitiesVisited(true);
                  setWorkspaceTab('communities');
                }}
                onOpenContentLink={token => void openContentLink(token)}
                onRead={() => refreshMentionCountRef.current?.()}
              />
            </Suspense>
          )}
          <div className={`nx-tab-keepalive ${workspaceTab === 'chat' ? '' : 'hidden'}`}>
            <Suspense fallback={<div className="nx-empty-side" role="status">Loading Chat…</div>}>
              <ChatWorkspace
                key={user.id}
                currentUserId={user.id}
                initialVersion={apiVersion}
                selectedConversationId={activeConvId}
                jumpTarget={chatJumpTarget}
                visible={workspaceTab === 'chat'}
                candidateRevision={candidateRevision}
                refreshOrgSubscriptionsRef={refreshOrgSubscriptionsRef}
                onReloadConversationsRef={reloadConversationsRef}
                onOrganizationAccessRevoked={handleOrganizationAccessRevoked}
                onBlockListChanged={refreshSafetyViews}
                onManageBlockedUsers={() => {
                  setFocusBlockedUsers(true);
                  setWorkspaceTab('security');
                }}
                onSelectConversation={setActiveConvId}
                onNewChat={() => setNewChatOpen(true)}
                onOpenContentLink={token => void openContentLink(token)}
                onMessageRecalled={messageId => {
                  if (messageSearchOpen) setSearchRecalledIds(current => new Set([...current, messageId]));
                  setOrganizationMessage(current => current?.messageId === messageId ? null : current);
                }}
                showDmReadReceipts={connState === 'connected' && user.dmReadReceipts?.enabled === true && pendingDmReceipts !== false}
                onDmReadReceiptPreference={applyDmReceiptPreference}
                onConnectionStateChange={setConnState}
                onUnreadTotalChange={setUnreadTotal}
                onCommunityFeedChanged={(conversationId, delta) => communityFeedListenerRef.current?.(conversationId, delta)}
                onVersionStale={handleVersionStale}
                availabilityMap={availabilityMap}
                nowMs={nowMs}
                onAvailabilityChanged={handleAvailabilityChanged}
                onOpenProfileCard={setProfileCardTarget}
              />
            </Suspense>
          </div>
          {quickSwitcherOpen && (
            <QuickSwitcher organizations={organizations} currentUserId={user.id} candidateRevision={candidateRevision}
              onClose={() => setQuickSwitcherOpen(false)} onChoose={chooseQuickSwitch} />
          )}
          {messageSearchOpen && (
            <Suspense fallback={null}>
              <MessageSearchDialog organizations={organizations} recalledIds={searchRecalledIds} onClose={() => {
                setMessageSearchOpen(false);
                setSearchRecalledIds(new Set());
              }}
                onOpenMessage={token => void openContentLink(token)} />
            </Suspense>
          )}
          {organizationMessage && (
            <Suspense fallback={null}>
              <OrganizationMessageContext target={organizationMessage} onClose={() => setOrganizationMessage(null)} />
            </Suspense>
          )}
          {newChatOpen && (
            <NewChatDialog
              currentUserId={user.id}
              candidateRevision={candidateRevision}
              onClose={() => setNewChatOpen(false)}
              onOpenChat={openDm}
              onOpenContacts={openContactsFromChat}
            />
          )}

          {communitiesVisited && (
            <div className={`nx-tab-keepalive ${workspaceTab === 'communities' ? '' : 'hidden'}`}>
              <Suspense fallback={<div className="nx-empty-side" role="status">Loading Communities…</div>}>
                <CommunitiesWorkspace
                  currentUserId={user.id}
                  currentDisplayName={user.displayName}
                  organizations={organizations}
                  requestedChannel={requestedChannel}
                  jumpTarget={communityJumpTarget}
                  feedListenerRef={communityFeedListenerRef}
                  onOpenContentLink={token => void openContentLink(token)}
                  onOpenCreateWorkspace={() => setWorkspaceModal({ mode: 'create' })}
                  onOpenJoinWorkspace={() => setWorkspaceModal({ mode: 'join' })}
                  onOpenWorkspaceSettings={org => setWorkspaceModal({ mode: 'settings', organization: org })}
                  availabilityMap={availabilityMap}
                  nowMs={nowMs}
                  onOpenProfileCard={setProfileCardTarget}
                />
              </Suspense>
            </div>
          )}

          {workspaceTab === 'directory' && (
            <div className="nx-page-scroll">
              <div className="nx-page-container">
                <div className="nx-page-head">
                  <div>
                    <h2>{activePeopleOrg ? activePeopleOrg.name : 'Contacts'}</h2>
                    <p>
                      {activePeopleOrg
                        ? 'Search workspace members by name or username and message them directly.'
                        : 'Connect with external people by exact username or a one-time contact link.'}
                    </p>
                  </div>
                  {activePeopleOrg && (
                    <Button
                      variant="default"
                      size="sm"
                      aria-label={`Manage tags for ${activePeopleOrg.name}`}
                      onClick={() => setOrgTagsModalOpen(true)}
                    >
                      <Tag size={14} aria-hidden="true" />
                      <span>Tags</span>
                    </Button>
                  )}
                </div>

                <div className="nx-filter-pills" role="group" aria-label="People scope">
                  <button
                    type="button"
                    className={`nx-filter-pill ${peopleFilter === 'contacts' ? 'active' : ''}`}
                    aria-pressed={peopleFilter === 'contacts'}
                    onClick={() => {
                      peopleFilterPickedRef.current = true;
                      setPeopleFilter('contacts');
                      setDirDeptId('');
                    }}
                  >
                    Contacts
                  </button>
                  {organizations.map(org => (
                    <button
                      key={org.id}
                      type="button"
                      className={`nx-filter-pill ${peopleFilter === org.id ? 'active' : ''}`}
                      aria-pressed={peopleFilter === org.id}
                      onClick={() => {
                        peopleFilterPickedRef.current = true;
                        setPeopleFilter(org.id);
                        setDirDeptId('');
                      }}
                    >
                      {org.name}
                    </button>
                  ))}
                </div>

                {peopleFilter === 'contacts' && (
                  <div style={{ marginBottom: 16 }}>
                    <ContactControls
                      usernameRef={contactUsernameRef}
                      onChange={() => {
                        void refreshDirectory();
                        refreshChatCandidates();
                      }}
                    />
                  </div>
                )}

                <form
                  className="nx-filter-bar"
                  onSubmit={e => {
                    e.preventDefault();
                    if (dirQuery === appliedDirectoryQuery) void refreshDirectory();
                    else setAppliedDirectoryQuery(dirQuery);
                  }}
                >
                  <div className="nx-search-input-wrap" style={{ flex: 2, minWidth: 220 }}>
                    <Search size={15} aria-hidden="true" />
                    <input
                      id="dir-search"
                      type="text"
                      aria-label={activePeopleOrg ? `Search ${activePeopleOrg.name}` : 'Search contacts'}
                      placeholder="Search by name or @username…"
                      value={dirQuery}
                      onChange={e => setDirQuery(e.target.value)}
                    />
                  </div>
                  {activePeopleOrg && (
                    <select
                      id="dir-dept"
                      className="nx-select"
                      aria-label="Filter by department"
                      value={dirDeptId}
                      onChange={e => setDirDeptId(e.target.value)}
                      style={{ flex: 1, minWidth: 180 }}
                    >
                      <option value="">All departments</option>
                      {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
                    </select>
                  )}
                  <Button type="submit" variant="primary">Search</Button>
                </form>

                {loadingDirectory && directory.length === 0 ? (
                  <Card>
                    <p className="nx-muted" role="status" style={{ margin: 0, textAlign: 'center' }}>
                      Loading people…
                    </p>
                  </Card>
                ) : directory.length === 0 && !directoryPage.error ? (
                  <Card>
                    <p className="nx-muted" style={{ margin: 0, textAlign: 'center' }}>
                      {activePeopleOrg
                        ? 'No matching workspace members.'
                        : appliedDirectoryQuery.trim()
                          ? 'No matching contacts.'
                          : 'No external contacts yet. Send a connection request or share a contact link above.'}
                    </p>
                  </Card>
                ) : directory.length > 0 && (
                  <div className="nx-dir-grid">
                    {directory.map(p => {
                      const pStatus = resolveUserAvailability(p.id, p, availabilityMap, nowMs);
                      const openPersonProfile = () =>
                        setProfileCardTarget({
                          userId: p.id,
                          organizationId: activePeopleOrg ? activePeopleOrg.id : null,
                          fallbackDisplayName: p.displayName,
                          fallbackUsername: p.username,
                          fallbackAvatarUrl: p.avatarUrl
                        });
                      return (
                        <article className="nx-person-card" key={p.id}>
                          <div className="nx-person-top">
                            <button
                              type="button"
                              className="nx-profile-trigger nx-avatar-trigger"
                              aria-label={`View profile for ${p.displayName}`}
                              onClick={openPersonProfile}
                            >
                              <Avatar
                                displayName={p.displayName}
                                avatarUrl={p.avatarUrl}
                                cacheKey={avatarBust}
                                size="lg"
                                presence={pStatus.dotTone}
                              />
                            </button>
                            <div style={{ minWidth: 0, flex: 1 }}>
                              <button
                                type="button"
                                className="nx-profile-trigger nx-name-trigger"
                                onClick={openPersonProfile}
                              >
                                <strong className="nx-user-name-wrap">{p.displayName}</strong>
                              </button>
                              <div className="nx-muted nx-inline-status-meta" style={{ marginTop: 2 }}>
                                <span className={`nx-dot ${pStatus.dotTone}`} aria-hidden="true" />
                                <span>{pStatus.label}</span>
                                {pStatus.expiryText && <span>· {pStatus.expiryText}</span>}
                              </div>
                              <div className="nx-muted" style={{ marginTop: 2 }}>
                                @{p.username}
                                {p.departmentName ? ` · ${p.departmentName}` : ''}
                                {p.branchName ? ` · ${p.branchName}` : ''}
                              </div>
                              {p.customStatus && <div className="nx-person-quote">“{p.customStatus}”</div>}
                            </div>
                          </div>

                          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                            {p.id === user.id ? (
                              <Badge variant="role">You</Badge>
                            ) : (
                              <Button variant="primary" size="sm" onClick={() => void handleOpenDm(p.id)}>
                                <MessageSquare size={14} aria-hidden="true" /> Message
                              </Button>
                            )}
                          </div>
                        </article>
                      );
                    })}
                  </div>
                )}
                <ListPaging loading={loadingDirectory} error={directoryPage.error} hasMore={directoryPage.next !== null}
                  onMore={directoryPage.loadMore} onRetry={directoryPage.retry} moreLabel="Load more people" />
              </div>
            </div>
          )}

          {workspaceTab === 'security' && (
            <div className="nx-page-scroll">
              <div className="nx-page-container">
                <div className="nx-page-head">
                  <div>
                    <h2>Profile &amp; security</h2>
                    <p>Manage your profile, account security, and blocked users.</p>
                  </div>
                  <Button variant="danger" onClick={handleLogout}>
                    <LogOut size={15} aria-hidden="true" /> Sign out
                  </Button>
                </div>

                {user.mfaSetupRequired && (
                  <ToastBar
                    variant="info"
                    rounded
                    message="Recommended: enable authenticator TOTP below to add extra protection to your workspace admin account."
                  />
                )}

                <div className="nx-settings-grid">
                  <Card title="Messaging privacy">
                    <label className="nx-dm-receipts-setting">
                      <input type="checkbox" role="switch" checked={pendingDmReceipts ?? user.dmReadReceipts?.enabled ?? true}
                        disabled={pendingDmReceipts !== null} aria-describedby="nx-dm-read-receipts-help"
                        onChange={event => void updateDmReceiptPreference(event.currentTarget.checked)} />
                      DM read receipts
                    </label>
                    <p className="nx-muted" id="nx-dm-read-receipts-help">Direct messages show when they were read if both people allow read receipts.</p>
                    {dmReceiptsError && <p className="nx-error" role="alert">{dmReceiptsError}</p>}
                  </Card>
                  <Card title="Availability">
                    <AvailabilityPicker
                      variant="inline"
                      availability={myEffectiveAvailability}
                      isOnline={myIsOnline}
                      nowMs={nowMs}
                      onUpdated={handleAvailabilityUpdated}
                    />
                  </Card>

                  {activeProfileOrg && (
                    <Card title="Profile & Status">
                      <div className="nx-profile-hero">
                        <Avatar displayName={user.displayName} avatarUrl={user.avatarUrl} cacheKey={avatarBust} size="lg" />
                        <div>
                          <strong className="nx-user-name-wrap">{user.displayName}</strong>
                          <div className="nx-muted" style={{ marginTop: 3 }}>
                            @{user.username} · <Badge variant="role">{activeProfileOrg.role}</Badge>
                          </div>
                        </div>
                      </div>

                      {organizations.length > 1 && (
                        <Field label="Workspace profile" htmlFor="profile-org-select" compact>
                          <select
                            id="profile-org-select"
                            className="nx-select"
                            value={activeProfileOrg.id}
                            onChange={e => {
                              setProfileOrgId(e.target.value);
                              void syncOrgProfile(e.target.value);
                            }}
                          >
                            {organizations.map(org => (
                              <option key={org.id} value={org.id}>{org.name}</option>
                            ))}
                          </select>
                        </Field>
                      )}

                      <form onSubmit={handleUpdateCustomStatus} className="nx-status-form">
                        <div style={{ flex: 1 }}>
                          <Field label="Custom status" htmlFor="custom-status" compact>
                            <input
                              id="custom-status"
                              type="text"
                              maxLength={100}
                              placeholder="e.g. On site until 15:00"
                              value={customStatusInput}
                              onChange={e => setCustomStatusInput(e.target.value)}
                            />
                          </Field>
                        </div>
                        <Button type="submit" variant="primary">Save</Button>
                      </form>

                      <Field label="Avatar image (re-encoded to 256×256 PNG, max 2 MB)" htmlFor="avatar-upload" compact>
                        <input id="avatar-upload" type="file" accept="image/png,image/jpeg,image/webp,image/gif" onChange={handleAvatarChange} />
                      </Field>
                    </Card>
                  )}

                  <div ref={blockedUsersRef} tabIndex={-1} role="region" aria-label="Blocked user settings">
                    <Suspense fallback={<div className="nx-empty-side" role="status">Loading blocked users…</div>}>
                      <BlockedUsersSettings onChanged={refreshSafetyViews} />
                    </Suspense>
                  </div>

                  <Card title="Change password">
                    <form onSubmit={handleChangePassword}>
                      <Field label="Current password" htmlFor="cur-pw">
                        <input id="cur-pw" type="password" autoComplete="current-password" required value={currentPassword} onChange={e => setCurrentPassword(e.target.value)} />
                      </Field>
                      <Field label="New password (at least 12 characters)" htmlFor="new-pw">
                        <input id="new-pw" type="password" autoComplete="new-password" required minLength={12} value={newPassword} onChange={e => setNewPassword(e.target.value)} />
                      </Field>
                      <Button type="submit" variant="primary">Update password</Button>
                    </form>
                  </Card>

                  <Card title="Authenticator MFA (TOTP)">
                    {user.mfaEnabled ? (
                      <p className="nx-ok">TOTP multi-factor authentication is active on your account.</p>
                    ) : mfaSecret ? (
                      <form onSubmit={handleEnableMfa}>
                        <p className="nx-muted">Add this shared key to your authenticator app:</p>
                        <p><code>{mfaSecret}</code></p>
                        <Field label="6-digit code" htmlFor="mfa-code">
                          <input id="mfa-code" type="text" inputMode="numeric" required value={mfaCode} onChange={e => setMfaCode(e.target.value)} />
                        </Field>
                        <Button type="submit" variant="primary">Verify &amp; enable MFA</Button>
                      </form>
                    ) : (
                      <>
                        <p className="nx-muted" style={{ marginBottom: 14 }}>Protect your Nexus account with a 6-digit authenticator code.</p>
                        <Button onClick={handleStartMfa}>Set up authenticator app</Button>
                      </>
                    )}
                    {recoveryCodes.length > 0 && (
                      <div style={{ marginTop: 14 }}>
                        <p className="nx-muted">Save these one-time recovery codes:</p>
                        <code>{recoveryCodes.join('  ')}</code>
                      </div>
                    )}
                  </Card>

                  <Card title={`Signed-in devices (${sessions.length})`}>
                    {sessions.map(s => (
                      <div className="nx-row" key={s.id}>
                        <div style={{ minWidth: 0 }}>
                          <div>
                            <strong>{s.isCurrent ? 'Current session' : 'Other session'}</strong>
                            <span className="nx-muted"> · {s.ipAddress}</span>
                          </div>
                          <div className="nx-muted" style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{s.userAgent}</div>
                        </div>
                        {!s.isCurrent && (
                          <Button variant="danger" size="sm" onClick={() => void handleRevokeSession(s.id)}>
                            Sign out
                          </Button>
                        )}
                      </div>
                    ))}
                  </Card>
                </div>
              </div>
            </div>
          )}

          {workspaceTab === 'hr' && hrOrganizations.length > 0 && (
            <div className="nx-page-scroll">
              <div className="nx-page-container">
                <div className="nx-page-head">
                  <div>
                    <h2>HR CSV Import</h2>
                    <p>Preview changes, update employee records, and review suggested account matches.</p>
                  </div>
                </div>
                <Card>
                  {hrOrganizations.length > 1 && (
                    <Field label="Workspace" htmlFor="hr-org-select">
                      <select
                        id="hr-org-select"
                        className="nx-select"
                        value={hrOrgId || hrOrganizations[0].id}
                        onChange={e => setHrOrgId(e.target.value)}
                      >
                        {hrOrganizations.map(org => (
                          <option key={org.id} value={org.id}>{org.name}</option>
                        ))}
                      </select>
                    </Field>
                  )}
                  <Field label="CSV records" htmlFor="hr-csv">
                    <textarea id="hr-csv" rows={7} value={hrCsv} onChange={e => setHrCsv(e.target.value)} />
                  </Field>
                  <div className="nx-inline-actions" style={{ marginBottom: 14 }}>
                    <Button onClick={() => void handleHrImport(true)}>Dry run (diff)</Button>
                    <Button variant="primary" onClick={() => void handleHrImport(false)}>Apply import</Button>
                  </div>
                  {hrReport && (
                    <div>
                      <p className="nx-muted">
                        {hrReport.dryRun ? 'Dry run' : 'Applied'} · Total: {hrReport.totalRows} · Added: {hrReport.addedCount} · Updated: {hrReport.updatedCount} · Unchanged: {hrReport.unchangedCount} · Deactivated: {hrReport.deactivatedCount}
                      </p>
                      {hrReport.matchSuggestions.length > 0 && (
                        <div>
                          <strong>Suggested account matches ({hrReport.matchSuggestions.length})</strong>
                          {hrReport.matchSuggestions.map(m => (
                            <div className="nx-row" key={`${m.userId}-${m.employeeCode}`}>
                              <span>{m.displayName} (@{m.username}) → <strong>{m.employeeCode}</strong> ({m.employeeFullName})</span>
                              <Badge variant="role">{m.confidence}</Badge>
                            </div>
                          ))}
                        </div>
                      )}
                    </div>
                  )}
                </Card>
              </div>
            </div>
          )}
        </main>
      </div>

      {profileCardTarget && (
        <Suspense fallback={null}>
          <ProfileCardModal
            target={profileCardTarget}
            currentUserId={user.id}
            availabilityMap={availabilityMap}
            nowMs={nowMs}
            avatarCacheKey={avatarBust}
            onClose={() => setProfileCardTarget(null)}
            onStartDirectChat={openDm}
          />
        </Suspense>
      )}

      {orgTagsModalOpen && activePeopleOrg && (
        <Suspense fallback={null}>
          <PeopleTagsModal
            open={orgTagsModalOpen}
            onClose={() => setOrgTagsModalOpen(false)}
            scope={{
              kind: 'organization',
              organizationId: activePeopleOrg.id,
              organizationName: activePeopleOrg.name
            }}
          />
        </Suspense>
      )}

      {workspaceModal && (
        <WorkspaceModals
          modal={workspaceModal}
          mfaEnabled={user.mfaEnabled}
          onClose={() => setWorkspaceModal(null)}
          onOrganizationsChanged={handleOrganizationsChanged}
          onSecurity={() => {
            setWorkspaceModal(null);
            setWorkspaceTab('security');
          }}
        />
      )}
    </div>
  );
}

createRoot(document.getElementById('root')!).render(<App />);

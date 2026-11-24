import { FormEvent, RefObject } from 'react';
import { ShieldCheck, UserPlus } from 'lucide-react';
import { LiveAvailabilityEntry, resolveUserAvailability } from '../availability';
import { ListPaging } from '../cursorList';
import { OrganizationItem } from '../organizationControls';
import { ProfileCardTarget } from '../ProfileCardModal';
import { orgApiPath } from '../scope';
import { OwnershipTransfer } from '../transferOwnership';
import { Avatar, Badge, Button, Field } from '../ui';
import { CommunityItemDto, CommunityMemberDto, DepartmentLite, DirectoryUserLite } from './types';

export interface CommunityMembersDrawerProps {
  headingRef: RefObject<HTMLHeadingElement | null>;
  currentUserId: string;
  selectedCommunity: CommunityItemDto;
  activeOrg: OrganizationItem | null;
  canManage: boolean;
  departments: DepartmentLite[];
  mappedDeptId: string;
  setMappedDeptId: (id: string) => void;
  onSyncDepartment: () => void;
  onToggleOfficial: () => void;
  memberSearchQuery: string;
  setMemberSearchQuery: (q: string) => void;
  addMemberUserId: string;
  setAddMemberUserId: (id: string) => void;
  addCandidates: {
    items: DirectoryUserLite[];
    loading: boolean;
    error: { message: string } | null;
    next: string | null;
    loadMore: () => void;
    retry: () => void;
  };
  onAddMember: (e: FormEvent) => void;
  members: CommunityMemberDto[];
  loadingMembers: boolean;
  memberListError: { message: string } | null;
  memberListHasMore: boolean;
  onLoadMoreMembers: () => void;
  onRetryMembers: () => void;
  availabilityMap: Record<string, LiveAvailabilityEntry>;
  nowMs: number;
  onOpenProfileCard: (target: ProfileCardTarget) => void;
  onSetOwnershipTransfer: (target: OwnershipTransfer) => void;
  onToggleAdmin: (member: CommunityMemberDto) => void;
  onRemoveMember: (userId: string) => void;
  onLeaveCommunity: () => void;
  onClose: () => void;
}

export function CommunityMembersDrawer({
  headingRef,
  currentUserId,
  selectedCommunity,
  activeOrg,
  canManage,
  departments,
  mappedDeptId,
  setMappedDeptId,
  onSyncDepartment,
  onToggleOfficial,
  memberSearchQuery,
  setMemberSearchQuery,
  addMemberUserId,
  setAddMemberUserId,
  addCandidates,
  onAddMember,
  members,
  loadingMembers,
  memberListError,
  memberListHasMore,
  onLoadMoreMembers,
  onRetryMembers,
  availabilityMap,
  nowMs,
  onOpenProfileCard,
  onSetOwnershipTransfer,
  onToggleAdmin,
  onRemoveMember,
  onLeaveCommunity,
  onClose
}: CommunityMembersDrawerProps) {
  return (
    <aside className="nx-drawer" aria-label="Community members">
      <div className="nx-drawer-head">
        <h4 ref={headingRef} tabIndex={-1}>{loadingMembers ? 'Members' : `Members (${members.length})`}</h4>
        <Button variant="ghost" size="sm" onClick={onClose}>
          Close
        </Button>
      </div>

      <div className="nx-drawer-body">
        {activeOrg?.role === 'CommsAdmin' && (
          <div className="nx-drawer-section">
            <Button variant="default" size="sm" fullWidth onClick={onToggleOfficial}>
              <ShieldCheck size={14} aria-hidden="true" />{' '}
              {selectedCommunity.isOfficial ? 'Remove Official status' : 'Designate as Official'}
            </Button>
          </div>
        )}

        {canManage && departments.length > 0 && (
          <div className="nx-drawer-section">
            <Field label="Department membership sync" htmlFor="comm-dept-map" compact>
              <select
                id="comm-dept-map"
                className="nx-select"
                value={mappedDeptId}
                onChange={e => setMappedDeptId(e.target.value)}
              >
                <option value="">None (manual membership)</option>
                {departments.map(d => (
                  <option key={d.id} value={d.id}>{d.name}</option>
                ))}
              </select>
            </Field>
            <Button variant="default" size="sm" fullWidth onClick={onSyncDepartment}>
              Save &amp; sync department
            </Button>
          </div>
        )}

        {canManage && (
          <form className="nx-drawer-section" onSubmit={onAddMember}>
            <Field label="Find people to add" htmlFor="comm-add-search" compact>
              <input
                id="comm-add-search"
                type="search"
                value={memberSearchQuery}
                onChange={event => {
                  setMemberSearchQuery(event.target.value);
                  setAddMemberUserId('');
                }}
              />
            </Field>
            <Field label="Add member" htmlFor="comm-add-member" compact>
              <select
                id="comm-add-member"
                className="nx-select"
                value={addMemberUserId}
                onChange={e => setAddMemberUserId(e.target.value)}
              >
                <option value="">
                  {addCandidates.loading
                    ? 'Loading people…'
                    : addCandidates.items.length
                      ? 'Select a person'
                      : 'No matching people'}
                </option>
                {addCandidates.items.map(u => (
                  <option key={u.id} value={u.id}>
                    {u.displayName} (@{u.username})
                  </option>
                ))}
              </select>
            </Field>
            <Button type="submit" variant="primary" size="sm" fullWidth disabled={!addMemberUserId}>
              <UserPlus size={14} aria-hidden="true" /> Add member
            </Button>
            <ListPaging
              loading={addCandidates.loading}
              error={addCandidates.error}
              hasMore={addCandidates.next !== null}
              onMore={addCandidates.loadMore}
              onRetry={addCandidates.retry}
              moreLabel="Load more people to add"
            />
          </form>
        )}

        <div className="nx-drawer-section">
          {loadingMembers && members.length === 0 ? (
            <p className="nx-muted" role="status" style={{ textAlign: 'center', margin: '12px 0' }}>
              Loading members…
            </p>
          ) : members.map(m => {
            const mStatus = resolveUserAvailability(m.userId, m, availabilityMap, nowMs);
            const openMemberProfile = () =>
              onOpenProfileCard({
                userId: m.userId,
                organizationId: selectedCommunity.organizationId,
                fallbackDisplayName: m.displayName,
                fallbackUsername: m.username,
                fallbackAvatarUrl: m.avatarUrl
              });
            return (
              <div className="nx-member-row nx-managed-member" key={m.userId}>
                <button
                  type="button"
                  className="nx-profile-trigger nx-avatar-trigger"
                  aria-label={`View profile for ${m.displayName}`}
                  onClick={openMemberProfile}
                >
                  <Avatar displayName={m.displayName} avatarUrl={m.avatarUrl} size="sm" presence={mStatus.dotTone} />
                </button>
                <div style={{ minWidth: 0, flex: 1 }}>
                  <button
                    type="button"
                    className="nx-profile-trigger nx-name-trigger"
                    onClick={openMemberProfile}
                  >
                    <strong>{m.displayName}</strong>
                  </button>
                  <div className="nx-muted nx-inline-status-meta">
                    <span className={`nx-dot ${mStatus.dotTone}`} aria-hidden="true" />
                    <span>{mStatus.label}</span>
                    <span>· @{m.username}</span>
                    <Badge variant="role">{m.role}</Badge>
                  </div>
                </div>
                {canManage && m.userId !== currentUserId && m.role !== 'Owner' && (
                  <div className="nx-member-actions">
                    {selectedCommunity.myRole === 'Owner' && m.isActive !== false &&
                      (!selectedCommunity.isOfficial || !m.isUnverified) && (
                      <Button
                        variant="ghost"
                        size="sm"
                        aria-label={`Transfer ownership to ${m.displayName} (@${m.username})`}
                        onClick={() => onSetOwnershipTransfer({
                          path: orgApiPath(selectedCommunity.organizationId, `/api/communities/${selectedCommunity.id}/transfer-ownership`),
                          resourceLabel: 'Community',
                          resourceName: selectedCommunity.name,
                          userId: m.userId,
                          displayName: m.displayName,
                          username: m.username
                        })}
                      >
                        Transfer ownership
                      </Button>
                    )}
                    {selectedCommunity.myRole === 'Owner' && (
                      <Button variant="ghost" size="sm" onClick={() => onToggleAdmin(m)}>
                        {m.role === 'Admin' ? 'Demote' : 'Admin'}
                      </Button>
                    )}
                    <Button variant="danger" size="sm" onClick={() => onRemoveMember(m.userId)}>
                      Remove
                    </Button>
                  </div>
                )}
              </div>
            );
          })}
          <ListPaging
            loading={loadingMembers}
            error={memberListError}
            hasMore={memberListHasMore}
            onMore={onLoadMoreMembers}
            onRetry={onRetryMembers}
            moreLabel="Load more members"
          />
        </div>

        {selectedCommunity.myRole !== 'Owner' && (
          <div className="nx-drawer-section">
            <Button variant="danger" size="sm" fullWidth onClick={onLeaveCommunity}>
              Leave community
            </Button>
          </div>
        )}
      </div>
    </aside>
  );
}

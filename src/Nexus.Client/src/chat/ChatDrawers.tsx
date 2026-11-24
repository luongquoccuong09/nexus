import { RefObject } from 'react';
import { LiveAvailabilityEntry, resolveUserAvailability } from '../availability';
import { ListPaging } from '../cursorList';
import { ProfileCardTarget } from '../ProfileCardModal';
import { personalApiPath } from '../scope';
import { OwnershipTransfer } from '../transferOwnership';
import { Badge, Button, Field } from '../ui';
import { ConversationListItem, DirectoryUserLite, GroupMemberItem } from './types';

export interface ChatMembersDrawerProps {
  activeConv: ConversationListItem;
  currentUserId: string;
  headingRef: RefObject<HTMLElement | null>;
  groupMembers: GroupMemberItem[];
  loadingMembers: boolean;
  memberError: { message: string } | null;
  memberHasMore: boolean;
  onLoadMoreMembers: () => void;
  onRetryMembers: () => void;
  groupMemberSearch: string;
  setGroupMemberSearch: (q: string) => void;
  addMemberId: string;
  setAddMemberId: (id: string) => void;
  addMemberCandidates: {
    items: DirectoryUserLite[];
    loading: boolean;
    error: { message: string } | null;
    next: string | null;
    loadMore: () => void;
    retry: () => void;
  };
  availabilityMap: Record<string, LiveAvailabilityEntry>;
  nowMs: number;
  onOpenProfileCard: (target: ProfileCardTarget) => void;
  onAddMember: () => void;
  onRemoveMember: (userId: string) => void;
  onLeaveGroup: () => void;
  onSetOwnershipTransfer: (target: OwnershipTransfer) => void;
  onClose: () => void;
}

export function ChatMembersDrawer({
  activeConv,
  currentUserId,
  headingRef,
  groupMembers,
  loadingMembers,
  memberError,
  memberHasMore,
  onLoadMoreMembers,
  onRetryMembers,
  groupMemberSearch,
  setGroupMemberSearch,
  addMemberId,
  setAddMemberId,
  addMemberCandidates,
  availabilityMap,
  nowMs,
  onOpenProfileCard,
  onAddMember,
  onRemoveMember,
  onLeaveGroup,
  onSetOwnershipTransfer,
  onClose
}: ChatMembersDrawerProps) {
  return (
    <aside className="nx-drawer" aria-label="Group members">
      <div className="nx-drawer-head">
        <strong ref={headingRef} tabIndex={-1}>{loadingMembers ? 'Members' : `Members (${groupMembers.length})`}</strong>
        <Button variant="ghost" size="sm" onClick={onClose}>Close</Button>
      </div>
      <div className="nx-drawer-body">
        {(activeConv.role === 'Owner' || activeConv.role === 'Admin') && (
          <div>
            <Field label="Find people to add" htmlFor="group-member-search" compact>
              <input
                id="group-member-search"
                type="search"
                value={groupMemberSearch}
                onChange={event => {
                  setGroupMemberSearch(event.target.value);
                  setAddMemberId('');
                }}
              />
            </Field>
            <Field label="Add person" htmlFor="add-group-member" compact>
              <div className="nx-inline-actions">
                <select id="add-group-member" value={addMemberId} onChange={e => setAddMemberId(e.target.value)}>
                  <option value="">
                    {addMemberCandidates.loading
                      ? 'Loading people…'
                      : addMemberCandidates.items.length
                        ? 'Select a person'
                        : 'No matching people'}
                  </option>
                  {addMemberCandidates.items.map(u => (
                    <option key={u.id} value={u.id}>
                      {u.displayName} (@{u.username})
                    </option>
                  ))}
                </select>
                <Button variant="primary" size="sm" disabled={!addMemberId} onClick={onAddMember}>
                  Add
                </Button>
              </div>
            </Field>
            <ListPaging
              loading={addMemberCandidates.loading}
              error={addMemberCandidates.error}
              hasMore={addMemberCandidates.next !== null}
              onMore={addMemberCandidates.loadMore}
              onRetry={addMemberCandidates.retry}
              moreLabel="Load more people to add"
            />
          </div>
        )}

        <div>
          {groupMembers.map(gm => {
            const gmStatus = resolveUserAvailability(gm.userId, gm, availabilityMap, nowMs);
            return (
              <div className="nx-row nx-managed-member" key={gm.userId}>
                <div style={{ minWidth: 0, flex: 1 }}>
                  <div className="nx-user-name-wrap">
                    <button
                      type="button"
                      className="nx-profile-trigger nx-name-trigger"
                      onClick={() =>
                        onOpenProfileCard({
                          userId: gm.userId,
                          organizationId: null,
                          fallbackDisplayName: gm.displayName,
                          fallbackUsername: gm.username
                        })
                      }
                    >
                      <strong style={{ fontSize: 13 }}>{gm.displayName}</strong>
                    </button>
                    <Badge variant="role">{gm.role}</Badge>
                  </div>
                  <div className="nx-muted nx-inline-status-meta">
                    <span className={`nx-dot ${gmStatus.dotTone}`} aria-hidden="true" />
                    <span>{gmStatus.label}</span>
                    <span>· @{gm.username}</span>
                  </div>
                </div>
                {(activeConv.role === 'Owner' || activeConv.role === 'Admin') &&
                  gm.userId !== currentUserId &&
                  gm.role !== 'Owner' && (
                    <div className="nx-member-actions">
                      {activeConv.role === 'Owner' && gm.isActive !== false && (
                        <Button
                          variant="ghost"
                          size="sm"
                          disabled={loadingMembers}
                          aria-label={`Transfer ownership to ${gm.displayName} (@${gm.username})`}
                          onClick={() => onSetOwnershipTransfer({
                            path: personalApiPath(`/api/conversations/${activeConv.id}/transfer-ownership`),
                            resourceLabel: 'Group chat',
                            resourceName: activeConv.title,
                            userId: gm.userId,
                            displayName: gm.displayName,
                            username: gm.username
                          })}
                        >
                          Transfer ownership
                        </Button>
                      )}
                      <Button variant="danger" size="sm" onClick={() => onRemoveMember(gm.userId)}>
                        Remove
                      </Button>
                    </div>
                  )}
              </div>
            );
          })}
        </div>
        <ListPaging
          loading={loadingMembers}
          error={memberError}
          hasMore={memberHasMore}
          onMore={onLoadMoreMembers}
          onRetry={onRetryMembers}
          moreLabel="Load more members"
        />

        <div className="nx-drawer-footer">
          <Button variant="danger" fullWidth onClick={onLeaveGroup}>
            Leave group
          </Button>
        </div>
      </div>
    </aside>
  );
}

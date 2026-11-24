import { FormEvent } from 'react';
import { ListPaging } from '../cursorList';
import { SidebarSectionDto } from '../sidebarPrefs';
import { SidebarSectionModal } from '../sidebarControls';
import { OwnershipTransfer, TransferOwnershipDialog } from '../transferOwnership';
import { Avatar, Button, Field, Modal, ToastBar } from '../ui';
import { ConversationListItem, DirectoryUserLite } from './types';

export interface ChatModalsProps {
  activeConv: ConversationListItem | null;
  showNewGroupModal: boolean;
  setShowNewGroupModal: (open: boolean) => void;
  newGroupTitle: string;
  setNewGroupTitle: (v: string) => void;
  newGroupSearch: string;
  setNewGroupSearch: (v: string) => void;
  selectedGroupMemberIds: string[];
  setSelectedGroupMemberIds: (updater: (prev: string[]) => string[]) => void;
  groupCandidates: {
    items: DirectoryUserLite[];
    loading: boolean;
    error: { message: string } | null;
    next: string | null;
    loadMore: () => void;
    retry: () => void;
  };
  onCreateGroup: (e: FormEvent) => void;
  blockTarget: { id: string; name: string } | null;
  setBlockTarget: (target: { id: string; name: string } | null) => void;
  blockingPeer: boolean;
  blockError: string;
  onBlockPeer: () => void;
  showReportModal: boolean;
  setShowReportModal: (open: boolean) => void;
  reportReason: string;
  setReportReason: (v: string) => void;
  reportDetails: string;
  setReportDetails: (v: string) => void;
  onSubmitReport: (e: FormEvent) => void;
  ownershipTransfer: OwnershipTransfer | null;
  onCloseOwnershipTransfer: () => void;
  onRefreshOwnership: () => void;
  onTransferredOwnership: () => void;
  sectionModal: { mode: 'create' } | { mode: 'rename'; section: SidebarSectionDto } | null;
  setSectionModal: (modal: { mode: 'create' } | { mode: 'rename'; section: SidebarSectionDto } | null) => void;
  sidebarBusy: boolean;
  onSubmitSection: (name: string) => Promise<void>;
}

export function ChatModals({
  activeConv,
  showNewGroupModal,
  setShowNewGroupModal,
  newGroupTitle,
  setNewGroupTitle,
  newGroupSearch,
  setNewGroupSearch,
  selectedGroupMemberIds,
  setSelectedGroupMemberIds,
  groupCandidates,
  onCreateGroup,
  blockTarget,
  setBlockTarget,
  blockingPeer,
  blockError,
  onBlockPeer,
  showReportModal,
  setShowReportModal,
  reportReason,
  setReportReason,
  reportDetails,
  setReportDetails,
  onSubmitReport,
  ownershipTransfer,
  onCloseOwnershipTransfer,
  onRefreshOwnership,
  onTransferredOwnership,
  sectionModal,
  setSectionModal,
  sidebarBusy,
  onSubmitSection
}: ChatModalsProps) {
  return (
    <>
      {ownershipTransfer && activeConv && (
        <TransferOwnershipDialog
          target={ownershipTransfer}
          onClose={onCloseOwnershipTransfer}
          onRefresh={onRefreshOwnership}
          onTransferred={onTransferredOwnership}
        />
      )}

      <Modal
        open={showNewGroupModal}
        onOpenChange={setShowNewGroupModal}
        title="Create group chat"
        footer={
          <>
            <Button onClick={() => setShowNewGroupModal(false)}>Cancel</Button>
            <Button type="submit" form="create-group-form" variant="primary">Create group</Button>
          </>
        }
      >
        <form id="create-group-form" onSubmit={onCreateGroup}>
          <Field label="Group name" htmlFor="modal-group-name">
            <input
              id="modal-group-name"
              type="text"
              required
              maxLength={120}
              placeholder="e.g. Engineering, Ops Team…"
              value={newGroupTitle}
              onChange={e => setNewGroupTitle(e.target.value)}
            />
          </Field>

          <Field label="Find people to add (optional)" htmlFor="new-group-search" compact>
            <input
              id="new-group-search"
              type="search"
              value={newGroupSearch}
              onChange={event => setNewGroupSearch(event.target.value)}
            />
          </Field>
          {selectedGroupMemberIds.length > 0 && <p className="nx-muted">{selectedGroupMemberIds.length} selected</p>}
          {groupCandidates.items.length === 0 && !groupCandidates.error && (
            <p className="nx-muted">{groupCandidates.loading ? 'Searching…' : 'No matching people.'}</p>
          )}
          {groupCandidates.items.length > 0 && (
            <div className="nx-member-picker">
              {groupCandidates.items.map(u => {
                const checked = selectedGroupMemberIds.includes(u.id);
                const contextTag = [
                  ...(u.workspaceNames ?? []),
                  ...(u.isContact ? ['Contact'] : []),
                  ...(u.departmentName ? [u.departmentName] : [])
                ].filter(Boolean).join(', ');
                return (
                  <label className="nx-member-option" key={u.id}>
                    <input
                      type="checkbox"
                      checked={checked}
                      onChange={e => {
                        setSelectedGroupMemberIds(prev =>
                          e.target.checked ? [...prev, u.id] : prev.filter(id => id !== u.id)
                        );
                      }}
                    />
                    <Avatar displayName={u.displayName} avatarUrl={u.avatarUrl} size="sm" />
                    <div>
                      <div className="nx-user-name-wrap">
                        <strong style={{ fontSize: 13 }}>{u.displayName}</strong>
                      </div>
                      <div className="nx-muted">@{u.username}{contextTag ? ` · ${contextTag}` : ''}</div>
                    </div>
                  </label>
                );
              })}
            </div>
          )}
          <ListPaging
            loading={groupCandidates.loading}
            error={groupCandidates.error}
            hasMore={groupCandidates.next !== null}
            onMore={groupCandidates.loadMore}
            onRetry={groupCandidates.retry}
            moreLabel="Load more people"
          />
        </form>
      </Modal>

      <Modal
        open={Boolean(blockTarget)}
        onOpenChange={open => {
          if (!open && !blockingPeer) setBlockTarget(null);
        }}
        title={blockTarget ? `Block ${blockTarget.name}?` : 'Block user'}
        footer={
          <>
            <Button disabled={blockingPeer} onClick={() => setBlockTarget(null)}>Cancel</Button>
            <Button variant="danger" disabled={blockingPeer} onClick={onBlockPeer}>
              {blockingPeer ? 'Blocking…' : 'Block user'}
            </Button>
          </>
        }
      >
        <p className="nx-muted">Neither of you will be able to send direct messages until you unblock this user.</p>
        {blockError && <ToastBar variant="error" message={blockError} />}
      </Modal>

      <Modal
        open={showReportModal && Boolean(activeConv?.peer)}
        onOpenChange={setShowReportModal}
        title={activeConv?.peer ? `Report ${activeConv.peer.displayName}` : 'Report user'}
        footer={
          <>
            <Button onClick={() => setShowReportModal(false)}>Cancel</Button>
            <Button type="submit" form="report-user-form" variant="danger">Submit report</Button>
          </>
        }
      >
        <form id="report-user-form" onSubmit={onSubmitReport}>
          <Field label="Reason" htmlFor="report-reason">
            <select id="report-reason" value={reportReason} onChange={e => setReportReason(e.target.value)}>
              <option value="spam">Spam or unsolicited messages</option>
              <option value="impersonation">Impersonation</option>
              <option value="harassment">Harassment or abuse</option>
              <option value="other">Other</option>
            </select>
          </Field>
          <Field label="Additional details (optional)" htmlFor="report-details" compact>
            <textarea
              id="report-details"
              rows={3}
              maxLength={1000}
              value={reportDetails}
              onChange={e => setReportDetails(e.target.value)}
            />
          </Field>
        </form>
      </Modal>

      <SidebarSectionModal
        open={sectionModal !== null}
        mode={sectionModal?.mode === 'rename' ? 'rename' : 'create'}
        initialName={sectionModal?.mode === 'rename' ? sectionModal.section.name : ''}
        busy={sidebarBusy}
        onClose={() => setSectionModal(null)}
        onSubmit={onSubmitSection}
      />
    </>
  );
}

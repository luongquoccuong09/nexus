import { FormEvent } from 'react';
import { Archive } from 'lucide-react';
import { OrganizationItem } from '../organizationControls';
import { Button, Field, Modal } from '../ui';
import { CommunityChannelDto, CommunityItemDto } from './types';

export interface CommunityModalsProps {
  organizations: OrganizationItem[];
  selectedCommunity: CommunityItemDto | null;
  selectedSubChannel: CommunityChannelDto | null;
  createOpen: boolean;
  setCreateOpen: (open: boolean) => void;
  createOrgId: string;
  setCreateOrgId: (id: string) => void;
  newCommName: string;
  setNewCommName: (v: string) => void;
  newCommDesc: string;
  setNewCommDesc: (v: string) => void;
  newCommVisibility: 'Public' | 'Private';
  setNewCommVisibility: (v: 'Public' | 'Private') => void;
  onCreateCommunity: (e: FormEvent) => void;
  addChannelForId: string | null;
  setAddChannelForId: (id: string | null) => void;
  newChannelName: string;
  setNewChannelName: (v: string) => void;
  newChannelDesc: string;
  setNewChannelDesc: (v: string) => void;
  newChannelPolicy: 'Members' | 'AdminsOnly';
  setNewChannelPolicy: (v: 'Members' | 'AdminsOnly') => void;
  onCreateChannel: (e: FormEvent) => void;
  editCommOpen: boolean;
  setEditCommOpen: (open: boolean) => void;
  editCommName: string;
  setEditCommName: (v: string) => void;
  editCommDesc: string;
  setEditCommDesc: (v: string) => void;
  onSaveCommunityMeta: (e: FormEvent) => void;
  editChannelOpen: boolean;
  setEditChannelOpen: (open: boolean) => void;
  editChannelName: string;
  setEditChannelName: (v: string) => void;
  editChannelDesc: string;
  setEditChannelDesc: (v: string) => void;
  onSaveChannelMeta: (e: FormEvent) => void;
  archiveConfirmOpen: boolean;
  setArchiveConfirmOpen: (open: boolean) => void;
  onArchiveChannel: () => void;
  savingMeta: boolean;
}

export function CommunityModals({
  organizations,
  selectedCommunity,
  selectedSubChannel,
  createOpen,
  setCreateOpen,
  createOrgId,
  setCreateOrgId,
  newCommName,
  setNewCommName,
  newCommDesc,
  setNewCommDesc,
  newCommVisibility,
  setNewCommVisibility,
  onCreateCommunity,
  addChannelForId,
  setAddChannelForId,
  newChannelName,
  setNewChannelName,
  newChannelDesc,
  setNewChannelDesc,
  newChannelPolicy,
  setNewChannelPolicy,
  onCreateChannel,
  editCommOpen,
  setEditCommOpen,
  editCommName,
  setEditCommName,
  editCommDesc,
  setEditCommDesc,
  onSaveCommunityMeta,
  editChannelOpen,
  setEditChannelOpen,
  editChannelName,
  setEditChannelName,
  editChannelDesc,
  setEditChannelDesc,
  onSaveChannelMeta,
  archiveConfirmOpen,
  setArchiveConfirmOpen,
  onArchiveChannel,
  savingMeta
}: CommunityModalsProps) {
  return (
    <>
      <Modal
        open={createOpen}
        onOpenChange={setCreateOpen}
        title="Create community"
        description="Starts with a main community feed. You can add optional channels anytime."
      >
        <form onSubmit={onCreateCommunity}>
          {organizations.length > 1 && (
            <Field label="Workspace" htmlFor="new-comm-workspace">
              <select
                id="new-comm-workspace"
                className="nx-select"
                value={createOrgId || organizations[0]?.id || ''}
                onChange={e => setCreateOrgId(e.target.value)}
              >
                {organizations.map(org => (
                  <option key={org.id} value={org.id}>{org.name}</option>
                ))}
              </select>
            </Field>
          )}
          <Field label="Community name" htmlFor="new-comm-name">
            <input
              id="new-comm-name"
              type="text"
              required
              maxLength={120}
              placeholder="e.g. Engineering, Company Announcements"
              value={newCommName}
              onChange={e => setNewCommName(e.target.value)}
            />
          </Field>
          <Field label="Description (optional)" htmlFor="new-comm-desc">
            <textarea
              id="new-comm-desc"
              rows={2}
              maxLength={500}
              placeholder="What is this community for?"
              value={newCommDesc}
              onChange={e => setNewCommDesc(e.target.value)}
            />
          </Field>
          <fieldset className="nx-vis-fieldset">
            <legend>Community access</legend>
            <label className={`nx-vis-option ${newCommVisibility === 'Public' ? 'selected' : ''}`}>
              <input
                type="radio"
                name="community-visibility"
                value="Public"
                checked={newCommVisibility === 'Public'}
                onChange={() => setNewCommVisibility('Public')}
              />
              <div>
                <strong>Shared Community</strong>
                <p>Any active member of this workspace can browse and join. Official communities require a confirmed employee profile.</p>
              </div>
            </label>
            <label className={`nx-vis-option ${newCommVisibility === 'Private' ? 'selected' : ''}`}>
              <input
                type="radio"
                name="community-visibility"
                value="Private"
                checked={newCommVisibility === 'Private'}
                onChange={() => setNewCommVisibility('Private')}
              />
              <div>
                <strong>Private Community</strong>
                <p>Hidden from Browse communities. Only people added by a community owner or admin can see and join.</p>
              </div>
            </label>
          </fieldset>
          <div className="nx-modal-footer">
            <Button type="button" variant="default" onClick={() => setCreateOpen(false)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!newCommName.trim()}>
              Create community
            </Button>
          </div>
        </form>
      </Modal>

      <Modal
        open={Boolean(addChannelForId)}
        onOpenChange={open => {
          if (!open) setAddChannelForId(null);
        }}
        title="Add channel"
        description="Channels inherit this community's members and roles automatically."
      >
        <form onSubmit={onCreateChannel}>
          <Field label="Channel name" htmlFor="new-ch-name">
            <input
              id="new-ch-name"
              type="text"
              required
              maxLength={120}
              placeholder="e.g. announcements, releases, qa"
              value={newChannelName}
              onChange={e => setNewChannelName(e.target.value)}
            />
          </Field>
          <Field label="Description (optional)" htmlFor="new-ch-desc">
            <textarea
              id="new-ch-desc"
              rows={2}
              maxLength={500}
              placeholder="What is this channel for?"
              value={newChannelDesc}
              onChange={e => setNewChannelDesc(e.target.value)}
            />
          </Field>
          <Field label="Posting policy" htmlFor="new-ch-policy">
            <select
              id="new-ch-policy"
              className="nx-select"
              value={newChannelPolicy}
              onChange={e => setNewChannelPolicy(e.target.value as 'Members' | 'AdminsOnly')}
            >
              <option value="Members">Members — Everyone in the community can post</option>
              <option value="AdminsOnly">Admins only — Only owners and admins can publish posts</option>
            </select>
          </Field>
          <div className="nx-modal-footer">
            <Button type="button" variant="default" onClick={() => setAddChannelForId(null)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!newChannelName.trim()}>
              Create channel
            </Button>
          </div>
        </form>
      </Modal>

      <Modal
        open={editCommOpen && Boolean(selectedCommunity)}
        onOpenChange={open => {
          if (!open && !savingMeta) setEditCommOpen(false);
        }}
        title="Edit community"
        description="Update the community name and description."
      >
        <form onSubmit={onSaveCommunityMeta}>
          <Field label="Community name" htmlFor="edit-comm-name">
            <input
              id="edit-comm-name"
              type="text"
              required
              minLength={1}
              maxLength={120}
              value={editCommName}
              onChange={e => setEditCommName(e.target.value)}
            />
          </Field>
          <Field label="Description (optional)" htmlFor="edit-comm-desc">
            <textarea
              id="edit-comm-desc"
              rows={3}
              maxLength={500}
              placeholder="Describe what this community is for…"
              value={editCommDesc}
              onChange={e => setEditCommDesc(e.target.value)}
            />
          </Field>
          <div className="nx-modal-footer">
            <Button type="button" variant="default" disabled={savingMeta} onClick={() => setEditCommOpen(false)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={savingMeta || !editCommName.trim()}>
              {savingMeta ? 'Saving…' : 'Save changes'}
            </Button>
          </div>
        </form>
      </Modal>

      <Modal
        open={editChannelOpen && Boolean(selectedSubChannel)}
        onOpenChange={open => {
          if (!open && !savingMeta) setEditChannelOpen(false);
        }}
        title="Edit channel"
        description="Update the channel name and description without changing its link or history."
      >
        <form onSubmit={onSaveChannelMeta}>
          <Field label="Channel name" htmlFor="edit-ch-name">
            <input
              id="edit-ch-name"
              type="text"
              required
              minLength={1}
              maxLength={120}
              value={editChannelName}
              onChange={e => setEditChannelName(e.target.value)}
            />
          </Field>
          <Field label="Description (optional)" htmlFor="edit-ch-desc">
            <textarea
              id="edit-ch-desc"
              rows={3}
              maxLength={500}
              placeholder="Describe what this channel is for…"
              value={editChannelDesc}
              onChange={e => setEditChannelDesc(e.target.value)}
            />
          </Field>
          <div className="nx-modal-footer">
            <Button type="button" variant="default" disabled={savingMeta} onClick={() => setEditChannelOpen(false)}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={savingMeta || !editChannelName.trim()}>
              {savingMeta ? 'Saving…' : 'Save changes'}
            </Button>
          </div>
        </form>
      </Modal>

      <Modal
        open={archiveConfirmOpen && Boolean(selectedSubChannel)}
        onOpenChange={open => {
          if (!open && !savingMeta) setArchiveConfirmOpen(false);
        }}
        title={selectedSubChannel ? `Archive #${selectedSubChannel.name}?` : 'Archive channel'}
        footer={
          <>
            <Button disabled={savingMeta} onClick={() => setArchiveConfirmOpen(false)}>
              Cancel
            </Button>
            <Button variant="danger" disabled={savingMeta} onClick={onArchiveChannel}>
              <Archive size={14} aria-hidden="true" /> {savingMeta ? 'Archiving…' : 'Archive channel'}
            </Button>
          </>
        }
      >
        <p className="nx-muted" style={{ margin: 0 }}>
          Archiving this channel makes it read-only. Its ID, past posts, comments, polls, and files remain preserved, and an owner or admin can restore it anytime.
        </p>
      </Modal>
    </>
  );
}

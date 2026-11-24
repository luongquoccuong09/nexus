import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { ChevronDown, ChevronRight, Pencil, Plus, Search, Tag, Trash2, UserMinus, UserPlus, Users, X } from 'lucide-react';
import { orgApiPath, protectedRequest } from '../scope';
import { searchPeopleScope } from '../peopleDirectory';
import { Badge, Button, Field, Modal } from '../ui';
import {
  PeopleTagDto,
  PeopleTagMemberDto,
  PeopleTagMembersPageResponse,
  PeopleTagPageResponse,
  formatTagApiError,
  slugifyTagName,
  validateTagSlug
} from './tagHelpers';

export type PeopleTagsModalScope =
  | {
      kind: 'organization';
      organizationId: string;
      organizationName: string;
    }
  | {
      kind: 'community';
      organizationId: string;
      communityId: string;
      communityName: string;
    };

interface PeopleTagsModalProps {
  open: boolean;
  onClose: () => void;
  scope: PeopleTagsModalScope | null;
}

interface EligibleMember {
  userId: string;
  username: string;
  displayName: string;
}

interface TagMembersState {
  items: PeopleTagMemberDto[];
  nextCursor: string | null;
  loading: boolean;
  error: string;
  loaded: boolean;
}

function getTagsBasePath(scope: PeopleTagsModalScope): string {
  if (scope.kind === 'organization') {
    return orgApiPath(scope.organizationId, '/api/tags');
  }
  return orgApiPath(scope.organizationId, `/api/communities/${scope.communityId}/tags`);
}

export default function PeopleTagsModal({ open, onClose, scope }: PeopleTagsModalProps) {
  const [tags, setTags] = useState<PeopleTagDto[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [canManage, setCanManage] = useState(false);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [searchInput, setSearchInput] = useState('');
  const [debouncedQuery, setDebouncedQuery] = useState('');

  // Create form state
  const [createOpen, setCreateOpen] = useState(false);
  const [createName, setCreateName] = useState('');
  const [createSlug, setCreateSlug] = useState('');
  const [createSlugTouched, setCreateSlugTouched] = useState(false);
  const [createDesc, setCreateDesc] = useState('');
  const [formError, setFormError] = useState('');

  // Edit & Delete state
  const [editingTagId, setEditingTagId] = useState<string | null>(null);
  const [editName, setEditName] = useState('');
  const [editSlug, setEditSlug] = useState('');
  const [editDesc, setEditDesc] = useState('');
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);

  // Expanded tag & member management
  const [expandedTagId, setExpandedTagId] = useState<string | null>(null);
  const [membersByTag, setMembersByTag] = useState<Record<string, TagMembersState>>({});

  // Eligible member picker for the expanded tag
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerQuery, setPickerQuery] = useState('');
  const [pickerItems, setPickerItems] = useState<EligibleMember[]>([]);
  const [pickerNext, setPickerNext] = useState<string | null>(null);
  const [pickerLoading, setPickerLoading] = useState(false);
  const [pickerError, setPickerError] = useState('');

  // Single in-flight mutation guard
  const inFlightRef = useRef(false);
  const [busyKey, setBusyKey] = useState<string | null>(null);

  const searchRef = useRef<HTMLInputElement | null>(null);

  const scopeKey = scope
    ? scope.kind === 'organization'
      ? `org:${scope.organizationId}`
      : `comm:${scope.organizationId}:${scope.communityId}`
    : '';

  // Debounce search query
  useEffect(() => {
    const timer = window.setTimeout(() => {
      setDebouncedQuery(searchInput.trim());
    }, 220);
    return () => window.clearTimeout(timer);
  }, [searchInput]);

  // Reset modal state when closed or scope changes
  useEffect(() => {
    if (!open || !scope) {
      setCreateOpen(false);
      setEditingTagId(null);
      setConfirmDeleteId(null);
      setExpandedTagId(null);
      setPickerOpen(false);
      setFormError('');
      setError('');
      setNotice('');
      return;
    }
    setSearchInput('');
    setDebouncedQuery('');
    setMembersByTag({});
    setExpandedTagId(null);
    setCreateOpen(false);
    setEditingTagId(null);
    setConfirmDeleteId(null);
    setPickerOpen(false);
    setError('');
    setNotice('');
    setFormError('');
  }, [open, scopeKey]);

  const fetchTagsPage = useCallback(
    async (cursor: string | null, append: boolean, signal?: AbortSignal) => {
      if (!scope) return;
      const base = getTagsBasePath(scope);
      const params = new URLSearchParams({ limit: '50' });
      if (debouncedQuery) params.set('q', debouncedQuery);
      if (cursor) params.set('cursor', cursor);

      if (append) setLoadingMore(true);
      else setLoading(true);
      setError('');

      try {
        const res = await window.fetch(`${base}?${params}`, { credentials: 'same-origin', signal });
        const data = (await res.json().catch(() => ({}))) as Partial<PeopleTagPageResponse> & {
          code?: string;
          message?: string;
        };
        if (!res.ok) {
          if (!signal?.aborted) {
            setError(formatTagApiError(res.status, data.code, data.message));
          }
          return;
        }
        if (signal?.aborted) return;
        const incoming = Array.isArray(data.items) ? data.items : [];
        setTags(prev => (append ? [...prev, ...incoming] : incoming));
        setNextCursor(typeof data.nextCursor === 'string' && data.nextCursor ? data.nextCursor : null);
        setCanManage(Boolean(data.canManage));
      } catch (err) {
        if ((err as Error).name !== 'AbortError') {
          setError('Unable to load tags.');
        }
      } finally {
        if (!signal?.aborted) {
          setLoading(false);
          setLoadingMore(false);
        }
      }
    },
    [scope, debouncedQuery]
  );

  useEffect(() => {
    if (!open || !scope) return;
    const controller = new AbortController();
    void fetchTagsPage(null, false, controller.signal);
    return () => controller.abort();
  }, [open, scope, fetchTagsPage]);

  const loadTagMembers = useCallback(
    async (tagId: string, cursor: string | null, append: boolean) => {
      if (!scope) return;
      const base = getTagsBasePath(scope);
      const params = new URLSearchParams({ limit: '50' });
      if (cursor) params.set('cursor', cursor);

      setMembersByTag(prev => ({
        ...prev,
        [tagId]: {
          items: prev[tagId]?.items ?? [],
          nextCursor: prev[tagId]?.nextCursor ?? null,
          loading: true,
          error: '',
          loaded: prev[tagId]?.loaded ?? false
        }
      }));

      try {
        const res = await window.fetch(`${base}/${tagId}/members?${params}`, {
          credentials: 'same-origin'
        });
        const data = (await res.json().catch(() => ({}))) as Partial<PeopleTagMembersPageResponse> & {
          code?: string;
          message?: string;
        };
        if (!res.ok) {
          setMembersByTag(prev => ({
            ...prev,
            [tagId]: {
              items: prev[tagId]?.items ?? [],
              nextCursor: prev[tagId]?.nextCursor ?? null,
              loading: false,
              error: formatTagApiError(res.status, data.code, data.message),
              loaded: true
            }
          }));
          return;
        }
        const incoming = Array.isArray(data.items) ? data.items : [];
        setMembersByTag(prev => {
          const existing = append ? (prev[tagId]?.items ?? []) : [];
          const seen = new Set(existing.map(m => m.userId));
          const merged = [...existing, ...incoming.filter(m => !seen.has(m.userId))];
          return {
            ...prev,
            [tagId]: {
              items: merged,
              nextCursor: typeof data.nextCursor === 'string' && data.nextCursor ? data.nextCursor : null,
              loading: false,
              error: '',
              loaded: true
            }
          };
        });
      } catch {
        setMembersByTag(prev => ({
          ...prev,
          [tagId]: {
            items: prev[tagId]?.items ?? [],
            nextCursor: prev[tagId]?.nextCursor ?? null,
            loading: false,
            error: 'Unable to load tag members.',
            loaded: true
          }
        }));
      }
    },
    [scope]
  );

  const handleToggleExpand = (tagId: string) => {
    if (expandedTagId === tagId) {
      setExpandedTagId(null);
      setPickerOpen(false);
      return;
    }
    setExpandedTagId(tagId);
    setPickerOpen(false);
    setPickerQuery('');
    setPickerItems([]);
    setPickerNext(null);
    setPickerError('');
    if (!membersByTag[tagId]?.loaded) {
      void loadTagMembers(tagId, null, false);
    }
  };

  // Load eligible members when picker is open
  const loadEligibleMembers = useCallback(
    async (query: string, cursor: string | null, append: boolean, signal?: AbortSignal) => {
      if (!scope) return;
      setPickerLoading(true);
      setPickerError('');
      try {
        if (scope.kind === 'organization') {
          const page = await searchPeopleScope<{ id: string; username: string; displayName: string }>(
            scope.organizationId,
            query,
            '',
            cursor,
            signal
          );
          if (signal?.aborted) return;
          const mapped: EligibleMember[] = page.items.map(p => ({
            userId: p.id,
            username: p.username,
            displayName: p.displayName
          }));
          setPickerItems(prev => {
            const base = append ? prev : [];
            const seen = new Set(base.map(m => m.userId));
            return [...base, ...mapped.filter(m => !seen.has(m.userId))];
          });
          setPickerNext(page.next);
        } else {
          const params = new URLSearchParams({ limit: '50' });
          if (cursor) params.set('cursor', cursor);
          const url = orgApiPath(scope.organizationId, `/api/communities/${scope.communityId}/members?${params}`);
          const res = await window.fetch(url, { credentials: 'same-origin', signal });
          const data = await res.json().catch(() => ({}));
          if (!res.ok) {
            if (!signal?.aborted) setPickerError(data.message || 'Unable to load community members.');
            return;
          }
          if (signal?.aborted) return;
          const rawItems = Array.isArray(data.items) ? data.items : [];
          const mapped: EligibleMember[] = rawItems.map(
            (m: { userId: string; username: string; displayName: string }) => ({
              userId: m.userId,
              username: m.username,
              displayName: m.displayName
            })
          );
          setPickerItems(prev => {
            const base = append ? prev : [];
            const seen = new Set(base.map(m => m.userId));
            return [...base, ...mapped.filter(m => !seen.has(m.userId))];
          });
          setPickerNext(typeof data.nextCursor === 'string' && data.nextCursor ? data.nextCursor : null);
        }
      } catch (err) {
        if ((err as Error).name !== 'AbortError') {
          setPickerError('Unable to load eligible members.');
        }
      } finally {
        if (!signal?.aborted) setPickerLoading(false);
      }
    },
    [scope]
  );

  useEffect(() => {
    if (!pickerOpen || !scope) return;
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      void loadEligibleMembers(pickerQuery, null, false, controller.signal);
    }, scope.kind === 'organization' ? 200 : 0);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [pickerOpen, scope, pickerQuery, loadEligibleMembers]);

  const expandedMemberIds = useMemo(() => {
    if (!expandedTagId) return new Set<string>();
    return new Set((membersByTag[expandedTagId]?.items ?? []).map(m => m.userId));
  }, [expandedTagId, membersByTag]);

  const filteredPickerCandidates = useMemo(() => {
    const q = pickerQuery.trim().toLowerCase().replace(/^@/, '');
    return pickerItems.filter(candidate => {
      if (expandedMemberIds.has(candidate.userId)) return false;
      if (!q) return true;
      return (
        candidate.displayName.toLowerCase().includes(q) ||
        candidate.username.toLowerCase().includes(q)
      );
    });
  }, [pickerItems, pickerQuery, expandedMemberIds]);

  async function handleCreateTag(e: FormEvent) {
    e.preventDefault();
    if (!scope || inFlightRef.current) return;
    const trimmedName = createName.trim();
    if (trimmedName.length < 2 || trimmedName.length > 48) {
      setFormError('Tag name must be 2–48 characters.');
      return;
    }
    const slugCheck = validateTagSlug(createSlug);
    if (!slugCheck.valid) {
      setFormError(slugCheck.error);
      return;
    }

    inFlightRef.current = true;
    setBusyKey('create');
    setFormError('');
    setNotice('');
    try {
      const res = await protectedRequest(getTagsBasePath(scope), 'POST', {
        name: trimmedName,
        slug: slugCheck.slug,
        description: createDesc.trim() || null
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setFormError(formatTagApiError(res.status, data.code, data.message));
        return;
      }
      const created = data as PeopleTagDto;
      setTags(prev => [created, ...prev.filter(t => t.id !== created.id)]);
      setCreateOpen(false);
      setCreateName('');
      setCreateSlug('');
      setCreateSlugTouched(false);
      setCreateDesc('');
      setNotice(`Tag @${created.slug} created.`);
    } catch {
      setFormError('Unable to create tag.');
    } finally {
      inFlightRef.current = false;
      setBusyKey(null);
    }
  }

  function startEditTag(tag: PeopleTagDto) {
    setEditingTagId(tag.id);
    setEditName(tag.name);
    setEditSlug(tag.slug);
    setEditDesc(tag.description ?? '');
    setConfirmDeleteId(null);
    setFormError('');
  }

  async function handleSaveEditTag(e: FormEvent, tagId: string) {
    e.preventDefault();
    if (!scope || inFlightRef.current) return;
    const trimmedName = editName.trim();
    if (trimmedName.length < 2 || trimmedName.length > 48) {
      setFormError('Tag name must be 2–48 characters.');
      return;
    }
    const slugCheck = validateTagSlug(editSlug);
    if (!slugCheck.valid) {
      setFormError(slugCheck.error);
      return;
    }

    inFlightRef.current = true;
    setBusyKey(`edit:${tagId}`);
    setFormError('');
    setNotice('');
    try {
      const res = await protectedRequest(`${getTagsBasePath(scope)}/${tagId}`, 'PUT', {
        name: trimmedName,
        slug: slugCheck.slug,
        description: editDesc.trim() || null
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setFormError(formatTagApiError(res.status, data.code, data.message));
        return;
      }
      const updated = data as PeopleTagDto;
      setTags(prev => prev.map(t => (t.id === tagId ? updated : t)));
      setEditingTagId(null);
      setNotice(`Tag @${updated.slug} updated.`);
    } catch {
      setFormError('Unable to update tag.');
    } finally {
      inFlightRef.current = false;
      setBusyKey(null);
    }
  }

  async function handleDeleteTag(tag: PeopleTagDto) {
    if (!scope || inFlightRef.current) return;
    inFlightRef.current = true;
    setBusyKey(`delete:${tag.id}`);
    setError('');
    setNotice('');
    try {
      const res = await protectedRequest(`${getTagsBasePath(scope)}/${tag.id}`, 'DELETE');
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        setError(formatTagApiError(res.status, data.code, data.message));
        return;
      }
      setTags(prev => prev.filter(t => t.id !== tag.id));
      if (expandedTagId === tag.id) setExpandedTagId(null);
      setConfirmDeleteId(null);
      setNotice(`Tag @${tag.slug} deleted.`);
    } catch {
      setError('Unable to delete tag.');
    } finally {
      inFlightRef.current = false;
      setBusyKey(null);
    }
  }

  async function handleAddMember(tagId: string, candidate: EligibleMember) {
    if (!scope || inFlightRef.current) return;
    inFlightRef.current = true;
    setBusyKey(`add:${tagId}:${candidate.userId}`);
    setError('');
    setNotice('');
    try {
      const res = await protectedRequest(`${getTagsBasePath(scope)}/${tagId}/members`, 'POST', {
        userId: candidate.userId
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setError(formatTagApiError(res.status, data.code, data.message));
        return;
      }
      const added: PeopleTagMemberDto = {
        userId: candidate.userId,
        username: candidate.username,
        displayName: candidate.displayName,
        isUnverified: Boolean(data.isUnverified),
        addedAt: typeof data.addedAt === 'string' ? data.addedAt : new Date().toISOString()
      };
      const alreadyMember = (membersByTag[tagId]?.items ?? []).some(m => m.userId === added.userId);
      setMembersByTag(prev => {
        const current = prev[tagId]?.items ?? [];
        if (current.some(m => m.userId === added.userId)) return prev;
        return {
          ...prev,
          [tagId]: {
            items: [added, ...current],
            nextCursor: prev[tagId]?.nextCursor ?? null,
            loading: false,
            error: '',
            loaded: true
          }
        };
      });
      if (!alreadyMember && data.added !== false) {
        setTags(prev =>
          prev.map(t => (t.id === tagId ? { ...t, memberCount: t.memberCount + 1 } : t))
        );
      }
      setNotice(`Added ${candidate.displayName}.`);
    } catch {
      setError('Unable to add member to tag.');
    } finally {
      inFlightRef.current = false;
      setBusyKey(null);
    }
  }

  async function handleRemoveMember(tagId: string, member: PeopleTagMemberDto) {
    if (!scope || inFlightRef.current) return;
    inFlightRef.current = true;
    setBusyKey(`remove:${tagId}:${member.userId}`);
    setError('');
    setNotice('');
    try {
      const res = await protectedRequest(
        `${getTagsBasePath(scope)}/${tagId}/members/${member.userId}`,
        'DELETE'
      );
      if (!res.ok) {
        const data = await res.json().catch(() => ({}));
        setError(formatTagApiError(res.status, data.code, data.message));
        return;
      }
      setMembersByTag(prev => {
        const current = prev[tagId]?.items ?? [];
        return {
          ...prev,
          [tagId]: {
            items: current.filter(m => m.userId !== member.userId),
            nextCursor: prev[tagId]?.nextCursor ?? null,
            loading: false,
            error: '',
            loaded: true
          }
        };
      });
      setTags(prev =>
        prev.map(t => (t.id === tagId ? { ...t, memberCount: Math.max(0, t.memberCount - 1) } : t))
      );
      setNotice(`Removed ${member.displayName}.`);
    } catch {
      setError('Unable to remove member from tag.');
    } finally {
      inFlightRef.current = false;
      setBusyKey(null);
    }
  }

  if (!scope) return null;

  const title =
    scope.kind === 'organization'
      ? `Tags · ${scope.organizationName}`
      : `Community tags · ${scope.communityName}`;
  const description =
    scope.kind === 'organization'
      ? 'Reusable groups of workspace members you can mention with @handle across communities.'
      : 'Groups of members scoped to this community for quick @handle mentions in posts.';

  return (
    <Modal
      open={open}
      onOpenChange={next => {
        if (!next) onClose();
      }}
      title={title}
      description={description}
      size="md"
      initialFocusRef={searchRef}
    >
      <div className="nx-tags-modal">
        <div className="nx-tags-toolbar">
          <div className="nx-search-input-wrap nx-tags-search">
            <Search size={14} aria-hidden="true" />
            <input
              ref={searchRef}
              type="search"
              aria-label="Search tags"
              placeholder="Filter tags by name or @handle…"
              value={searchInput}
              onChange={e => setSearchInput(e.target.value)}
            />
            {searchInput && (
              <Button
                variant="ghost"
                size="sm"
                iconOnly
                aria-label="Clear tag search"
                onClick={() => setSearchInput('')}
              >
                <X size={13} aria-hidden="true" />
              </Button>
            )}
          </div>

          {canManage && (
            <Button
              variant={createOpen ? 'default' : 'primary'}
              size="sm"
              onClick={() => {
                setCreateOpen(v => !v);
                setFormError('');
                setEditingTagId(null);
              }}
            >
              <Plus size={14} aria-hidden="true" />
              <span>{createOpen ? 'Cancel' : 'New tag'}</span>
            </Button>
          )}
        </div>

        {error && (
          <div className="nx-tags-banner error" role="alert">
            <span>{error}</span>
            <Button variant="ghost" size="sm" onClick={() => void fetchTagsPage(null, false)}>
              Retry
            </Button>
          </div>
        )}

        {notice && (
          <div className="nx-tags-banner ok" role="status">
            <span>{notice}</span>
          </div>
        )}

        {canManage && createOpen && (
          <form className="nx-tag-form" onSubmit={handleCreateTag}>
            <div className="nx-tag-form-head">
              <strong>Create tag</strong>
            </div>
            {formError && <p className="nx-error" role="alert">{formError}</p>}
            <div className="nx-tag-form-grid">
              <Field label="Name" htmlFor="nx-tag-create-name">
                <input
                  id="nx-tag-create-name"
                  type="text"
                  maxLength={48}
                  placeholder="On-Call Engineering"
                  value={createName}
                  onChange={e => {
                    const nextName = e.target.value;
                    setCreateName(nextName);
                    if (!createSlugTouched) {
                      setCreateSlug(slugifyTagName(nextName));
                    }
                  }}
                  required
                />
              </Field>
              <Field label="Mention handle (@slug)" htmlFor="nx-tag-create-slug">
                <input
                  id="nx-tag-create-slug"
                  type="text"
                  maxLength={32}
                  placeholder="on-call"
                  value={createSlug}
                  onChange={e => {
                    setCreateSlugTouched(true);
                    setCreateSlug(e.target.value.toLowerCase());
                  }}
                  required
                />
              </Field>
            </div>
            <Field label="Description (optional)" htmlFor="nx-tag-create-desc">
              <input
                id="nx-tag-create-desc"
                type="text"
                maxLength={200}
                placeholder="Primary responders for production incidents"
                value={createDesc}
                onChange={e => setCreateDesc(e.target.value)}
              />
            </Field>
            <div className="nx-tag-form-actions">
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => {
                  setCreateOpen(false);
                  setFormError('');
                }}
              >
                Cancel
              </Button>
              <Button type="submit" variant="primary" size="sm" disabled={busyKey === 'create'}>
                {busyKey === 'create' ? 'Creating…' : 'Create tag'}
              </Button>
            </div>
          </form>
        )}

        {loading && tags.length === 0 ? (
          <div className="nx-tags-state" role="status">Loading tags…</div>
        ) : tags.length === 0 ? (
          <div className="nx-tags-state">
            <Tag size={22} aria-hidden="true" />
            <p>
              {debouncedQuery
                ? `No tags match "${debouncedQuery}".`
                : 'No tags created yet.'}
            </p>
          </div>
        ) : (
          <div className="nx-tags-list">
            {tags.map(tag => {
              const isExpanded = expandedTagId === tag.id;
              const isEditing = editingTagId === tag.id;
              const isConfirmingDelete = confirmDeleteId === tag.id;
              const membersState = membersByTag[tag.id];
              const isEditable =
                canManage &&
                ((scope.kind === 'organization' && tag.scope === 'organization') ||
                  (scope.kind === 'community' && tag.scope === 'community'));

              return (
                <div className="nx-tag-card" key={tag.id}>
                  {isEditing ? (
                    <form className="nx-tag-form inline" onSubmit={e => void handleSaveEditTag(e, tag.id)}>
                      {formError && <p className="nx-error" role="alert">{formError}</p>}
                      <div className="nx-tag-form-grid">
                        <Field label="Name" htmlFor={`nx-tag-edit-name-${tag.id}`}>
                          <input
                            id={`nx-tag-edit-name-${tag.id}`}
                            type="text"
                            maxLength={48}
                            value={editName}
                            onChange={e => setEditName(e.target.value)}
                            required
                          />
                        </Field>
                        <Field label="Mention handle (@slug)" htmlFor={`nx-tag-edit-slug-${tag.id}`}>
                          <input
                            id={`nx-tag-edit-slug-${tag.id}`}
                            type="text"
                            maxLength={32}
                            value={editSlug}
                            onChange={e => setEditSlug(e.target.value.toLowerCase())}
                            required
                          />
                        </Field>
                      </div>
                      <Field label="Description (optional)" htmlFor={`nx-tag-edit-desc-${tag.id}`}>
                        <input
                          id={`nx-tag-edit-desc-${tag.id}`}
                          type="text"
                          maxLength={200}
                          value={editDesc}
                          onChange={e => setEditDesc(e.target.value)}
                        />
                      </Field>
                      <div className="nx-tag-form-actions">
                        <Button
                          type="button"
                          variant="ghost"
                          size="sm"
                          onClick={() => {
                            setEditingTagId(null);
                            setFormError('');
                          }}
                        >
                          Cancel
                        </Button>
                        <Button
                          type="submit"
                          variant="primary"
                          size="sm"
                          disabled={busyKey === `edit:${tag.id}`}
                        >
                          {busyKey === `edit:${tag.id}` ? 'Saving…' : 'Save changes'}
                        </Button>
                      </div>
                    </form>
                  ) : (
                    <div className="nx-tag-card-head">
                      <button
                        type="button"
                        className="nx-tag-expand-btn"
                        aria-expanded={isExpanded}
                        onClick={() => handleToggleExpand(tag.id)}
                      >
                        {isExpanded ? (
                          <ChevronDown size={15} aria-hidden="true" />
                        ) : (
                          <ChevronRight size={15} aria-hidden="true" />
                        )}
                        <div className="nx-tag-meta">
                          <div className="nx-tag-title-row">
                            <strong className="nx-tag-name" title={tag.name}>{tag.name}</strong>
                            <code className="nx-tag-slug">@{tag.slug}</code>
                            <Badge variant="role">
                              {tag.scope === 'organization' ? 'Organization' : 'Community'}
                            </Badge>
                            <span className="nx-muted nx-tag-count" aria-label={`${tag.memberCount} ${tag.memberCount === 1 ? 'member' : 'members'}`}>
                              <Users size={11} aria-hidden="true" /> {tag.memberCount}
                            </span>
                          </div>
                          {tag.description && (
                            <p className="nx-tag-desc" title={tag.description}>{tag.description}</p>
                          )}
                        </div>
                      </button>

                      {isEditable && (
                        <div className="nx-tag-actions">
                          <Button
                            variant="ghost"
                            size="sm"
                            iconOnly
                            aria-label={`Edit tag ${tag.name}`}
                            title="Edit tag"
                            onClick={() => startEditTag(tag)}
                          >
                            <Pencil size={14} aria-hidden="true" />
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            iconOnly
                            aria-label={`Delete tag ${tag.name}`}
                            title="Delete tag"
                            onClick={() => setConfirmDeleteId(isConfirmingDelete ? null : tag.id)}
                          >
                            <Trash2 size={14} aria-hidden="true" />
                          </Button>
                        </div>
                      )}
                    </div>
                  )}

                  {isConfirmingDelete && !isEditing && (
                    <div className="nx-tag-delete-confirm" role="alert">
                      <span>Delete <strong>@{tag.slug}</strong>? Existing posts will keep their text.</span>
                      <div className="nx-tag-form-actions">
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={() => setConfirmDeleteId(null)}
                        >
                          Cancel
                        </Button>
                        <Button
                          variant="danger"
                          size="sm"
                          disabled={busyKey === `delete:${tag.id}`}
                          onClick={() => void handleDeleteTag(tag)}
                        >
                          {busyKey === `delete:${tag.id}` ? 'Deleting…' : 'Delete tag'}
                        </Button>
                      </div>
                    </div>
                  )}

                  {isExpanded && (
                    <div className="nx-tag-members">
                      <div className="nx-tag-members-head">
                        <span className="nx-muted">
                          Members ({tag.memberCount})
                        </span>
                        {isEditable && (
                          <Button
                            variant={pickerOpen ? 'default' : 'ghost'}
                            size="sm"
                            onClick={() => setPickerOpen(v => !v)}
                          >
                            <UserPlus size={13} aria-hidden="true" />
                            <span>{pickerOpen ? 'Close picker' : 'Add member'}</span>
                          </Button>
                        )}
                      </div>

                      {isEditable && pickerOpen && (
                        <div className="nx-tag-member-picker">
                          <div className="nx-search-input-wrap">
                            <Search size={13} aria-hidden="true" />
                            <input
                              type="search"
                              aria-label={`Search eligible members to add to ${tag.name}`}
                              placeholder="Search people to add…"
                              value={pickerQuery}
                              onChange={e => setPickerQuery(e.target.value)}
                            />
                          </div>
                          {pickerError && <p className="nx-error" role="alert">{pickerError}</p>}
                          {pickerLoading && filteredPickerCandidates.length === 0 ? (
                            <div className="nx-tag-picker-empty" role="status">Searching…</div>
                          ) : filteredPickerCandidates.length === 0 ? (
                            <div className="nx-tag-picker-empty">No matching eligible members.</div>
                          ) : (
                            <div className="nx-tag-picker-list">
                              {filteredPickerCandidates.slice(0, 20).map(candidate => (
                                <div className="nx-tag-member-row" key={candidate.userId}>
                                  <div className="nx-tag-member-info">
                                    <strong title={candidate.displayName}>{candidate.displayName}</strong>
                                    <span className="nx-muted">@{candidate.username}</span>
                                  </div>
                                  <Button
                                    variant="default"
                                    size="sm"
                                    disabled={busyKey === `add:${tag.id}:${candidate.userId}`}
                                    onClick={() => void handleAddMember(tag.id, candidate)}
                                  >
                                    {busyKey === `add:${tag.id}:${candidate.userId}` ? 'Adding…' : 'Add'}
                                  </Button>
                                </div>
                              ))}
                              {pickerNext && (
                                <div className="nx-tag-load-more">
                                  <Button
                                    variant="ghost"
                                    size="sm"
                                    disabled={pickerLoading}
                                    onClick={() => void loadEligibleMembers(pickerQuery, pickerNext, true)}
                                  >
                                    {pickerLoading ? 'Loading…' : 'Load more people'}
                                  </Button>
                                </div>
                              )}
                            </div>
                          )}
                        </div>
                      )}

                      {membersState?.loading && membersState.items.length === 0 ? (
                        <div className="nx-tag-picker-empty" role="status">Loading members…</div>
                      ) : membersState?.error ? (
                        <div className="nx-tags-banner error" role="alert">
                          <span>{membersState.error}</span>
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => void loadTagMembers(tag.id, null, false)}
                          >
                            Retry
                          </Button>
                        </div>
                      ) : !membersState || membersState.items.length === 0 ? (
                        <div className="nx-tag-picker-empty">No members in this tag yet.</div>
                      ) : (
                        <div className="nx-tag-member-list">
                          {membersState.items.map(member => (
                            <div className="nx-tag-member-row" key={member.userId}>
                              <div className="nx-tag-member-info">
                                <strong title={member.displayName}>{member.displayName}</strong>
                                <span className="nx-muted">@{member.username}</span>
                                {member.isUnverified && <Badge variant="role">Unverified</Badge>}
                              </div>
                              {isEditable && (
                                <Button
                                  variant="ghost"
                                  size="sm"
                                  iconOnly
                                  aria-label={`Remove ${member.displayName} from ${tag.name}`}
                                  title="Remove member"
                                  disabled={busyKey === `remove:${tag.id}:${member.userId}`}
                                  onClick={() => void handleRemoveMember(tag.id, member)}
                                >
                                  <UserMinus size={14} aria-hidden="true" />
                                </Button>
                              )}
                            </div>
                          ))}
                          {membersState.nextCursor && (
                            <div className="nx-tag-load-more">
                              <Button
                                variant="ghost"
                                size="sm"
                                disabled={membersState.loading}
                                onClick={() => void loadTagMembers(tag.id, membersState.nextCursor, true)}
                              >
                                {membersState.loading ? 'Loading…' : 'Load more members'}
                              </Button>
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                  )}
                </div>
              );
            })}

            {nextCursor && (
              <div className="nx-tag-load-more">
                <Button
                  variant="default"
                  size="sm"
                  disabled={loadingMore}
                  onClick={() => void fetchTagsPage(nextCursor, true)}
                >
                  {loadingMore ? 'Loading…' : 'Load more tags'}
                </Button>
              </div>
            )}
          </div>
        )}
      </div>
    </Modal>
  );
}

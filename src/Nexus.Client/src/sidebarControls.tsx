import { FormEvent, useEffect, useRef, useState } from 'react';
import { ArrowDown, ArrowUp, Eye, EyeOff, FolderPlus, MoreHorizontal, Pencil, Star, Trash2 } from 'lucide-react';
import { SidebarItemPrefDto, SidebarSectionDto } from './sidebarPrefs';
import { Button, Field, Modal } from './ui';

export function SidebarItemActions({
  itemLabel,
  pref,
  sections,
  canMoveUp,
  canMoveDown,
  onToggleFavorite,
  onToggleHidden,
  onAssignSection,
  onMoveUp,
  onMoveDown
}: {
  itemLabel: string;
  pref?: SidebarItemPrefDto;
  sections: SidebarSectionDto[];
  canMoveUp: boolean;
  canMoveDown: boolean;
  onToggleFavorite: () => void;
  onToggleHidden: () => void;
  onAssignSection: (sectionId: string | null) => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
}) {
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (ev: MouseEvent) => {
      if (wrapRef.current && !wrapRef.current.contains(ev.target as Node)) setOpen(false);
    };
    const onKeyDown = (ev: KeyboardEvent) => {
      if (ev.key === 'Escape') {
        setOpen(false);
        triggerRef.current?.focus();
      }
    };
    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  const isFavorite = Boolean(pref?.isFavorite);
  const isHidden = Boolean(pref?.isHidden);
  const currentSectionId = pref?.sectionId ?? null;

  return (
    <div className="nx-row-org-wrap" ref={wrapRef}>
      <button
        ref={triggerRef}
        type="button"
        className="nx-row-org-btn"
        aria-label={`Organize ${itemLabel}`}
        aria-haspopup="menu"
        aria-expanded={open}
        title="Organize"
        onClick={e => {
          e.stopPropagation();
          setOpen(v => !v);
        }}
      >
        <MoreHorizontal size={14} aria-hidden="true" />
      </button>
      {open && (
        <div className="nx-menu-pop nx-org-menu-pop" role="menu" aria-label={`Organize ${itemLabel}`}>
          <button
            type="button"
            className="nx-menu-item"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              onToggleFavorite();
            }}
          >
            <Star size={14} aria-hidden="true" />
            <span>{isFavorite ? 'Remove favorite' : 'Add to favorites'}</span>
          </button>
          {canMoveUp && (
            <button
              type="button"
              className="nx-menu-item"
              role="menuitem"
              onClick={() => {
                setOpen(false);
                onMoveUp();
              }}
            >
              <ArrowUp size={14} aria-hidden="true" />
              <span>Move up</span>
            </button>
          )}
          {canMoveDown && (
            <button
              type="button"
              className="nx-menu-item"
              role="menuitem"
              onClick={() => {
                setOpen(false);
                onMoveDown();
              }}
            >
              <ArrowDown size={14} aria-hidden="true" />
              <span>Move down</span>
            </button>
          )}
          {sections.length > 0 && (
            <div className="nx-org-menu-group" role="group" aria-label="Move to section">
              <div className="nx-org-menu-label">Section</div>
              <button
                type="button"
                className={`nx-menu-item ${currentSectionId === null ? 'active' : ''}`}
                role="menuitem"
                onClick={() => {
                  setOpen(false);
                  onAssignSection(null);
                }}
              >
                <span>Default list</span>
              </button>
              {sections.map(sec => (
                <button
                  key={sec.id}
                  type="button"
                  className={`nx-menu-item ${currentSectionId === sec.id ? 'active' : ''}`}
                  role="menuitem"
                  onClick={() => {
                    setOpen(false);
                    onAssignSection(sec.id);
                  }}
                >
                  <span>{sec.name}</span>
                </button>
              ))}
            </div>
          )}
          <button
            type="button"
            className="nx-menu-item"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              onToggleHidden();
            }}
          >
            {isHidden ? <Eye size={14} aria-hidden="true" /> : <EyeOff size={14} aria-hidden="true" />}
            <span>{isHidden ? 'Show in sidebar' : 'Hide from sidebar'}</span>
          </button>
        </div>
      )}
    </div>
  );
}

export function SidebarSectionHeader({
  section,
  count,
  onRename,
  onDelete
}: {
  section: SidebarSectionDto;
  count: number;
  onRename: (section: SidebarSectionDto) => void;
  onDelete: (section: SidebarSectionDto) => void;
}) {
  return (
    <div className="nx-section-label nx-custom-section-head">
      <span className="nx-custom-section-name">{section.name} ({count})</span>
      <span className="nx-custom-section-actions">
        <button
          type="button"
          className="nx-section-icon-btn"
          aria-label={`Rename section ${section.name}`}
          title="Rename section"
          onClick={() => onRename(section)}
        >
          <Pencil size={12} aria-hidden="true" />
        </button>
        <button
          type="button"
          className="nx-section-icon-btn"
          aria-label={`Delete section ${section.name}`}
          title="Delete section"
          onClick={() => onDelete(section)}
        >
          <Trash2 size={12} aria-hidden="true" />
        </button>
      </span>
    </div>
  );
}

export function SidebarSectionModal({
  open,
  mode,
  initialName,
  busy,
  onClose,
  onSubmit
}: {
  open: boolean;
  mode: 'create' | 'rename';
  initialName?: string;
  busy: boolean;
  onClose: () => void;
  onSubmit: (name: string) => Promise<void>;
}) {
  const [name, setName] = useState(initialName ?? '');
  const inputRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (open) setName(initialName ?? '');
  }, [initialName, open]);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    if (!name.trim() || busy) return;
    await onSubmit(name.trim());
  }

  return (
    <Modal
      open={open}
      onOpenChange={value => { if (!value) onClose(); }}
      title={mode === 'create' ? 'New sidebar section' : 'Rename section'}
      description="Sections organize your sidebar without changing membership or notifications."
      initialFocusRef={inputRef}
    >
      <form onSubmit={e => void handleSubmit(e)}>
        <Field label="Section name" htmlFor="sidebar-section-name">
          <input
            ref={inputRef}
            id="sidebar-section-name"
            type="text"
            maxLength={80}
            required
            placeholder="e.g. Priority, Projects, Announcements"
            value={name}
            onChange={e => setName(e.currentTarget.value)}
          />
        </Field>
        <div className="nx-modal-footer">
          <Button type="button" variant="default" onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={busy || !name.trim()}>
            <FolderPlus size={14} aria-hidden="true" /> {mode === 'create' ? 'Create section' : 'Save name'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

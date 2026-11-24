import { KeyboardEvent, useEffect, useMemo, useRef, useState } from 'react';
import { Clock, Search, X } from 'lucide-react';
import {
  EMOJI_CATEGORIES,
  EMOJIS_BY_CATEGORY,
  EmojiCategoryId,
  EmojiEntry,
  SKIN_TONES,
  SkinToneId,
  applySkinTone,
  searchEmojis
} from './emojiCatalog';

const RECENTS_STORAGE_KEY = 'nexus.emoji.recents.v1';
const SKIN_TONE_STORAGE_KEY = 'nexus.emoji.skinTone.v1';
const MAX_RECENTS = 24;

type ActiveTab = 'recent' | EmojiCategoryId;

export interface EmojiPickerProps {
  onSelect: (emoji: string) => void;
  onClose: () => void;
}

function loadRecents(): string[] {
  try {
    const raw = window.localStorage.getItem(RECENTS_STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter((item): item is string => typeof item === 'string' && item.length > 0).slice(0, MAX_RECENTS);
  } catch {
    return [];
  }
}

function saveRecents(recents: string[]): void {
  try {
    window.localStorage.setItem(RECENTS_STORAGE_KEY, JSON.stringify(recents.slice(0, MAX_RECENTS)));
  } catch {
    // Ignore restricted/private storage errors.
  }
}

function loadSkinTone(): SkinToneId {
  try {
    const raw = window.localStorage.getItem(SKIN_TONE_STORAGE_KEY);
    if (raw && SKIN_TONES.some(option => option.id === raw)) {
      return raw as SkinToneId;
    }
  } catch {
    // Ignore restricted/private storage errors.
  }
  return 'default';
}

function saveSkinTone(tone: SkinToneId): void {
  try {
    window.localStorage.setItem(SKIN_TONE_STORAGE_KEY, tone);
  } catch {
    // Ignore restricted/private storage errors.
  }
}

export default function EmojiPicker({ onSelect, onClose }: EmojiPickerProps) {
  const [recents, setRecents] = useState<string[]>(() => loadRecents());
  const [skinTone, setSkinTone] = useState<SkinToneId>(() => loadSkinTone());
  const [activeTab, setActiveTab] = useState<ActiveTab>(() => (loadRecents().length > 0 ? 'recent' : 'smileys'));
  const [query, setQuery] = useState('');
  const [hoveredLabel, setHoveredLabel] = useState('');

  const searchInputRef = useRef<HTMLInputElement | null>(null);
  const gridRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    searchInputRef.current?.focus();
  }, []);

  const trimmedQuery = query.trim();
  const searchResults = useMemo(
    () => (trimmedQuery ? searchEmojis(trimmedQuery) : []),
    [trimmedQuery]
  );

  const visibleEntries = useMemo<readonly EmojiEntry[]>(() => {
    if (trimmedQuery) return searchResults;
    if (activeTab === 'recent') return [];
    return EMOJIS_BY_CATEGORY[activeTab];
  }, [activeTab, searchResults, trimmedQuery]);

  const activeCategoryLabel = useMemo(() => {
    if (trimmedQuery) return `Search results (${searchResults.length})`;
    if (activeTab === 'recent') return 'Recently used';
    return EMOJI_CATEGORIES.find(c => c.id === activeTab)?.label ?? 'Emoji';
  }, [activeTab, searchResults.length, trimmedQuery]);

  function handlePickEmoji(renderedEmoji: string) {
    const nextRecents = [renderedEmoji, ...recents.filter(item => item !== renderedEmoji)].slice(0, MAX_RECENTS);
    setRecents(nextRecents);
    saveRecents(nextRecents);
    onSelect(renderedEmoji);
  }

  function handleSkinToneChange(nextTone: SkinToneId) {
    setSkinTone(nextTone);
    saveSkinTone(nextTone);
  }

  function handleRootKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') {
      e.preventDefault();
      e.stopPropagation();
      onClose();
    }
  }

  function handleSearchKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.nativeEvent.isComposing || e.nativeEvent.keyCode === 229) {
      return;
    }
    if (e.key === 'Enter') {
      e.preventDefault();
      e.stopPropagation();
      if (trimmedQuery && searchResults.length > 0) {
        const first = searchResults[0];
        handlePickEmoji(applySkinTone(first.emoji, skinTone, first.toneCapable));
      }
    }
  }

  function selectTab(nextTab: ActiveTab) {
    setQuery('');
    setActiveTab(nextTab);
    if (gridRef.current) {
      gridRef.current.scrollTop = 0;
    }
  }

  return (
    <div
      className="nx-emoji-picker"
      role="dialog"
      aria-label="Emoji picker"
      onKeyDown={handleRootKeyDown}
    >
      <div className="nx-emoji-search-row">
        <Search size={14} className="nx-emoji-search-icon" aria-hidden="true" />
        <input
          ref={searchInputRef}
          type="search"
          className="nx-emoji-search-input"
          aria-label="Search emoji"
          placeholder="Search emoji…"
          value={query}
          onChange={e => setQuery(e.target.value)}
          onKeyDown={handleSearchKeyDown}
        />
        {query && (
          <button
            type="button"
            className="nx-emoji-clear-btn"
            aria-label="Clear emoji search"
            onClick={() => {
              setQuery('');
              searchInputRef.current?.focus();
            }}
          >
            <X size={13} aria-hidden="true" />
          </button>
        )}
      </div>

      <div className="nx-emoji-tabs" role="tablist" aria-label="Emoji categories">
        <button
          type="button"
          role="tab"
          aria-selected={!trimmedQuery && activeTab === 'recent'}
          aria-label="Recently used"
          title="Recently used"
          className={`nx-emoji-tab ${!trimmedQuery && activeTab === 'recent' ? 'active' : ''}`}
          onClick={() => selectTab('recent')}
        >
          <Clock size={14} aria-hidden="true" />
        </button>
        {EMOJI_CATEGORIES.map(cat => {
          const isSelected = !trimmedQuery && activeTab === cat.id;
          return (
            <button
              key={cat.id}
              type="button"
              role="tab"
              aria-selected={isSelected}
              aria-label={cat.label}
              title={cat.label}
              className={`nx-emoji-tab ${isSelected ? 'active' : ''}`}
              onClick={() => selectTab(cat.id)}
            >
              <span aria-hidden="true">{cat.icon}</span>
            </button>
          );
        })}
      </div>

      <div className="nx-emoji-section-title">{activeCategoryLabel}</div>

      <div className="nx-emoji-grid-scroll" ref={gridRef} role="tabpanel" aria-label={activeCategoryLabel}>
        {!trimmedQuery && activeTab === 'recent' ? (
          recents.length === 0 ? (
            <div className="nx-emoji-empty">No recently used emoji yet.</div>
          ) : (
            <div className="nx-emoji-grid">
              {recents.map(emoji => (
                <button
                  key={emoji}
                  type="button"
                  className="nx-emoji-cell"
                  aria-label={`Insert ${emoji}`}
                  title={emoji}
                  onMouseEnter={() => setHoveredLabel(emoji)}
                  onFocus={() => setHoveredLabel(emoji)}
                  onClick={() => handlePickEmoji(emoji)}
                >
                  {emoji}
                </button>
              ))}
            </div>
          )
        ) : visibleEntries.length === 0 ? (
          <div className="nx-emoji-empty">No matching emoji.</div>
        ) : (
          <div className="nx-emoji-grid">
            {visibleEntries.map(entry => {
              const rendered = applySkinTone(entry.emoji, skinTone, entry.toneCapable);
              return (
                <button
                  key={`${entry.category}-${entry.emoji}`}
                  type="button"
                  className="nx-emoji-cell"
                  aria-label={entry.name}
                  title={entry.name}
                  onMouseEnter={() => setHoveredLabel(entry.name)}
                  onFocus={() => setHoveredLabel(entry.name)}
                  onClick={() => handlePickEmoji(rendered)}
                >
                  {rendered}
                </button>
              );
            })}
          </div>
        )}
      </div>

      <div className="nx-emoji-footer">
        <span className="nx-emoji-preview-label">{hoveredLabel || 'Skin tone'}</span>
        <div className="nx-emoji-tones" role="radiogroup" aria-label="Skin tone">
          {SKIN_TONES.map(option => {
            const active = skinTone === option.id;
            return (
              <button
                key={option.id}
                type="button"
                role="radio"
                aria-checked={active}
                aria-label={option.label}
                title={option.label}
                className={`nx-emoji-tone-btn ${active ? 'active' : ''}`}
                onClick={() => handleSkinToneChange(option.id)}
              >
                <span aria-hidden="true">{option.preview}</span>
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}

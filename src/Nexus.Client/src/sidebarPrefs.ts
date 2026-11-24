import { useCallback, useEffect, useMemo, useState } from 'react';
import { protectedRequest } from './scope';
export { moveIdByOffset, reorderIdList } from './sidebarOrder';

export type SidebarSurface = 'chat' | 'communities';

export interface SidebarSectionDto {
  id: string;
  surface: SidebarSurface;
  name: string;
  sortOrder: number;
}

export interface SidebarItemPrefDto {
  targetId: string;
  surface: SidebarSurface;
  isFavorite: boolean;
  sectionId: string | null;
  isHidden: boolean;
  sortOrder: number;
}

export interface SidebarStateDto {
  surface: SidebarSurface;
  sections: SidebarSectionDto[];
  items: SidebarItemPrefDto[];
}

export function useSidebarOrganization(surface: SidebarSurface, enabled: boolean, accountKey: string) {
  const [sections, setSections] = useState<SidebarSectionDto[]>([]);
  const [items, setItems] = useState<SidebarItemPrefDto[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    if (!enabled) return;
    const res = await fetch(`/api/sidebar?surface=${surface}`, { credentials: 'same-origin', signal });
    if (!res.ok) return;
    const data = await res.json() as SidebarStateDto;
    if (signal?.aborted) return;
    setSections(data.sections ?? []);
    setItems(data.items ?? []);
  }, [enabled, surface]);

  useEffect(() => {
    setSections([]);
    setItems([]);
    setError('');
    if (!enabled) return;
    const controller = new AbortController();
    void load(controller.signal).catch(() => {});
    return () => controller.abort();
  }, [accountKey, enabled, load]);

  const itemByTargetId = useMemo(
    () => new Map(items.map(item => [item.targetId, item])),
    [items]
  );

  const createSection = useCallback(async (name: string): Promise<SidebarSectionDto | null> => {
    const trimmed = name.trim();
    if (!trimmed) return null;
    setBusy(true);
    setError('');
    try {
      const res = await protectedRequest('/api/sidebar/sections', 'POST', { surface, name: trimmed });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setError(data.message || 'Unable to create section.');
        return null;
      }
      const created = data as SidebarSectionDto;
      setSections(prev => [...prev, created].sort((a, b) => a.sortOrder - b.sortOrder));
      return created;
    } catch {
      setError('Unable to create section.');
      return null;
    } finally {
      setBusy(false);
    }
  }, [surface]);

  const renameSection = useCallback(async (sectionId: string, name: string): Promise<boolean> => {
    const trimmed = name.trim();
    if (!trimmed) return false;
    setBusy(true);
    setError('');
    try {
      const res = await protectedRequest(`/api/sidebar/sections/${sectionId}`, 'PUT', { name: trimmed });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setError(data.message || 'Unable to rename section.');
        return false;
      }
      setSections(prev => prev.map(s => s.id === sectionId ? { ...s, name: data.name ?? trimmed } : s));
      return true;
    } catch {
      setError('Unable to rename section.');
      return false;
    } finally {
      setBusy(false);
    }
  }, []);

  const deleteSection = useCallback(async (sectionId: string): Promise<boolean> => {
    setBusy(true);
    setError('');
    try {
      const res = await protectedRequest(`/api/sidebar/sections/${sectionId}`, 'DELETE');
      if (!res.ok) {
        setError('Unable to delete section.');
        return false;
      }
      setSections(prev => prev.filter(s => s.id !== sectionId));
      setItems(prev => prev.map(item => item.sectionId === sectionId ? { ...item, sectionId: null } : item));
      return true;
    } catch {
      setError('Unable to delete section.');
      return false;
    } finally {
      setBusy(false);
    }
  }, []);

  const updateItem = useCallback(async (
    targetId: string,
    patch: { isFavorite?: boolean; sectionId?: string | null; clearSection?: boolean; isHidden?: boolean; sortOrder?: number }
  ): Promise<SidebarItemPrefDto | null> => {
    setError('');
    try {
      const res = await protectedRequest(`/api/sidebar/items/${targetId}`, 'PUT', {
        surface,
        isFavorite: patch.isFavorite,
        sectionId: patch.clearSection ? null : patch.sectionId ?? undefined,
        clearSection: Boolean(patch.clearSection),
        isHidden: patch.isHidden,
        sortOrder: patch.sortOrder
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        setError(data.message || 'Unable to update sidebar preference.');
        return null;
      }
      const updated = data as SidebarItemPrefDto;
      setItems(prev => {
        const rest = prev.filter(i => i.targetId !== targetId);
        return [...rest, updated].sort((a, b) => a.sortOrder - b.sortOrder);
      });
      return updated;
    } catch {
      setError('Unable to update sidebar preference.');
      return null;
    }
  }, [surface]);

  const persistReorder = useCallback(async (orderedTargetIds: string[], orderedSectionIds?: string[]) => {
    setItems(prev => {
      const map = new Map(prev.map(i => [i.targetId, i]));
      orderedTargetIds.forEach((id, idx) => {
        const existing = map.get(id);
        map.set(id, existing
          ? { ...existing, sortOrder: idx }
          : { targetId: id, surface, isFavorite: false, sectionId: null, isHidden: false, sortOrder: idx });
      });
      return [...map.values()].sort((a, b) => a.sortOrder - b.sortOrder);
    });
    if (orderedSectionIds) {
      setSections(prev => {
        const map = new Map(prev.map(s => [s.id, s]));
        return orderedSectionIds
          .map((id, idx) => {
            const sec = map.get(id);
            return sec ? { ...sec, sortOrder: idx } : null;
          })
          .filter((s): s is SidebarSectionDto => s !== null);
      });
    }
    await protectedRequest('/api/sidebar/reorder', 'PUT', {
      surface,
      orderedTargetIds,
      orderedSectionIds
    }).catch(() => {});
  }, [surface]);

  return {
    sections,
    items,
    itemByTargetId,
    error,
    setError,
    busy,
    refresh: load,
    createSection,
    renameSection,
    deleteSection,
    updateItem,
    persistReorder
  };
}

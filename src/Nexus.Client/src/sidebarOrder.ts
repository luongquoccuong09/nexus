export function reorderIdList(ids: string[], fromId: string, toId: string): string[] {
  const fromIdx = ids.indexOf(fromId);
  const toIdx = ids.indexOf(toId);
  if (fromIdx === -1 || toIdx === -1 || fromIdx === toIdx) return ids;
  const copy = [...ids];
  const [moved] = copy.splice(fromIdx, 1);
  copy.splice(toIdx, 0, moved);
  return copy;
}

export function moveIdByOffset(ids: string[], targetId: string, delta: -1 | 1): string[] {
  const idx = ids.indexOf(targetId);
  const nextIdx = idx + delta;
  if (idx === -1 || nextIdx < 0 || nextIdx >= ids.length) return ids;
  return reorderIdList(ids, targetId, ids[nextIdx]);
}

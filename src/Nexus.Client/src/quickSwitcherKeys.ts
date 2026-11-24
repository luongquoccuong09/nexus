type CompositionKey = Pick<KeyboardEvent, 'isComposing' | 'keyCode'>;

export function isComposingKey(event: CompositionKey, composing: boolean) {
  return composing || event.isComposing || event.keyCode === 229;
}

export function isQuickSwitcherShortcut(event: Pick<KeyboardEvent,
  'key' | 'ctrlKey' | 'metaKey' | 'altKey' | 'shiftKey' | 'repeat' | 'defaultPrevented'> & CompositionKey, composing: boolean) {
  return !event.defaultPrevented && !event.repeat && !isComposingKey(event, composing) &&
    !event.altKey && !event.shiftKey && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k';
}

export function stepQuickSwitcherSelection(current: number, length: number, direction: 1 | -1) {
  if (length === 0) return -1;
  if (current < 0) return direction === 1 ? 0 : length - 1;
  return (current + direction + length) % length;
}

export function shouldSendOnEnter(
  event: Pick<KeyboardEvent, 'key' | 'shiftKey' | 'isComposing' | 'keyCode'>,
  compositionActive: boolean
): boolean {
  // Some IMEs report 229 on the key that completes composition.
  return event.key === 'Enter' && !event.shiftKey && !compositionActive && !event.isComposing && event.keyCode !== 229;
}

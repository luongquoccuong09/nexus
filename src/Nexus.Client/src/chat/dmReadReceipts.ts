export interface DmReadReceipt {
  version: number;
  peerLastReadSeq?: number;
  peerLastReadAt?: string;
}

export interface DmReadReceiptPreference {
  enabled: boolean;
  version: number;
}

export function mergeDmReadReceipt(current: DmReadReceipt | undefined, incoming: DmReadReceipt): DmReadReceipt {
  if (!current || incoming.version > current.version) return incoming;
  if (incoming.version < current.version) return current;
  if (incoming.peerLastReadSeq === undefined) return incoming;
  if (current.peerLastReadSeq === undefined || current.peerLastReadSeq > incoming.peerLastReadSeq) return current;
  return incoming;
}

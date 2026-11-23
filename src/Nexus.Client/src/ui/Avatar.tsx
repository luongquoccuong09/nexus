export function getInitials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

export function Avatar({
  displayName,
  avatarUrl,
  cacheKey,
  size = 'md',
  variant = 'user',
  presence,
  onRail = false
}: {
  displayName: string;
  avatarUrl?: string | null;
  cacheKey?: number;
  size?: 'sm' | 'md' | 'lg';
  variant?: 'user' | 'group';
  presence?: 'online' | 'offline';
  onRail?: boolean;
}) {
  const cls = [
    'nx-avatar',
    size === 'sm' ? 'sm' : size === 'lg' ? 'lg' : '',
    variant === 'group' ? 'group' : ''
  ]
    .filter(Boolean)
    .join(' ');

  const node = avatarUrl ? (
    <img
      className={cls}
      src={cacheKey ? `${avatarUrl}?v=${cacheKey}` : avatarUrl}
      alt={`${displayName} avatar`}
    />
  ) : (
    <span className={cls} aria-hidden="true">
      {getInitials(displayName)}
    </span>
  );

  if (!presence) {
    return node;
  }

  return (
    <span className="nx-avatar-wrap">
      {node}
      <span className={`nx-avatar-dot nx-dot ${presence} ${onRail ? 'on-rail' : ''}`} />
    </span>
  );
}

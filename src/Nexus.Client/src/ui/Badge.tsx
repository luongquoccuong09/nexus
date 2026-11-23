import { ReactNode } from 'react';

export function Badge({
  variant,
  children,
  ariaLabel
}: {
  variant: 'unverified' | 'verified' | 'role' | 'warning' | 'success';
  children: ReactNode;
  ariaLabel?: string;
}) {
  return (
    <span className={`nx-badge ${variant}`} aria-label={ariaLabel}>
      {children}
    </span>
  );
}

/**
 * Enforces P0-D13 / M1.4b trust visibility wherever a user's display name appears.
 */
export function TrustBadge({ isUnverified, showVerified = true }: { isUnverified: boolean; showVerified?: boolean }) {
  if (isUnverified) {
    return (
      <Badge variant="unverified" ariaLabel="Unverified account">
        Unverified
      </Badge>
    );
  }
  if (!showVerified) return null;
  return (
    <Badge variant="verified" ariaLabel="Verified account">
      Verified
    </Badge>
  );
}

export function UserNameWithBadge({
  user,
  showVerified = true
}: {
  user: { displayName: string; isUnverified: boolean };
  showVerified?: boolean;
}) {
  return (
    <span className="nx-user-name-wrap">
      <strong>{user.displayName}</strong>
      <TrustBadge isUnverified={user.isUnverified} showVerified={showVerified} />
    </span>
  );
}

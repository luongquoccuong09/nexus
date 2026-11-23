import { ReactNode } from 'react';

export function Badge({
  variant,
  children,
  ariaLabel
}: {
  variant: 'role' | 'success';
  children: ReactNode;
  ariaLabel?: string;
}) {
  return (
    <span className={`nx-badge ${variant}`} aria-label={ariaLabel}>
      {children}
    </span>
  );
}

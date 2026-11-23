import { ReactNode } from 'react';

export function Card({
  title,
  children,
  className = ''
}: {
  title?: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={`nx-card ${className}`.trim()}>
      {title && <h3>{title}</h3>}
      {children}
    </section>
  );
}

export function Field({
  label,
  htmlFor,
  compact = false,
  children
}: {
  label: string;
  htmlFor?: string;
  compact?: boolean;
  children: ReactNode;
}) {
  return (
    <div className={`nx-field ${compact ? 'compact' : ''}`.trim()}>
      <label htmlFor={htmlFor}>{label}</label>
      {children}
    </div>
  );
}

export function EmptyState({
  icon,
  title,
  description,
  actions
}: {
  icon?: ReactNode;
  title: string;
  description: string;
  actions?: ReactNode;
}) {
  return (
    <div className="nx-empty-stage">
      <div className="nx-empty-stage-card">
        {icon}
        <h3>{title}</h3>
        <p>{description}</p>
        {actions && <div className="nx-empty-actions">{actions}</div>}
      </div>
    </div>
  );
}

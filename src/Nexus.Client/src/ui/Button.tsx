import { ButtonHTMLAttributes } from 'react';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'default' | 'primary' | 'ghost' | 'danger';
  size?: 'sm' | 'md';
  iconOnly?: boolean;
  fullWidth?: boolean;
}

export function Button({
  variant = 'default',
  size = 'md',
  iconOnly = false,
  fullWidth = false,
  className = '',
  type = 'button',
  ...rest
}: ButtonProps) {
  const classes = [
    'nx-btn',
    variant !== 'default' ? variant : '',
    size === 'sm' ? 'sm' : '',
    iconOnly ? 'icon-only' : '',
    fullWidth ? 'full' : '',
    className
  ]
    .filter(Boolean)
    .join(' ');

  return <button type={type} className={classes} {...rest} />;
}

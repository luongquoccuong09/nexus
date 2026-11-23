export function ToastBar({
  variant,
  message,
  onDismiss,
  rounded = false
}: {
  variant: 'error' | 'ok' | 'info';
  message: string;
  onDismiss?: () => void;
  rounded?: boolean;
}) {
  return (
    <div
      className={`nx-toast-bar ${variant} ${rounded ? 'rounded' : ''}`}
      role={variant === 'error' ? 'alert' : 'status'}
    >
      <span>{message}</span>
      {onDismiss && (
        <button type="button" onClick={onDismiss} aria-label="Dismiss notification">
          ×
        </button>
      )}
    </div>
  );
}

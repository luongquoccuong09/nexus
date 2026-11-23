import { ReactNode } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { X } from 'lucide-react';
import { Button } from './Button';

/**
 * Accessible focus-trapped dialog powered by Radix UI Dialog and styled exclusively with Nexus tokens.
 */
export function Modal({
  open,
  onOpenChange,
  title,
  description,
  children,
  footer
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  children: ReactNode;
  footer?: ReactNode;
}) {
  return (
    <Dialog.Root open={open} onOpenChange={onOpenChange}>
      <Dialog.Portal>
        <Dialog.Overlay className="nx-modal-overlay" />
        <Dialog.Content className="nx-modal-content">
          <div className="nx-modal-head">
            <Dialog.Title asChild>
              <h3>{title}</h3>
            </Dialog.Title>
            <Dialog.Close asChild>
              <Button variant="ghost" size="sm" iconOnly aria-label="Close dialog">
                <X size={16} aria-hidden="true" />
              </Button>
            </Dialog.Close>
          </div>
          {description && (
            <Dialog.Description className="nx-muted" style={{ padding: '10px 20px 0', margin: 0 }}>
              {description}
            </Dialog.Description>
          )}
          <div className="nx-modal-body">{children}</div>
          {footer && <div className="nx-modal-foot">{footer}</div>}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

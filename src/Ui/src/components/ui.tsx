import { AnimatePresence, motion } from 'framer-motion';
import { Check, CheckCircle2, CircleAlert, Info, X } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { useEffect, useId, useRef } from 'react';
import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';

export function Toggle({ checked, onChange, label, disabled = false, describedBy }: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  disabled?: boolean;
  describedBy?: string;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      aria-describedby={describedBy}
      className={`toggle ${checked ? 'is-on' : ''}`}
      onClick={() => onChange(!checked)}
      disabled={disabled}
    >
      <motion.span animate={{ x: checked ? 16 : 0 }} transition={{ type: 'spring', stiffness: 600, damping: 35 }} />
    </button>
  );
}

export function ToggleRow({ icon: Icon, title, description, checked, onChange, disabled }: {
  icon?: LucideIcon;
  title: string;
  description: string;
  checked: boolean;
  onChange: (value: boolean) => void;
  disabled?: boolean;
}) {
  const descriptionId = useId();
  return (
    <div className="toggle-row">
      {Icon && <Icon size={18} className="row-icon" strokeWidth={1.6} />}
      <div className="toggle-row-copy">
        <span className="row-title">{title}</span>
        <p id={descriptionId}>{description}</p>
      </div>
      <Toggle checked={checked} onChange={onChange} label={title} disabled={disabled} describedBy={descriptionId} />
    </div>
  );
}

export function PageHeading({ title, description, children }: { title: string; description: string; children?: ReactNode }) {
  return (
    <div className="page-heading">
      <div><h1>{title}</h1><p>{description}</p></div>
      {children && <div className="page-heading-actions">{children}</div>}
    </div>
  );
}

export function Modal({ title, description, children, footer, onClose, icon: Icon, wide = false }: {
  title: string;
  description?: string;
  children: ReactNode;
  footer?: ReactNode;
  onClose: () => void;
  icon?: LucideIcon;
  wide?: boolean;
}) {
  const titleId = useId();
  const descriptionId = useId();
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef(onClose);
  closeRef.current = onClose;

  useEffect(() => {
    const previousFocus = document.activeElement as HTMLElement | null;
    const previousOverflow = document.body.style.overflow;
    const appRoot = document.getElementById('root');
    const wasInert = appRoot?.inert ?? false;
    if (appRoot) appRoot.inert = true;
    document.body.style.overflow = 'hidden';
    const frame = requestAnimationFrame(() => {
      const preferred = dialogRef.current?.querySelector<HTMLElement>('[data-autofocus]');
      (preferred ?? dialogRef.current)?.focus();
    });
    const handleKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { event.preventDefault(); closeRef.current(); }
      if (event.key !== 'Tab') return;
      const elements = Array.from(dialogRef.current?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), [tabindex="0"]',
      ) ?? []).filter((element) => element.getClientRects().length > 0);
      const first = elements[0];
      const last = elements[elements.length - 1];
      if (!first) { event.preventDefault(); return; }
      if (document.activeElement === dialogRef.current || !dialogRef.current?.contains(document.activeElement)) {
        event.preventDefault(); (event.shiftKey ? last : first).focus();
      } else if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    document.addEventListener('keydown', handleKey);
    return () => {
      cancelAnimationFrame(frame);
      document.body.style.overflow = previousOverflow;
      if (appRoot) appRoot.inert = wasInert;
      document.removeEventListener('keydown', handleKey);
      if (previousFocus?.isConnected && !previousFocus.matches(':disabled')) previousFocus.focus();
      else document.getElementById('main-content')?.focus({ preventScroll: true });
    };
  }, []);

  return createPortal(
    <motion.div className="modal-backdrop" initial={{ opacity: 0 }} animate={{ opacity: 1 }} exit={{ opacity: 0 }}
      onClick={(event) => { if (event.target === event.currentTarget) onClose(); }}>
      <motion.div ref={dialogRef} tabIndex={-1} role="dialog" aria-modal="true" aria-labelledby={titleId}
        aria-describedby={description ? descriptionId : undefined}
        className={`modal ${wide ? 'modal-wide' : ''}`}
        initial={{ opacity: 0, y: 16, scale: 0.98 }} animate={{ opacity: 1, y: 0, scale: 1 }}
        exit={{ opacity: 0, y: 8, scale: 0.99 }} transition={{ duration: 0.2 }}>
        <div className="modal-header">
          {Icon && <div className="modal-icon"><Icon size={22} strokeWidth={1.6} /></div>}
          <button className="icon-button modal-close" onClick={onClose} aria-label="关闭对话框"><X size={18} /></button>
          <h2 id={titleId}>{title}</h2>
          {description && <p id={descriptionId}>{description}</p>}
        </div>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-footer">{footer}</div>}
      </motion.div>
    </motion.div>, document.body,
  );
}

export interface ToastItem { id: number; title: string; description?: string; type: 'success' | 'info' | 'error' }

function Toast({ item, onDismiss }: { item: ToastItem; onDismiss: (id: number) => void }) {
  useEffect(() => {
    const timer = window.setTimeout(() => onDismiss(item.id), item.type === 'error' ? 7000 : 4200);
    return () => clearTimeout(timer);
  }, [item.id, item.type, onDismiss]);
  const Icon = item.type === 'success' ? CheckCircle2 : item.type === 'error' ? CircleAlert : Info;
  return (
    <motion.div layout className={`toast toast-${item.type}`} initial={{ opacity: 0, y: 20, scale: 0.97 }}
      animate={{ opacity: 1, y: 0, scale: 1 }} exit={{ opacity: 0, x: 30 }}>
      <Icon size={19} className="toast-icon" />
      <div><strong>{item.title}</strong>{item.description && <p>{item.description}</p>}</div>
      <button className="icon-button" aria-label="关闭通知" onClick={() => onDismiss(item.id)}><X size={15} /></button>
    </motion.div>
  );
}

export function Toasts({ items, onDismiss }: { items: ToastItem[]; onDismiss: (id: number) => void }) {
  return <div className="toast-container" aria-live="polite" aria-atomic="false"><AnimatePresence>{items.map((item) => <Toast key={item.id} item={item} onDismiss={onDismiss} />)}</AnimatePresence></div>;
}

export function Checkbox({ checked, onChange, children }: { checked: boolean; onChange: (value: boolean) => void; children: ReactNode }) {
  return (
    <label className="checkbox-label">
      <input type="checkbox" checked={checked} onChange={(event) => onChange(event.target.checked)} />
      <span className="checkbox-visual">{checked && <Check size={12} strokeWidth={3} />}</span>
      <span>{children}</span>
    </label>
  );
}
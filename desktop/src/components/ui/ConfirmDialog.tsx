import { useEffect, useRef, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ActionButton } from "./ActionButton";

/**
 * A modal confirmation built on the native <dialog>: the browser traps focus inside it, makes the rest of
 * the page inert, closes it on Escape, and returns focus to the control that opened it. Focus starts on
 * Cancel, so an accidental Enter never confirms a destructive action.
 */
export function ConfirmDialog({
  open,
  title,
  children,
  confirmLabel,
  destructive = false,
  busy = false,
  onConfirm,
  onCancel,
}: {
  open: boolean;
  title: string;
  children: ReactNode;
  confirmLabel: string;
  destructive?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const { t } = useTranslation();
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={ref}
      className="modal"
      aria-labelledby="confirm-title"
      onCancel={(e) => {
        e.preventDefault(); // Escape: let the owner decide (it may be busy)
        if (!busy) onCancel();
      }}
    >
      {open && (
        <div className="flex flex-col gap-4">
          <h2 id="confirm-title" className="text-base font-semibold">
            {title}
          </h2>
          <div className="flex flex-col gap-2 text-sm text-ink-secondary">{children}</div>
          <div className="flex flex-wrap justify-end gap-2">
            <ActionButton className="btn" autoFocus onClick={onCancel} inactive={busy}>
              {t("common.cancel")}
            </ActionButton>
            <ActionButton className={`btn ${destructive ? "btn-danger" : "btn-primary"}`} onClick={onConfirm} inactive={busy}>
              {confirmLabel}
            </ActionButton>
          </div>
        </div>
      )}
    </dialog>
  );
}

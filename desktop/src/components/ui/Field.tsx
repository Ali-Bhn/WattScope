import { useId, type ReactNode } from "react";

export interface FieldControlProps {
  id: string;
  "aria-describedby": string | undefined;
  "aria-invalid": boolean | undefined;
}

/**
 * A labelled form control with a unit, an explanation and a validation message, all wired for assistive
 * technology (label for/id, aria-describedby, aria-invalid). The control itself is passed as a render
 * function so any <input>/<select> can be used.
 */
export function Field({
  label,
  unit,
  help,
  error,
  children,
}: {
  label: string;
  unit?: string;
  help?: ReactNode;
  error?: string | null;
  children: (props: FieldControlProps) => ReactNode;
}) {
  const id = useId();
  const helpId = `${id}-help`;
  const errorId = `${id}-error`;
  const describedBy = [help ? helpId : null, error ? errorId : null].filter(Boolean).join(" ") || undefined;

  return (
    <div className="flex min-w-0 flex-col gap-1">
      <label htmlFor={id} className="label">
        {label}
      </label>
      <div className="flex items-center gap-2">
        {children({ id, "aria-describedby": describedBy, "aria-invalid": error ? true : undefined })}
        {unit && (
          <span dir="ltr" className="text-sm text-ink-secondary">
            {unit}
          </span>
        )}
      </div>
      {help && (
        <p id={helpId} className="max-w-prose text-xs text-ink-muted">
          {help}
        </p>
      )}
      {error && (
        <p id={errorId} role="alert" className="text-xs text-red-300">
          <span aria-hidden="true">✕ </span>
          {error}
        </p>
      )}
    </div>
  );
}

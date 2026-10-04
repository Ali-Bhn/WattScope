import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ActionButton } from "@/components/ui/ActionButton";
import { Field } from "@/components/ui/Field";
import { ErrorPanel } from "@/components/ui/States";
import { validationKey, validationParams, type SpecName } from "@/lib/validation";
import type { NumberSetting } from "./useNumberSetting";

/** One of the Settings groups (Monitoring, Energy estimation, ...): a titled card other pages can scroll to. */
export function SettingsGroup({ id, title, description, children }: { id: string; title: string; description?: string; children: ReactNode }) {
  return (
    <section id={`settings-${id}`} aria-labelledby={`settings-${id}-title`} className="card flex scroll-mt-4 flex-col gap-4">
      <div className="flex flex-col gap-1">
        <h2 id={`settings-${id}-title`} tabIndex={-1} className="text-base font-semibold">
          {title}
        </h2>
        {description && <p className="max-w-prose text-sm text-ink-secondary">{description}</p>}
      </div>
      <div className="flex flex-col gap-5 divide-y divide-surface-border/60 [&>*:not(:first-child)]:pt-5">{children}</div>
    </section>
  );
}

/** A single setting (or closely related set) inside a group. */
export function SettingBlock({ title, children }: { title?: string; children: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-col gap-3">
      {title && <h3 className="text-sm font-semibold">{title}</h3>}
      {children}
    </div>
  );
}

/** A numeric field + Save button wired to a `useNumberSetting`, with its validation message, saved confirmation and failure. */
export function NumberSettingField({
  setting,
  spec,
  label,
  unit,
  help,
  width = "w-28",
  inputMode = "decimal",
  extra,
}: {
  setting: NumberSetting;
  spec: SpecName;
  label: string;
  unit?: string;
  help?: ReactNode;
  width?: string;
  inputMode?: "decimal" | "numeric";
  /** Extra controls rendered next to the Save button. */
  extra?: ReactNode;
}) {
  const { t } = useTranslation();
  const error = setting.visibleError ? t(validationKey(setting.visibleError), validationParams(spec)) : null;
  const canSave = setting.dirty && setting.parsed.ok && !setting.saving;

  return (
    <div className="flex flex-col gap-2">
      <div className="flex flex-wrap items-end gap-3">
        <Field label={label} unit={unit} help={help} error={error}>
          {(props) => (
            <input
              {...props}
              className={`input ${width}`}
              dir="ltr"
              inputMode={inputMode}
              autoComplete="off"
              value={setting.text}
              onChange={(e) => setting.setText(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && canSave) void setting.save();
              }}
            />
          )}
        </Field>
        <ActionButton className="btn btn-primary" onClick={() => void setting.save()} inactive={!canSave}>
          {t("common.save")}
        </ActionButton>
        {extra}
      </div>
      {setting.saved && !setting.dirty && (
        <p role="status" className="text-xs text-accent-green">
          <span aria-hidden="true">✓ </span>
          {t("common.saved")}
        </p>
      )}
      {setting.saveError !== null && <ErrorPanel error={setting.saveError} compact />}
    </div>
  );
}

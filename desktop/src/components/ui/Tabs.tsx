import { useId, type KeyboardEvent, type ReactNode } from "react";

export interface TabDef<T extends string> {
  id: T;
  label: string;
}

/**
 * An accessible tab strip (role=tablist): arrow keys move between tabs (mirrored in right-to-left layouts),
 * Home/End jump to the ends, and only the selected tab is in the tab order. The panel is labelled by its tab.
 */
export function Tabs<T extends string>({ tabs, value, onChange, children, label }: { tabs: TabDef<T>[]; value: T; onChange: (id: T) => void; children: ReactNode; label: string }) {
  const base = useId();
  const index = tabs.findIndex((t) => t.id === value);

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    const rtl = document.documentElement.dir === "rtl";
    const forward = rtl ? "ArrowLeft" : "ArrowRight";
    const backward = rtl ? "ArrowRight" : "ArrowLeft";
    let next = index;
    if (e.key === forward) next = (index + 1) % tabs.length;
    else if (e.key === backward) next = (index - 1 + tabs.length) % tabs.length;
    else if (e.key === "Home") next = 0;
    else if (e.key === "End") next = tabs.length - 1;
    else return;
    e.preventDefault();
    onChange(tabs[next].id);
    document.getElementById(`${base}-tab-${tabs[next].id}`)?.focus();
  };

  return (
    <div className="flex flex-col gap-4">
      <div role="tablist" aria-label={label} className="flex flex-wrap gap-2" onKeyDown={onKeyDown}>
        {tabs.map((tab) => {
          const selected = tab.id === value;
          return (
            <button
              key={tab.id}
              id={`${base}-tab-${tab.id}`}
              type="button"
              role="tab"
              aria-selected={selected}
              aria-controls={`${base}-panel`}
              tabIndex={selected ? 0 : -1}
              onClick={() => onChange(tab.id)}
              className={`rounded-md px-3 py-1.5 text-sm font-medium transition-colors ${
                selected ? "bg-accent-green text-surface-0" : "bg-surface-2 text-ink-secondary hover:bg-surface-3"
              }`}
            >
              {tab.label}
            </button>
          );
        })}
      </div>
      <div id={`${base}-panel`} role="tabpanel" aria-labelledby={`${base}-tab-${value}`} className="flex min-w-0 flex-col gap-4">
        {children}
      </div>
    </div>
  );
}

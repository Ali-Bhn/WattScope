import { useRef, useState } from "react";
import { parseSetting, type ParseResult, type SpecName } from "@/lib/validation";

export interface NumberSetting {
  text: string;
  setText: (text: string) => void;
  /** Validation result of the current text (always computed; shown to the user only once they have edited it). */
  parsed: ParseResult;
  dirty: boolean;
  /** The validation error worth showing now: only after the user changed the value. */
  visibleError: Extract<ParseResult, { ok: false }>["error"] | null;
  saving: boolean;
  saveError: unknown;
  saved: boolean;
  save: () => Promise<void>;
}

/**
 * One editable numeric setting. `initial` is the stored value (loaded by the caller, so loading and failure
 * states are handled in one place). The user's text is validated against the setting's range; Save is only
 * possible when it is valid *and* different from what is stored, and a stored value the user has not touched is
 * never flagged as an error (so an out-of-range value from an older version is shown, not hidden, and fixed by
 * editing). `persist` may return the canonical stored text to adopt (e.g. what the backend echoed back).
 */
export function useNumberSetting(spec: SpecName, initial: string, persist: (parsed: Extract<ParseResult, { ok: true }>) => Promise<string | void>): NumberSetting {
  const [text, setTextState] = useState(initial);
  const [stored, setStored] = useState(initial);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const persistRef = useRef(persist);
  persistRef.current = persist;

  const parsed = parseSetting(text, spec);
  const dirty = text.trim() !== stored.trim();

  const setText = (next: string) => {
    setTextState(next);
    setSaved(false);
    setSaveError(null);
  };

  const save = async () => {
    if (!parsed.ok || saving) return;
    setSaving(true);
    setSaveError(null);
    try {
      const echoed = await persistRef.current(parsed);
      const canonical = typeof echoed === "string" ? echoed : parsed.canonical;
      setStored(canonical);
      setTextState(canonical);
      setSaved(true);
    } catch (error) {
      setSaveError(error);
    } finally {
      setSaving(false);
    }
  };

  return { text, setText, parsed, dirty, visibleError: dirty && !parsed.ok ? parsed.error : null, saving, saveError, saved, save };
}

// Language facts that need no DOM, so they are unit-testable (i18n.ts itself touches the document).

export const SUPPORTED_LANGUAGES = ["en", "de", "fa"] as const;
export type SupportedLanguage = (typeof SUPPORTED_LANGUAGES)[number];

const RTL_LANGUAGES: readonly SupportedLanguage[] = ["fa"];

export function isRtl(lang: string): boolean {
  return RTL_LANGUAGES.includes(lang as SupportedLanguage);
}

/** A stored value that is not a supported language (an old build, a hand-edited store) falls back to English. */
export function normalizeLanguage(value: string | null | undefined): SupportedLanguage {
  return (SUPPORTED_LANGUAGES as readonly string[]).includes(value ?? "") ? (value as SupportedLanguage) : "en";
}

/** The document direction and language tags a UI language implies. */
export function documentAttributes(lang: string): { dir: "rtl" | "ltr"; lang: string } {
  return { dir: isRtl(lang) ? "rtl" : "ltr", lang };
}

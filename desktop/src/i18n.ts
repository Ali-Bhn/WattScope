import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import en from "@/locales/en.json";
import de from "@/locales/de.json";
import fa from "@/locales/fa.json";

export const SUPPORTED_LANGUAGES = ["en", "de", "fa"] as const;
export type SupportedLanguage = (typeof SUPPORTED_LANGUAGES)[number];

const RTL_LANGUAGES: readonly SupportedLanguage[] = ["fa"];
const STORAGE_KEY = "wattscope.language";

export function isRtl(lang: string): boolean {
  return RTL_LANGUAGES.includes(lang as SupportedLanguage);
}

/** Sets <html dir/lang> -- the whole layout mirrors via CSS logical properties, not just text (Part H). */
export function applyDocumentDirection(lang: string) {
  document.documentElement.dir = isRtl(lang) ? "rtl" : "ltr";
  document.documentElement.lang = lang;
}

function storedLanguage(): SupportedLanguage {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored && (SUPPORTED_LANGUAGES as readonly string[]).includes(stored)) return stored as SupportedLanguage;
  } catch {
    // storage unavailable: fall back to English
  }
  return "en";
}

/** The native tray menu follows the UI language (it is not rendered by React). */
function syncHostLanguage(lang: string) {
  invoke("set_ui_language", { lang }).catch(() => undefined); // not running inside Tauri (e.g. a plain browser): nothing to sync
}

void i18n.use(initReactI18next).init({
  resources: {
    en: { translation: en },
    de: { translation: de },
    fa: { translation: fa },
  },
  lng: storedLanguage(),
  fallbackLng: "en",
  interpolation: { escapeValue: false },
});

i18n.on("languageChanged", (lang) => {
  applyDocumentDirection(lang);
  try {
    localStorage.setItem(STORAGE_KEY, lang);
  } catch {
    // best effort
  }
  syncHostLanguage(lang);
});
applyDocumentDirection(i18n.language);
syncHostLanguage(i18n.language);

export default i18n;

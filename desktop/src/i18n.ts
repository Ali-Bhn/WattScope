import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { invoke } from "@tauri-apps/api/core";
import en from "@/locales/en.json";
import de from "@/locales/de.json";
import fa from "@/locales/fa.json";
import { setFormatLanguage } from "@/lib/format";
import { documentAttributes, normalizeLanguage, SUPPORTED_LANGUAGES, type SupportedLanguage } from "@/lib/language";

export { SUPPORTED_LANGUAGES, type SupportedLanguage };

const STORAGE_KEY = "wattscope.language";

/** Sets <html dir/lang> -- the whole layout mirrors via CSS logical properties, not just text. */
export function applyDocumentDirection(lang: string) {
  const attributes = documentAttributes(lang);
  document.documentElement.dir = attributes.dir;
  document.documentElement.lang = attributes.lang;
}

function storedLanguage(): SupportedLanguage {
  try {
    return normalizeLanguage(localStorage.getItem(STORAGE_KEY));
  } catch {
    return "en"; // storage unavailable
  }
}

/** The native tray menu follows the UI language (it is not rendered by React). */
function syncHostLanguage(lang: string) {
  invoke("set_ui_language", { lang }).catch(() => undefined); // not running inside Tauri (e.g. a plain browser): nothing to sync
}

const initial = storedLanguage();
setFormatLanguage(initial);

void i18n.use(initReactI18next).init({
  resources: {
    en: { translation: en },
    de: { translation: de },
    fa: { translation: fa },
  },
  lng: initial,
  fallbackLng: "en",
  interpolation: { escapeValue: false },
});

i18n.on("languageChanged", (lang) => {
  setFormatLanguage(lang); // dates and numbers follow the language; set before React re-renders
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

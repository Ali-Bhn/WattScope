import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import en from "@/locales/en.json";
import de from "@/locales/de.json";
import fa from "@/locales/fa.json";

export const SUPPORTED_LANGUAGES = ["en", "de", "fa"] as const;
export type SupportedLanguage = (typeof SUPPORTED_LANGUAGES)[number];

const RTL_LANGUAGES: readonly SupportedLanguage[] = ["fa"];

export function isRtl(lang: string): boolean {
  return RTL_LANGUAGES.includes(lang as SupportedLanguage);
}

/** Sets <html dir/lang> -- the whole layout mirrors via CSS logical properties, not just text (Part H). */
export function applyDocumentDirection(lang: string) {
  document.documentElement.dir = isRtl(lang) ? "rtl" : "ltr";
  document.documentElement.lang = lang;
}

void i18n.use(initReactI18next).init({
  resources: {
    en: { translation: en },
    de: { translation: de },
    fa: { translation: fa },
  },
  lng: "en",
  fallbackLng: "en",
  interpolation: { escapeValue: false },
});

i18n.on("languageChanged", applyDocumentDirection);
applyDocumentDirection(i18n.language);

export default i18n;

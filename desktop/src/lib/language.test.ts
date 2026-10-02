import { describe, expect, it } from "vitest";
import { documentAttributes, isRtl, normalizeLanguage, SUPPORTED_LANGUAGES } from "./language";

describe("language", () => {
  it("supports English, German and Persian, and only Persian is right-to-left", () => {
    expect([...SUPPORTED_LANGUAGES]).toEqual(["en", "de", "fa"]);
    expect(isRtl("fa")).toBe(true);
    expect(isRtl("en")).toBe(false);
    expect(isRtl("de")).toBe(false);
    expect(isRtl("ar")).toBe(false); // an unsupported language is never silently RTL
  });

  it("sets the document direction and language together (the layout mirrors, not just the text)", () => {
    expect(documentAttributes("fa")).toEqual({ dir: "rtl", lang: "fa" });
    expect(documentAttributes("de")).toEqual({ dir: "ltr", lang: "de" });
  });

  it("falls back to English for a stored value that is not a supported language", () => {
    expect(normalizeLanguage("de")).toBe("de");
    expect(normalizeLanguage("fa")).toBe("fa");
    expect(normalizeLanguage("fr")).toBe("en");
    expect(normalizeLanguage("")).toBe("en");
    expect(normalizeLanguage(null)).toBe("en");
    expect(normalizeLanguage(undefined)).toBe("en");
    expect(normalizeLanguage("EN")).toBe("en"); // case-sensitive match only: anything else is the default
  });
});

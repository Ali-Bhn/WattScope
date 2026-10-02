import { describe, expect, it } from "vitest";
import { ERROR_KEYS } from "../lib/errors";
import { END_REASON_KEYS, COST_STATUS_KEYS, SOURCE_KEYS } from "../lib/gaming";
import en from "./en.json";
import de from "./de.json";
import fa from "./fa.json";

type Tree = { [key: string]: string | Tree };

function leaves(tree: Tree, prefix = ""): Record<string, string> {
  return Object.entries(tree).reduce<Record<string, string>>((acc, [key, value]) => {
    const path = prefix ? `${prefix}.${key}` : key;
    return typeof value === "string" ? { ...acc, [path]: value } : { ...acc, ...leaves(value, path) };
  }, {});
}

// Every application source file (tests excluded), read as text, so the checks below see exactly what ships.
const sourceFiles = import.meta.glob(["../**/*.ts", "../**/*.tsx", "!../**/*.test.ts"], { query: "?raw", import: "default", eager: true }) as Record<string, string>;

const english = leaves(en as Tree);
const languages = { de: leaves(de as Tree), fa: leaves(fa as Tree) };
const all = { en: english, ...languages };

const placeholders = (text: string) => [...text.matchAll(/\{\{\s*(\w+)\s*\}\}/g)].map((m) => m[1]).sort();
/** i18next resolves a counted key through its plural forms: "x" is satisfied by "x_one"/"x_other". */
const hasKey = (strings: Record<string, string>, key: string) => key in strings || `${key}_other` in strings || `${key}_one` in strings;

describe("localization", () => {
  it.each(Object.entries(languages))("%s has exactly the keys English has", (_lang, strings) => {
    expect(Object.keys(strings).sort()).toEqual(Object.keys(english).sort());
  });

  it.each(Object.entries(languages))("%s has no empty strings", (_lang, strings) => {
    for (const [key, value] of Object.entries(strings)) {
      expect(value.trim(), key).not.toBe("");
    }
  });

  it.each(Object.entries(languages))("%s uses exactly the interpolation placeholders English does", (_lang, strings) => {
    for (const [key, value] of Object.entries(strings)) {
      expect(placeholders(value), key).toEqual(placeholders(english[key]));
    }
  });

  it("has no malformed placeholders or stray braces in any language", () => {
    for (const [lang, strings] of Object.entries(all)) {
      for (const [key, value] of Object.entries(strings)) {
        const stripped = value.replace(/\{\{\s*\w+\s*\}\}/g, "");
        expect(stripped, `${lang} ${key}`).not.toMatch(/[{}]/);
      }
    }
  });

  it("provides both plural forms (one/other) for every counted string, in every language", () => {
    for (const [lang, strings] of Object.entries(all)) {
      for (const key of Object.keys(strings).filter((k) => k.endsWith("_one"))) {
        expect(strings[key.replace(/_one$/, "_other")], `${lang} ${key}`).toBeTruthy();
      }
      for (const key of Object.keys(strings).filter((k) => k.endsWith("_other"))) {
        expect(strings[key.replace(/_other$/, "_one")], `${lang} ${key}`).toBeTruthy();
      }
    }
  });

  it("counted strings actually use their count", () => {
    for (const [lang, strings] of Object.entries(all)) {
      for (const key of Object.keys(strings).filter((k) => k.endsWith("_other"))) {
        expect(placeholders(strings[key]), `${lang} ${key}`).toContain("count");
      }
    }
  });

  it("keeps technical tokens verbatim (units and identifiers are never translated or spelled out)", () => {
    for (const strings of Object.values(all)) {
      for (const key of ["terms.systemPower", "terms.cpuPower", "terms.gpuPower", "terms.cpuEnergy", "terms.gpuEnergy"]) {
        expect(strings[key], key).toBeTruthy();
      }
      expect(strings["terms.cpuPower"]).toContain("CPU");
      expect(strings["terms.gpuPower"]).toContain("GPU");
      expect(strings["terms.cpuEnergy"]).toContain("CPU");
      expect(strings["terms.gpuEnergy"]).toContain("GPU");
    }
  });

  it("covers every monitoring-quality state the dashboard can show, in every language", () => {
    const states = ["starting", "complete", "partial", "sensorUnavailable", "stopped", "unavailable"];
    for (const strings of Object.values(all)) {
      for (const state of states) {
        expect(strings[`dashboard.quality.${state}`], state).toBeTruthy();
        if (state !== "complete") expect(strings[`dashboard.qualityHelp.${state}`], state).toBeTruthy();
      }
    }
  });

  it("covers every dynamic key family the UI builds at runtime (not just literal t() calls)", () => {
    const families: Record<string, string[]> = {
      "badge.": ["measured", "estimated", "partial", "unavailable", "complete", "assumed"],
      "completeness.": ["complete", "partial", "none"],
      "readingState.": ["valid", "missing", "unavailable", "nonFinite", "negative", "aboveMaximum", "implausibleZero", "ambiguous", "notPresent"],
      "reports.status.": ["active", "stopped", "interrupted"],
      "backend.reason.": ["tooManyRestarts", "recorderLockHeld", "databaseError", "spawnFailed"],
      "nav.": ["dashboard", "gaming", "reports", "settings"],
      "settings.groups.": ["monitoring", "estimation", "electricity", "data", "language", "application"],
      "validation.": ["empty", "notNumber", "notInteger", "belowMin", "aboveMax", "tooManyDecimals"],
      "firstRun.points.": ["local", "measured", "estimated", "cpu", "price"].flatMap((p) => [`${p}.title`, `${p}.body`]),
    };
    for (const [lang, strings] of Object.entries(all)) {
      for (const [prefix, names] of Object.entries(families)) {
        for (const name of names) expect(hasKey(strings, prefix + name), `${lang} ${prefix}${name}`).toBe(true);
      }
      for (const { title, hint } of Object.values(ERROR_KEYS)) {
        expect(strings[title], `${lang} ${title}`).toBeTruthy();
        expect(strings[hint], `${lang} ${hint}`).toBeTruthy();
      }
      for (const key of [...Object.values(END_REASON_KEYS), ...Object.values(SOURCE_KEYS), ...Object.values(COST_STATUS_KEYS)]) {
        expect(strings[key], `${lang} ${key}`).toBeTruthy();
      }
    }
  });

  it("has a string for every literal t(\"...\") key used in the source (no missing translations)", () => {
    const used = new Set<string>();
    for (const source of Object.values(sourceFiles)) {
      for (const m of source.matchAll(/\bt\(\s*"([A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)+)"/g)) used.add(m[1]);
    }
    expect(used.size).toBeGreaterThan(150);
    for (const [lang, strings] of Object.entries(all)) {
      for (const key of used) expect(hasKey(strings, key), `${lang} is missing "${key}"`).toBe(true);
    }
  });
  it("has no unused strings left behind (every key is used literally, by a dynamic family, or by a lookup table)", () => {
    const corpus = Object.values(sourceFiles).join("\n");
    const dynamicPrefixes = [
      "badge.", "completeness.", "readingState.", "reports.status.", "backend.reason.", "nav.", "settings.groups.", "validation.",
      "firstRun.points.", "errors.", "gaming.endReason.", "gaming.source.", "gaming.costStatus.", "dashboard.quality.", "dashboard.qualityHelp.",
    ];
    const unused = Object.keys(english).filter((key) => {
      const base = key.replace(/_(one|other)$/, "");
      if (dynamicPrefixes.some((p) => base.startsWith(p))) return false;
      return !corpus.includes(`"${base}"`);
    });
    expect(unused).toEqual([]);
  });
  it("actually translates (German and Persian differ from English), except identifiers that are the same word", () => {
    // Words that are identical in German by nature ("Status" is the German word too) or are product/technical identifiers.
    const sameInGerman = new Set(["app.title", "chart.cpu", "chart.gpu", "nav.gaming", "gaming.title", "gaming.live", "settings.diagnostics.webview", "settings.diagnostics.os", "reports.titles.month", "settings.electricity.status", "settings.application.administrator"]);
    for (const [key, value] of Object.entries(english)) {
      if (sameInGerman.has(key)) continue;
      expect(languages.de[key], `de ${key}`).not.toBe(value);
    }
    const sameInPersian = new Set(["app.title", "chart.cpu", "chart.gpu", "settings.diagnostics.webview", "reports.titles.month"]);
    for (const [key, value] of Object.entries(english)) {
      if (sameInPersian.has(key)) continue;
      expect(languages.fa[key], `fa ${key}`).not.toBe(value);
    }
  });

  it("writes Persian in Persian script (only identifiers and bare placeholders stay Latin)", () => {
    // "common.na" is a bare dash ("not available"), which has no letters in any script.
    const latinOnly = new Set(["app.title", "chart.cpu", "chart.gpu", "settings.diagnostics.webview", "reports.titles.month", "common.na"]);
    for (const [key, value] of Object.entries(languages.fa)) {
      if (latinOnly.has(key)) continue;
      expect(value, key).toMatch(/[؀-ۿ]/);
    }
  });

  it("keeps CPU, GPU, W and kWh tokens readable inside Persian text", () => {
    expect(languages.fa["terms.gpuPower"]).toContain("GPU");
    expect(languages.fa["terms.cpuPower"]).toContain("CPU");
    expect(languages.fa["gaming.history.avgCpu"]).toContain("CPU");
    expect(languages.fa["gaming.history.avgGpu"]).toContain("GPU");
    expect(languages.fa["readingState.implausibleZero"]).toContain("0 W");
  });

  it("uses one name per concept in every language (terminology is consistent)", () => {
    // The English product terms; each concept has one canonical key in `terms.*` and the rest of the UI says the same words.
    expect(english["terms.monitoringSession"]).toBe("Monitoring session");
    expect(english["terms.gamingSession"]).toBe("Gaming session");
    expect(english["terms.systemPower"]).toBe("Estimated system power");
    expect(english["terms.cpuPower"]).toBe("CPU package power");
    expect(english["terms.gpuPower"]).toBe("GPU board power");
    // The same canonical German/Persian words appear wherever the concept is named.
    expect(languages.de["reports.titles.session"]).toContain(languages.de["terms.monitoringSession"]);
    expect(languages.de["gaming.history.avgSystem"]).toContain("Systemleistung");
    expect(languages.fa["reports.tabs.sessions"]).toContain("نشست");
    expect(languages.fa["terms.monitoringSession"]).toContain("نشست پایش");
  });

  it("states the honesty vocabulary the same way in every language", () => {
    for (const [lang, strings] of Object.entries(all)) {
      // PARTIAL always reads as a lower bound where it is explained.
      expect(strings["completeness.partial"], lang).toBeTruthy();
      expect(strings["badge.partial"], lang).not.toBe(strings["badge.complete"]);
      expect(strings["badge.measured"], lang).not.toBe(strings["badge.estimated"]);
      expect(strings["badge.unavailable"], lang).not.toBe(strings["badge.partial"]);
    }
  });

  it("has the Gaming navigation entry in every language", () => {
    for (const strings of Object.values(all)) {
      expect(strings["nav.gaming"]).toBeTruthy();
      expect(strings["common.na"]).toBeTruthy();
    }
  });

  it("has no leftover strings for the removed elevation button", () => {
    for (const strings of Object.values(all)) {
      expect(strings["settings.restartElevated"]).toBeUndefined();
    }
  });

  it("actually translates the key lifecycle strings", () => {
    for (const key of ["dashboard.quality.partial", "settings.application.exit", "settings.application.administratorHelp", "dashboard.startMonitoring", "backend.reason.tooManyRestarts"]) {
      expect(languages.de[key], key).not.toBe(english[key]);
      expect(languages.fa[key], key).not.toBe(english[key]);
    }
  });

  it("localizes every Gaming string in German and Persian (not left in English)", () => {
    const gamingKeys = Object.keys(english).filter((k) => k.startsWith("gaming."));
    expect(gamingKeys.length).toBeGreaterThan(80);
    const sameAllowed = new Set(["gaming.title", "gaming.live", "gaming.history.title"]);
    for (const key of gamingKeys) {
      if (sameAllowed.has(key)) continue;
      expect(languages.de[key], `de ${key}`).not.toBe(english[key]);
      expect(languages.fa[key], `fa ${key}`).not.toBe(english[key]);
    }
  });
});


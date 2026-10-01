import { describe, expect, it } from "vitest";
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

const english = leaves(en as Tree);
const languages = { de: leaves(de as Tree), fa: leaves(fa as Tree) };

describe("localization", () => {
  it.each(Object.entries(languages))("%s has exactly the keys English has", (_lang, strings) => {
    expect(Object.keys(strings).sort()).toEqual(Object.keys(english).sort());
  });

  it.each(Object.entries(languages))("%s has no empty strings", (_lang, strings) => {
    for (const [key, value] of Object.entries(strings)) {
      expect(value.trim(), key).not.toBe("");
    }
  });

  it.each(Object.entries(languages))("%s keeps interpolation-free strings (no stray placeholders)", (_lang, strings) => {
    for (const [key, value] of Object.entries(strings)) {
      expect(value, key).not.toMatch(/\{\{|\}\}/);
    }
  });

  it("covers every monitoring-quality state the dashboard can show, in every language", () => {
    const states = ["starting", "complete", "partial", "sensorUnavailable", "stopped", "unavailable"];
    for (const strings of [english, ...Object.values(languages)]) {
      for (const state of states) {
        expect(strings[`dashboard.quality.${state}`], state).toBeTruthy();
        if (state !== "complete") expect(strings[`dashboard.qualityHelp.${state}`], state).toBeTruthy();
      }
    }
  });

  it("actually translates the new M004 strings (German and Persian differ from English)", () => {
    for (const key of ["dashboard.quality.partial", "settings.background.exit", "settings.administrator.description", "dashboard.startMonitoring"]) {
      expect(languages.de[key], key).not.toBe(english[key]);
      expect(languages.fa[key], key).not.toBe(english[key]);
    }
  });

  it("writes Persian in Persian script", () => {
    expect(languages.fa["dashboard.quality.partial"]).toMatch(/[؀-ۿ]/);
    expect(languages.fa["settings.background.description"]).toMatch(/[؀-ۿ]/);
  });

  it("has no leftover strings for the removed elevation button", () => {
    for (const strings of [english, ...Object.values(languages)]) {
      expect(strings["settings.restartElevated"]).toBeUndefined();
    }
  });
});

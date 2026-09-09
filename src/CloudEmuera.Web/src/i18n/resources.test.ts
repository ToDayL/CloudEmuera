import { describe, expect, it } from "vitest";
import { resources } from "./resources";

function flatten(value: unknown, prefix = ""): Map<string, string> {
  const result = new Map<string, string>();
  for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (typeof child === "string") result.set(path, child);
    else for (const entry of flatten(child, path)) result.set(...entry);
  }
  return result;
}

describe("translation resources (I18N-001, I18N-009)", () => {
  it("have identical, non-empty keys and interpolation arguments", () => {
    const dictionaries = Object.values(resources).map(locale => flatten(locale.translation));
    const keys = [...dictionaries[0].keys()];
    for (const dictionary of dictionaries) {
      expect([...dictionary.keys()]).toEqual(keys);
      for (const [key, value] of dictionary) {
        expect(value.trim(), key).not.toBe("");
        expect(value.match(/{{\s*[\w.]+\s*}}/g) ?? [], key).toEqual(dictionaries[0].get(key)?.match(/{{\s*[\w.]+\s*}}/g) ?? []);
      }
    }
  });

  it("defines one/other variants for every count-dependent label", () => {
    const dictionary = flatten(resources["en-US"].translation);
    const pluralKeys = [
      "games.activations",
      "saves.filesCount",
      "gameDetail.blockingCount",
      "gameDetail.runtimeDiagnostics",
      "gameDetail.diagnostics",
      "adminRuntime.subscriptions",
      "adminRuntime.subscribed",
      "adminUsers.count",
    ];

    for (const key of pluralKeys) {
      expect(dictionary.has(`${key}_one`), key).toBe(true);
      expect(dictionary.has(`${key}_other`), key).toBe(true);
      expect(dictionary.has(key), key).toBe(false);
    }
  });
});

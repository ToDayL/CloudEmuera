import { describe, expect, it } from "vitest";
import { normalizeUiLocale, resolvePostLoginLocale, resolvePreAuthLocale } from "./index";
import { ApiError } from "../api";

describe("UI locale resolution (I18N-002, I18N-004)", () => {
  it("normalizes supported language tags", () => {
    expect(normalizeUiLocale("zh-Hans-CN")).toBe("zh-CN");
    expect(normalizeUiLocale("JA_jp")).toBe("ja-JP");
    expect(normalizeUiLocale("en-GB")).toBe("en-US");
    expect(normalizeUiLocale("fr-FR")).toBeNull();
  });
  it("uses the documented pre-auth and post-login precedence", () => {
    expect(resolvePreAuthLocale("ja-JP", "en-US", ["zh-CN"])).toBe("ja-JP");
    expect(resolvePreAuthLocale(null, null, ["fr-FR", "en-GB"])).toBe("en-US");
    expect(resolvePreAuthLocale("zh-unknown", "ja-unknown", ["en-US"])).toBe("en-US");
    expect(resolvePostLoginLocale(null, "ja-JP", "zh-CN")).toBe("ja-JP");
    expect(resolvePostLoginLocale("en-US", "ja-JP", "zh-CN")).toBe("en-US");
  });
  it("does not expose an unknown server message as the primary UI error", () => {
    const error = new ApiError("sensitive server detail", "UNKNOWN_FAILURE", 500, "req_test");
    expect(error.message).toContain("UNKNOWN_FAILURE");
    expect(error.message).toContain("req_test");
    expect(error.message).not.toContain("sensitive server detail");
    expect(error.serverMessage).toBe("sensitive server detail");
  });
});

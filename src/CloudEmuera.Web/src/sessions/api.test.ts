import { afterEach, describe, expect, it, vi } from "vitest";
import { browserLayoutWidth, waitForSession, type SessionState } from "./api";

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("browser layout width", () => {
  it("uses the narrower document content box when a scrollbar reduces available width", () => {
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 390 });
    Object.defineProperty(document.documentElement, "clientWidth", { configurable: true, value: 375 });

    expect(browserLayoutWidth()).toBe(375);
  });

  it("falls back to the browser width when the document metric is unavailable", () => {
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 390 });
    Object.defineProperty(document.documentElement, "clientWidth", { configurable: true, value: 0 });

    expect(browserLayoutWidth()).toBe(390);
  });
});

describe("session state polling", () => {
  it("can wait for a durable transition without a browser-side attempt deadline", async () => {
    vi.useFakeTimers();
    let requests = 0;
    vi.stubGlobal("fetch", vi.fn(async () => {
      requests++;
      return {
        ok: true,
        status: 200,
        json: async () => ({ id: "sess_poll", state: requests < 65 ? "STARTING" : "RUNNING" }),
      } as Response;
    }));

    const pending = waitForSession("sess_poll", new Set<SessionState>(["RUNNING"]), { attempts: null });
    await vi.advanceTimersByTimeAsync(70_000);

    await expect(pending).resolves.toMatchObject({ id: "sess_poll", state: "RUNNING" });
    expect(requests).toBe(65);
  });
});

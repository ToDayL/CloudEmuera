import { act, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { DeadlineClock } from "./DeadlineClock";

describe("DeadlineClock", () => {
  it("shows an expired prompt without submitting or changing its state", () => {
    render(<DeadlineClock deadlineUnixMilliseconds={Date.now() - 1} serverTimeOffsetMilliseconds={0} />);
    expect(screen.getByRole("status")).toHaveTextContent("时间已到，等待游戏确认");
  });

  it("uses monotonic elapsed time when the wall clock jumps", () => {
    vi.useFakeTimers();
    const start = new Date("2026-01-01T00:00:00.000Z");
    vi.setSystemTime(start);
    try {
      render(<DeadlineClock deadlineUnixMilliseconds={start.getTime() + 2_000} serverTimeOffsetMilliseconds={0} />);
      act(() => {
        vi.setSystemTime(new Date(start.getTime() + 60_000));
        vi.advanceTimersByTime(250);
      });
      expect(screen.getByRole("timer")).toHaveTextContent("00:02");
    } finally {
      vi.useRealTimers();
    }
  });
});

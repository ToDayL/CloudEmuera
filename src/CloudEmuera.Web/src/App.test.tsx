import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { App } from "./App";

describe("App", () => {
  it("renders the project name", () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(() => new Promise<Response>(() => undefined)),
    );

    render(<App />);

    expect(screen.getByRole("heading", { name: "CloudEmuera" })).toBeInTheDocument();
  });
});


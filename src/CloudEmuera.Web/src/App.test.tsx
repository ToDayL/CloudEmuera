import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { App } from "./App";

describe("App", () => {
  function renderAt(path: string) {
    return render(<MemoryRouter initialEntries={[path]}><App /></MemoryRouter>);
  }

  it("renders and filters the game library", () => {
    renderAt("/games");

    expect(screen.getByRole("heading", { name: "游戏库" })).toBeInTheDocument();
    fireEvent.change(screen.getByPlaceholderText("搜索游戏或版本…"), { target: { value: "Megaten" } });

    expect(screen.getByRole("heading", { name: "ERA Megaten" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "ERA: The World" })).not.toBeInTheDocument();
  });

  it("shows reconnect state without closing the session", () => {
    renderAt("/sessions/sess-world");

    fireEvent.click(screen.getByRole("button", { name: "实时连接" }));

    expect(screen.getByText("连接已中断，正在恢复…")).toBeInTheDocument();
    expect(screen.getByText("游戏仍在服务器上运行，你的输入会在重新连接后恢复。")).toBeInTheDocument();
  });

  it("switches to the Emuera-compatible console without losing input behavior", () => {
    const view = renderAt("/sessions/sess-world");

    fireEvent.click(view.getByRole("button", { name: "兼容" }));

    expect(view.getByRole("region", { name: "游戏控制台（兼容模式）" })).toBeInTheDocument();
    expect(view.getByText("Emuera Console")).toBeInTheDocument();
    fireEvent.click(view.getByRole("button", { name: "[1] 前往港口市场" }));
    expect(view.getByText("> 1")).toBeInTheDocument();
  });

  it("locks save mutation while a worker is active", () => {
    renderAt("/saves");
    fireEvent.click(screen.getByRole("button", { name: /周目二 · 港口存档/ }));

    expect(screen.getByText("Session 运行时存档由 Worker 独占")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "删除 save01.sav" })).toBeDisabled();
  });
});

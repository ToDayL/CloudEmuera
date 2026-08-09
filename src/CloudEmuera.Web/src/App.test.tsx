import { fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { AuthProvider, CurrentUser } from "./auth";

describe("App", () => {
  function renderAt(path: string) {
    const user: CurrentUser = { id: "usr_test", username: "tester", email: "tester@example.com", role: "PLAYER", status: "ACTIVE", mustChangePassword: false, stateVersion: 0 };
    return render(<MemoryRouter initialEntries={[path]}><AuthProvider initialUser={user}><App /></AuthProvider></MemoryRouter>);
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

  it("lets an administrator create a local user through the real admin API contract", async () => {
    const admin: CurrentUser = { id: "usr_admin", username: "admin", email: "admin@example.test", role: "ADMIN", status: "ACTIVE", mustChangePassword: false, stateVersion: 1 };
    const created: CurrentUser = { id: "usr_player", username: "player-one", email: "player@example.test", role: "PLAYER", status: "ACTIVE", mustChangePassword: true, stateVersion: 0 };
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ items: [admin] }), { status: 200, headers: { "Content-Type": "application/json" } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ token: "csrf-token" }), { status: 200, headers: { "Content-Type": "application/json" } }))
      .mockResolvedValueOnce(new Response(JSON.stringify(created), { status: 201, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    render(<MemoryRouter initialEntries={["/admin/users"]}><AuthProvider initialUser={admin}><App /></AuthProvider></MemoryRouter>);

    expect(await screen.findByRole("heading", { name: "用户管理" })).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText("用户名"), { target: { value: "player-one" } });
    fireEvent.change(screen.getByLabelText("登录邮箱"), { target: { value: "player@example.test" } });
    fireEvent.change(screen.getByLabelText("临时密码"), { target: { value: "player-temporary-password" } });
    fireEvent.click(screen.getByRole("button", { name: "创建用户" }));

    expect(await screen.findByText("player@example.test")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith("/api/v1/admin/users", expect.objectContaining({ method: "POST" }));
    vi.unstubAllGlobals();
  });

  it("does not misreport an unready service as invalid credentials", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ token: "csrf-token" }), { status: 200, headers: { "Content-Type": "application/json" } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ code: "SERVICE_NOT_READY", message: "服务尚未完成初始化。", requestId: "req_test" }), { status: 503, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    render(<MemoryRouter initialEntries={["/login"]}><AuthProvider initialUser={null}><App /></AuthProvider></MemoryRouter>);

    fireEvent.change(screen.getByLabelText("登录邮箱"), { target: { value: "admin@example.test" } });
    fireEvent.change(screen.getByLabelText("密码"), { target: { value: "temporary-password" } });
    fireEvent.click(screen.getByRole("button", { name: /登录/ }));

    expect(await screen.findByRole("alert")).toHaveTextContent("服务尚未完成数据库迁移或首次初始化。");
    vi.unstubAllGlobals();
  });
});

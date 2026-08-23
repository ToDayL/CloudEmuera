import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { AuthProvider, CurrentUser } from "./auth";

const digest = "sha256:abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";
const runtimeFontDigest = "01799063a83f8af346c5e02f1a46c3adcd8b81a189abda60a6903075aea7bb25";
const runtimeFontFaceId = "sarasa-fixed-sc-1.0.40-regular";

function runtimeFontCatalog() {
  return {
    schemaVersion: 1,
    catalogDigest: "a".repeat(64),
    defaultFaceId: runtimeFontFaceId,
    items: [{
      faceId: runtimeFontFaceId,
      displayName: "Sarasa Fixed SC Regular",
      family: "sarasa-fixed-sc",
      sourceVersion: "1.0.40",
      weight: 400,
      runtimeFamilyName: "Sarasa Fixed SC",
      webAssetDigest: runtimeFontDigest,
      webAssetByteLength: 9,
      webAssetUrl: `/api/v1/runtime-fonts/assets/${runtimeFontDigest}.woff2`,
      licenseId: "OFL-1.1",
    }],
  };
}

function game(overrides: Record<string, unknown> = {}) {
  return {
    id: "g1",
    name: "ERA: The World",
    visibility: "PRIVATE",
    status: "ACTIVE",
    workspaceStatus: "NONE",
    hasCurrentContent: true,
    contentDigest: digest,
    contentRevision: 1,
    stateVersion: 1,
    createdAt: "2026-08-01T00:00:00Z",
    updatedAt: "2026-08-09T08:00:00Z",
    ...overrides,
  };
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function mockFetch(handler: (url: string, init?: RequestInit) => Response | Promise<Response>) {
  const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    return Promise.resolve(handler(url, init));
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function session(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: 1,
    id: "sess-world",
    name: "港口旅程",
    game: { id: "g1", name: "ERA: The World" },
    sourceContentDigest: digest,
    sourceContentRevision: 1,
    runtimeVersion: "emuera-test",
    state: "RUNNING",
    stateVersion: 3,
    workerEpoch: 7,
    waitingForInput: false,
    createdAt: "2026-08-10T00:00:00Z",
    startedAt: "2026-08-10T00:01:00Z",
    lastActivityAt: "2026-08-10T00:02:00Z",
    closedAt: null,
    closeReason: null,
    fontFaceId: runtimeFontFaceId,
    fontSize: 18,
    lineHeight: 19,
    ...overrides,
  };
}

function emptyPresentationManifest() {
  return { schemaVersion: 1, assets: [], fonts: [], fontDiagnostics: [] };
}

class SilentWebSocket {
  static readonly OPEN = 1;
  static readonly CLOSED = 3;
  readonly readyState = 0;
  addEventListener(): void { /* connection remains pending for this test */ }
  send(): void { /* no server is needed for the reconnect UI assertion */ }
  close(): void { /* no-op */ }
}

describe("App", () => {
  function renderAt(path: string) {
    const user: CurrentUser = { id: "usr_test", username: "tester", email: "tester@example.com", role: "PLAYER", status: "ACTIVE", mustChangePassword: false, stateVersion: 0 };
    return render(<MemoryRouter initialEntries={[path]}><AuthProvider initialUser={user}><App /></AuthProvider></MemoryRouter>);
  }

  it("loads and saves Session startup defaults from the settings page", async () => {
    const savedDefaults = { fontFaceId: runtimeFontFaceId, fontSize: 24, lineHeight: 28 };
    const fetchMock = mockFetch((url, init) => {
      if (url === "/api/v1/preferences/session-startup-defaults" && init?.method === "PUT") return jsonResponse(savedDefaults);
      if (url === "/api/v1/preferences/session-startup-defaults") return jsonResponse({ fontFaceId: runtimeFontFaceId, fontSize: 18, lineHeight: 19 });
      if (url === "/api/v1/runtime-fonts") return jsonResponse(runtimeFontCatalog());
      if (url === `/api/v1/runtime-fonts/assets/${runtimeFontDigest}.woff2`) return new Response(new TextEncoder().encode("font-test"), { headers: { "Content-Type": "font/woff2", "Content-Length": "9" } });
      if (url === "/api/v1/auth/csrf") return jsonResponse({ token: "csrf-token" });
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    const originalFonts = Object.getOwnPropertyDescriptor(document, "fonts");
    Object.defineProperty(document, "fonts", { configurable: true, value: { add: vi.fn(), load: vi.fn().mockResolvedValue([]), ready: Promise.resolve([]) } });
    class TestFontFace {
      constructor(readonly family: string, readonly source: ArrayBuffer, readonly descriptors: FontFaceDescriptors) {}
      load(): Promise<FontFace> { return Promise.resolve(this as unknown as FontFace); }
    }
    vi.stubGlobal("FontFace", TestFontFace);
    try {
      renderAt("/settings");
      expect(await screen.findByRole("heading", { name: "设置" })).toBeInTheDocument();
      await waitFor(() => expect(screen.getByRole("button", { name: "保存默认值" })).toBeEnabled());
      expect(screen.getByLabelText("字号（px）")).toHaveValue(18);
      expect(screen.getByLabelText("行高（px）")).toHaveValue(19);
      expect(screen.queryByText("Sarasa Fixed SC Regular 已通过 WOFF2 校验")).not.toBeInTheDocument();

      fireEvent.change(screen.getByLabelText("字号（px）"), { target: { value: "24" } });
      fireEvent.change(screen.getByLabelText("行高（px）"), { target: { value: "28" } });
      fireEvent.click(screen.getByRole("button", { name: "保存默认值" }));

      expect(await screen.findByText("已保存。之后创建的 Session 将使用这些默认值。"))
        .toBeInTheDocument();
      const putCall = fetchMock.mock.calls.find(([url, init]) => String(url) === "/api/v1/preferences/session-startup-defaults" && init?.method === "PUT");
      expect(JSON.parse(String(putCall?.[1]?.body))).toEqual(savedDefaults);
    } finally {
      vi.unstubAllGlobals();
      if (originalFonts) Object.defineProperty(document, "fonts", originalFonts);
      else Reflect.deleteProperty(document, "fonts");
    }
  });

  it("renders live admin runtime diagnostics and sends an audited force-stop command", async () => {
    const admin: CurrentUser = { id: "usr_admin", username: "admin", email: "admin@example.com", role: "ADMIN", status: "ACTIVE", mustChangePassword: false, stateVersion: 1 };
    const runtime = {
      schemaVersion: 1,
      observedAt: "2026-08-21T08:00:00Z",
      instance: { controlPlaneState: "READY", activeWorkerCount: 1, webSocketConnectionCount: 2, subscriptionCount: 1 },
      workers: [{
        session: { id: "sess-1", name: "Session One", ownerUsername: "owner", gameId: "game-1", gameName: "Game One", state: "RUNNING", stateVersion: 4, lastActivityAt: "2026-08-21T07:59:00Z" },
        worker: { workerId: "worker-1", pid: 431, workerEpoch: 7, leaseStatus: "ACTIVE", heartbeatAt: "2026-08-21T07:59:57Z", heartbeatAgeMilliseconds: 3000, registered: true, ready: true, processExited: false, lastOutputSequence: 19 },
        realtime: { hubState: "LIVE", snapshotSequence: 19, snapshotBytes: 4096, snapshotSizeStatus: "KNOWN", subscriptionCount: 1, resyncCount: 2, softOverflowCount: 3, hardOverflowCount: 0, faultCount: 0, droppedPendingEventCount: 0 },
        runtimeConsistency: "MATCHED",
      }],
      recentFailures: [],
    };
    const fetchMock = mockFetch((url, init) => {
      if (url === "/api/v1/admin/workers?recentFailureLimit=20") return jsonResponse(runtime);
      if (url === "/api/v1/auth/csrf") return jsonResponse({ token: "csrf-token" });
      if (url === "/api/v1/admin/sessions/sess-1:force-stop" && init?.method === "POST") return jsonResponse(session({ id: "sess-1", state: "CRASHED", closeReason: "admin_force_stopped" }));
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });

    render(<MemoryRouter initialEntries={["/admin"]}><AuthProvider initialUser={admin}><App /></AuthProvider></MemoryRouter>);

    expect(await screen.findByText("Session One")).toBeInTheDocument();
    expect(screen.getByText(/worker-1/)).toBeInTheDocument();
    expect(screen.getByText(/snapshot 4\.0 KB/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "强制停止" }));
    const dialog = await screen.findByRole("dialog", { name: "强制停止 Session One" });
    fireEvent.change(within(dialog).getByLabelText("操作原因（必填）"), { target: { value: "管理员处理异常 Worker" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "确认强制停止" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledWith("/api/v1/admin/sessions/sess-1:force-stop", expect.objectContaining({ method: "POST" })));
    const forceCall = fetchMock.mock.calls.find(([url]) => String(url) === "/api/v1/admin/sessions/sess-1:force-stop");
    expect(JSON.parse(String(forceCall?.[1]?.body))).toEqual({ reason: "管理员处理异常 Worker" });
    vi.unstubAllGlobals();
  });

  it("loads the game library from the real API and filters by search and visibility", async () => {
    mockFetch((url) => {
      if (url === "/api/v1/games") {
        return jsonResponse({ items: [game({ id: "g1", name: "ERA: The World" }), game({ id: "g2", name: "ERA Megaten", visibility: "SERVER_SHARED", workspaceStatus: "DRAFT" })] });
      }
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    renderAt("/games");

    expect(await screen.findByRole("heading", { name: "ERA: The World" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "ERA Megaten" })).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("搜索游戏…"), { target: { value: "Megaten" } });
    expect(screen.getByRole("heading", { name: "ERA Megaten" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "ERA: The World" })).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("搜索游戏…"), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "我的游戏" }));
    expect(screen.getByRole("heading", { name: "ERA: The World" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "ERA Megaten" })).not.toBeInTheDocument();

    expect(screen.getByRole("link", { name: "开始游戏" })).toBeInTheDocument();
    vi.unstubAllGlobals();
  });

  it("creates a game through the real API contract", async () => {
    const created = game({ id: "g-new", name: "My Era Game", contentRevision: 0, hasCurrentContent: false, contentDigest: null, workspaceStatus: "NONE" });
    let gamesList: unknown[] = [];
    const fetchMock = mockFetch((url, init) => {
      if (url === "/api/v1/games" && init?.method === "POST") { gamesList = [created]; return jsonResponse(created, 201); }
      if (url === "/api/v1/auth/csrf") return jsonResponse({ token: "csrf-token" });
      if (url === "/api/v1/games") return jsonResponse({ items: gamesList });
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    renderAt("/games");

    expect(await screen.findByText("还没有游戏")).toBeInTheDocument();
    fireEvent.click(screen.getAllByRole("button", { name: "创建游戏" })[0]);
    const dialog = await screen.findByRole("dialog", { name: "创建游戏" });
    fireEvent.change(within(dialog).getByLabelText("游戏名称"), { target: { value: "My Era Game" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "创建游戏" }));

    expect(await screen.findByRole("heading", { name: "My Era Game" })).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith("/api/v1/games", expect.objectContaining({ method: "POST" }));
    vi.unstubAllGlobals();
  });

  it("uploads a package and binds it to a new game workspace", async () => {
    const ingestion = {
      ingestionId: "ing-1",
      ownerUserId: "usr_test",
      expiresAt: "2026-08-10T00:00:00Z",
      manifest: {
        schemaVersion: 1, archiveBytes: 1024, archiveDigest: "sha256:aa", contentBytes: 512,
        fileCount: 1, directoryCount: 1, contentDigest: "sha256:bb", files: [], directories: [], diagnostics: [],
      },
    };
    const created = game({ id: "g-new", name: "My Game", workspaceStatus: "DRAFT", contentRevision: 0, hasCurrentContent: false, contentDigest: null });
    mockFetch((url, init) => {
      if (url === "/api/v1/games" && init?.method === "POST") return jsonResponse(created, 201);
      if (url === "/api/v1/games/g-new/package" && init?.method === "PUT") return jsonResponse(created);
      if (url === "/api/v1/game-package-ingestions") return jsonResponse(ingestion, 201);
      if (url === "/api/v1/auth/csrf") return jsonResponse({ token: "csrf-token" });
      if (url === "/api/v1/games") return jsonResponse({ items: [] });
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    renderAt("/games");
    expect(await screen.findByText("还没有游戏")).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole("button", { name: "导入游戏" })[0]);
    const dialog = await screen.findByRole("dialog", { name: "导入游戏包" });
    const fileInput = dialog.querySelector('input[type="file"]');
    expect(fileInput).not.toBeNull();
    fireEvent.change(fileInput as HTMLInputElement, { target: { files: [new File(["zip"], "my-game.zip", { type: "application/zip" })] } });

    expect(await within(dialog).findByText("游戏包已安全解压")).toBeInTheDocument();
    expect(within(dialog).getByText(/1 个文件/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "绑定并查看草稿" }));

    expect(await within(dialog).findByText("游戏包已绑定到工作区")).toBeInTheDocument();
    expect(within(dialog).getByRole("link", { name: /查看草稿并启用/ })).toHaveAttribute("href", "/games/g-new");
    vi.unstubAllGlobals();
  });

  it("browses the workspace and opens a text file read-only", async () => {
    const draft = game({ workspaceStatus: "DRAFT", stateVersion: 2 });
    const files = [
      { path: "ERB", isDirectory: true, bytes: 0 },
      { path: "ERB/START.ERB", isDirectory: false, bytes: 13, etag: "sha256:start" },
    ];
    const textFile = { path: "ERB/START.ERB", content: "@SYSTEM_TITLE\n", encoding: "UTF-8", hasBom: false, bytes: 13, etag: "sha256:start", stateVersion: 2 };
    mockFetch((url) => {
      if (url === "/api/v1/games/g1") return jsonResponse(draft);
      if (url.startsWith("/api/v1/games/g1/files")) return jsonResponse({ items: files });
      if (url.startsWith("/api/v1/games/g1/file?")) return jsonResponse(textFile);
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    renderAt("/games/g1");
    expect(await screen.findByRole("heading", { name: "ERA: The World" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "文件" }));
    expect(await screen.findByRole("button", { name: /START\.ERB/ })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /START\.ERB/ }));

    const preview = await screen.findByLabelText("查看 ERB/START.ERB");
    expect(preview).toHaveValue("@SYSTEM_TITLE\n");
    expect(preview).toHaveAttribute("readonly");
    expect(screen.getByRole("link", { name: "下载 ERB/START.ERB" })).toHaveAttribute("href", expect.stringContaining("/api/v1/games/g1/download"));
    vi.unstubAllGlobals();
  });

  it("validates then activates the workspace through the real API contract", async () => {
    const draft = game({ workspaceStatus: "DRAFT", stateVersion: 2 });
    const validation = { canActivate: true, contentDigest: digest, fileCount: 1, totalBytes: 13, diagnostics: [], stateVersion: 3 };
    const activated = game({ workspaceStatus: "NONE", contentRevision: 2, stateVersion: 3 });
    let current = draft;
    mockFetch((url) => {
      if (url.endsWith(":validate")) return jsonResponse(validation);
      if (url.endsWith(":activate")) { current = activated; return jsonResponse(activated); }
      if (url === "/api/v1/games/g1") return jsonResponse(current);
      if (url === "/api/v1/auth/csrf") return jsonResponse({ token: "csrf-token" });
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    renderAt("/games/g1");
    expect(await screen.findByRole("heading", { name: "ERA: The World" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "兼容性" }));
    expect(await screen.findByText("尚未运行验证")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "运行验证" }));
    expect(await screen.findByText("验证通过，可以启用")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "内容" }));
    fireEvent.click(screen.getByRole("button", { name: "验证并启用" }));
    expect(await screen.findByText(/内容修订 #2/)).toBeInTheDocument();
    expect(screen.queryByText("工作区草稿")).not.toBeInTheDocument();
    vi.unstubAllGlobals();
  });

  it("surfaces persisted blocking diagnostics after a failed activation", async () => {
    const draft = game({ workspaceStatus: "DRAFT", stateVersion: 2 });
    const blocking = [
      { id: "diag-1", code: "ERB_ENTRYPOINT_MISSING", severity: "ERROR", path: "ERB", message: "ERB directory must contain at least one .ERB file at the package root.", messageKey: "game.validation.erb_entrypoint_missing", activationBlocking: true, overridePolicy: "NEVER", overriddenBy: null, overriddenAt: null },
    ];
    mockFetch((url, init) => {
      if (url === "/api/v1/games/g1") return jsonResponse(draft);
      if (url === "/api/v1/games/g1/diagnostics") return jsonResponse({ items: blocking });
      if (url.endsWith(":activate") && init?.method === "POST") {
        return jsonResponse({ code: "ACTIVATION_VALIDATION_FAILED", message: "The workspace has activation-blocking diagnostics.", requestId: "req-1" }, 422);
      }
      if (url === "/api/v1/auth/csrf") return jsonResponse({ token: "csrf-token" });
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    renderAt("/games/g1");
    expect(await screen.findByRole("heading", { name: "ERA: The World" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "验证并启用" }));
    expect(await screen.findByText(/1 条阻断诊断/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "兼容性" }));
    expect(await screen.findByText("ERB directory must contain at least one .ERB file at the package root.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /重新验证/ })).toBeInTheDocument();
    vi.unstubAllGlobals();
  });

  it("shows blocking ingestion diagnostics after a successful upload", async () => {
    const ingestion = {
      ingestionId: "ing-1",
      ownerUserId: "usr_test",
      expiresAt: "2026-08-10T00:00:00Z",
      manifest: {
        schemaVersion: 1, archiveBytes: 100, archiveDigest: "sha256:aa", contentBytes: 50,
        fileCount: 1, directoryCount: 0, contentDigest: "sha256:bb", files: [], directories: [],
        diagnostics: [
          { code: "TEXT_CONTROL_CHARACTER", severity: 2, stage: "ENCODING", logicalPath: "Readme.txt", messageKey: "gamePackage.diagnostic.textControlCharacter", arguments: {}, publishBlocking: true, suppressedCount: 0 },
        ],
      },
    };
    mockFetch((url, init) => {
      if (url === "/api/v1/games" && init?.method === "POST") return jsonResponse(game({ id: "g-new", name: "My Game", workspaceStatus: "DRAFT", contentRevision: 0, hasCurrentContent: false, contentDigest: null }), 201);
      if (url === "/api/v1/games/g-new/package" && init?.method === "PUT") return jsonResponse(game({ id: "g-new", workspaceStatus: "DRAFT", contentRevision: 0, hasCurrentContent: false, contentDigest: null }));
      if (url === "/api/v1/game-package-ingestions") return jsonResponse(ingestion, 201);
      if (url === "/api/v1/auth/csrf") return jsonResponse({ token: "csrf-token" });
      if (url === "/api/v1/games") return jsonResponse({ items: [] });
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    renderAt("/games");
    expect(await screen.findByText("还没有游戏")).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole("button", { name: "导入游戏" })[0]);
    const dialog = await screen.findByRole("dialog", { name: "导入游戏包" });
    const fileInput = dialog.querySelector('input[type="file"]');
    fireEvent.change(fileInput as HTMLInputElement, { target: { files: [new File(["zip"], "my-game.zip", { type: "application/zip" })] } });

    expect(await within(dialog).findByText("游戏包已安全解压")).toBeInTheDocument();
    expect(within(dialog).getByRole("list", { name: "阻断提醒明细" })).toBeInTheDocument();
    expect(within(dialog).getByText("TEXT_CONTROL_CHARACTER")).toBeInTheDocument();
    expect(within(dialog).getByText("Readme.txt")).toBeInTheDocument();
    vi.unstubAllGlobals();
  });

  it("reports a friendly message when the package upload fails at the transport level", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === "/api/v1/games") return Promise.resolve(jsonResponse({ items: [] }));
      if (url === "/api/v1/auth/csrf") return Promise.resolve(jsonResponse({ token: "csrf-token" }));
      if (url === "/api/v1/game-package-ingestions") return Promise.reject(new TypeError("Failed to fetch"));
      return Promise.resolve(jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404));
    });
    vi.stubGlobal("fetch", fetchMock);
    renderAt("/games");
    expect(await screen.findByText("还没有游戏")).toBeInTheDocument();

    fireEvent.click(screen.getAllByRole("button", { name: "导入游戏" })[0]);
    const dialog = await screen.findByRole("dialog", { name: "导入游戏包" });
    const fileInput = dialog.querySelector('input[type="file"]');
    fireEvent.change(fileInput as HTMLInputElement, { target: { files: [new File(["x".repeat(1024)], "large.zip", { type: "application/zip" })] } });

    expect(await within(dialog).findByText(/网络错误：上传未能完成/)).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "重新选择文件" })).toBeInTheDocument();
    vi.unstubAllGlobals();
  });

  it("loads a real Session while the browser connection is pending", async () => {
    const currentSession = session();
    mockFetch((url) => {
      if (url === "/api/v1/sessions/sess-world") return jsonResponse(currentSession);
      if (url === "/api/v1/runtime-fonts") return jsonResponse(runtimeFontCatalog());
      if (url === `/api/v1/runtime-fonts/assets/${runtimeFontDigest}.woff2`) return new Response(new TextEncoder().encode("font-test"), { headers: { "Content-Type": "font/woff2", "Content-Length": "9" } });
      if (url === "/api/v1/sessions/sess-world/presentation-manifest") return jsonResponse(emptyPresentationManifest());
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    const originalFonts = Object.getOwnPropertyDescriptor(document, "fonts");
    Object.defineProperty(document, "fonts", { configurable: true, value: { add: vi.fn(), load: vi.fn().mockResolvedValue([]), ready: Promise.resolve([]) } });
    class TestFontFace {
      constructor(readonly family: string, readonly source: ArrayBuffer, readonly descriptors: FontFaceDescriptors) {}
      load(): Promise<FontFace> { return Promise.resolve(this as unknown as FontFace); }
    }
    vi.stubGlobal("FontFace", TestFontFace);
    vi.stubGlobal("WebSocket", SilentWebSocket);
    try {
      renderAt("/sessions/sess-world");

      expect(await screen.findByRole("heading", { name: "港口旅程" })).toBeInTheDocument();
      expect(screen.getAllByText("连接中").length).toBeGreaterThan(0);
      expect(screen.getByText("等待 Worker 快照…")).toBeInTheDocument();
      expect(screen.getAllByText("运行中").length).toBeGreaterThan(0);
    } finally {
      vi.unstubAllGlobals();
      if (originalFonts) Object.defineProperty(document, "fonts", originalFonts);
      else Reflect.deleteProperty(document, "fonts");
    }
  });

  it("deletes a closed Session from the Session list", async () => {
    const closedSession = session({ state: "CLOSED", closedAt: "2026-08-10T00:03:00Z", closeReason: "requested" });
    let sessions: unknown[] = [closedSession];
    const fetchMock = mockFetch((url, init) => {
      if (url === "/api/v1/sessions?limit=50") return jsonResponse({ items: sessions, nextCursor: null });
      if (url === "/api/v1/auth/csrf") return jsonResponse({ token: "csrf-token" });
      if (url === "/api/v1/sessions/sess-world" && init?.method === "DELETE") {
        sessions = [];
        return new Response(null, { status: 204 });
      }
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    renderAt("/sessions");

    expect(await screen.findByRole("heading", { name: "港口旅程" })).toBeInTheDocument();
    expect(screen.queryByText("Session 与浏览器连接相互独立")).not.toBeInTheDocument();
    expect(screen.queryByText("关闭标签页不会停止游戏。请在不再需要时显式关闭 Session，以释放 Worker 名额。")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "删除" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "删除" }));

    expect(await screen.findByText("还没有 Session")).toBeInTheDocument();
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining("永久删除 SessionRoot"));
    expect(fetchMock).toHaveBeenCalledWith("/api/v1/sessions/sess-world", expect.objectContaining({ method: "DELETE" }));
    confirm.mockRestore();
    vi.unstubAllGlobals();
  });

  it("locks native save mutations while the real Session is running", async () => {
    const currentSession = session({ name: "周目二", game: { id: "g1", name: "港口游戏" } });
    mockFetch((url) => {
      if (url === "/api/v1/sessions?limit=100") return jsonResponse({ items: [currentSession], nextCursor: null });
      if (url === "/api/v1/sessions/sess-world") return jsonResponse(currentSession);
      if (url === "/api/v1/sessions/sess-world/saves") return jsonResponse({ schemaVersion: 1, layout: "SAV_DIRECTORY", items: [{ path: "save01.sav", kind: "NATIVE_SAVE", sizeBytes: 128, modifiedAt: "2026-08-10T00:02:00Z" }] });
      return jsonResponse({ code: "NOT_FOUND", message: "unexpected", requestId: "req" }, 404);
    });
    renderAt("/saves");

    expect(await screen.findByText("周目二")).toBeInTheDocument();
    expect(screen.getByText("Session 运行时存档由 Worker 独占")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "删除 save01.sav" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "重命名 save01.sav" })).toBeDisabled();
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

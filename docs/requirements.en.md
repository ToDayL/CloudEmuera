# CloudEmuera Requirements and High-Level Design

| Item | Value |
| --- | --- |
| Document status | Draft v0.5 |
| Date | 2026-08-12 |
| Chinese counterpart | [requirements.zh-CN.md](./requirements.zh-CN.md) |
| Intended audience | Product, frontend, backend, runtime, operations, and test engineers |

## 1. Purpose

This document defines the initial product requirements, system boundaries, and high-level technical design for CloudEmuera. CloudEmuera is a single-host system operated by its deployer for themselves and trusted participants. Multiple users may upload and manage their own games, start isolated Sessions, manage saves, and reconnect from desktop or mobile browsers. Application-level resource authorization remains, but the product is not a hostile-tenant game-hosting platform; ADR-0017 defines this trust boundary.

The terms “must,” “should,” and “may” denote mandatory, recommended, and optional capabilities. Numbered requirements are used for implementation traceability and acceptance. The English and Chinese documents use the same identifiers.

Scope note: retained references to the earlier independent control plane, strong isolation, fine-grained
resources, and control-flow recovery describe historical implementation or alternative designs only. The
current MVP follows [ADR-0017](adr/0017-trusted-self-hosted-mvp-simplification.md) and P1-S01.

## 2. Background and Design Decisions

### 2.1 Background

Traditional Emuera is a single-user desktop application with substantial coupling among the interpreter, window, input, rendering, file, and save lifecycles. uEmuera and gEmuera demonstrate that retaining the C# interpreter while replacing the platform and display layers is viable. However, both remain client-oriented and do not directly address multi-user access, browser reconnection, resource isolation, or server operations.

### 2.2 Version baseline

CloudEmuera will use Emuera.EM+EE as the runtime baseline, with an explicit compatibility target for games built for `Emuera 1824+v18`. The initial research baseline is `Emuera.NET 1824+v24+EMv18+EEv56`. Production builds must record the exact upstream commit, vendored-source revision, and CloudEmuera integration-layer version in a runtime manifest instead of recording only the moving term “latest.”

## 3. Goals and Non-Goals

### 3.1 Product goals

- Play Era/Emuera games through modern desktop and mobile browsers.
- Let players upload, inspect, view, and activate their own game packages containing ERB, CSV, and assets.
- Let one user run multiple Sessions for the same or different games concurrently.
- Keep Sessions alive independently from browser connections and restore the latest display and input state on reconnect.
- Isolate saves, configuration, temporary files, and runtime state across users and Sessions.
- Support current Emuera.EM+EE as far as practical while covering common `1824+v18` games.
- Provide a diagnosable, bounded, and backup-friendly architecture for a player-operated single-container deployment.

### 3.2 MVP scope

- Local accounts or one external identity provider.
- Game package upload, validation, browsing, and activation; no browser-based ERB/CSV file writes.
- Session creation, listing, opening, connection, reconnection, explicit closure, and reopening.
- Complete structured support for the text, line and layout, button, HTML/HTML Island, image/sprite,
  background, Shape/CBG, font, animation, and audio semantics of the pinned Emuera.EM+EE baseline that
  can be represented safely in a browser, excluding explicitly prohibited host capabilities.
- User input, timed input, and mobile soft keyboards.
- Per-Session isolated save spaces, with save import, export, rename, and deletion.
- Administrative inspection of basic Worker/Session state and force-stop of a Session.
- A single Docker container deployment in which one Web/API control-plane process directly creates and manages an independent Worker process for each active Session, with persistence through a mounted data directory.

### 3.3 Non-goals

- A public game marketplace, content discovery, ratings, or community features.
- Video desktop streaming or remote transport of Godot/Unity-rendered frames.
- Arbitrary local DLL execution, process execution, or unrestricted network access.
- Arbitrary-instruction process snapshots or resuming from the same instruction after a Worker crash in the first phase.
- Perfect compatibility with every Emuera fork, non-standard patch, or game that depends on historical bugs.
- Collaborative gameplay where multiple players jointly control one Session.
- Multiple containers, multiple API instances, multiple Worker Hosts, cross-host horizontal scaling, or seamless migration.
- A hostile multi-tenant game-hosting service or kernel-enforced isolation against authenticated users exploiting Worker/Runtime vulnerabilities.
- Fine-grained user- or process-level CPU, memory, disk, PID, FD, or output-rate scheduling, metering, or billing.

## 4. Roles and Permissions

### 4.1 Player

- View games the player is authorized to access.
- Upload game packages and manage owned games.
- Set the visibility of owned games.
- Create and manage owned Sessions.
- Connect to owned Sessions and submit input.
- Manage owned saves.

### 4.2 Administrator

- Manage users and instance-wide capacity settings.
- Inspect basic Worker and Session health.
- Force-stop Sessions for failure, security, or maintenance reasons.
- Manage backup, retention, and compatibility policies.

## 5. Core Domain Model

| Entity | Description |
| --- | --- |
| User | Identity, roles, and preferences |
| Game | Stable identity, owner, visibility, ingestion workspace, current runnable content, and runtime requirements |
| Session | A reconnectable instance created for one Game; its SessionRoot pins the content copied at creation |
| WorkerLease | Routing, lease, and fencing epoch for the Session's current Worker |
| ConsoleSnapshot | Current bounded display tree, input prompt, and output sequence |
| OutputEvent | An ordered delta applied to a ConsoleSnapshot |

Relationship constraints:

```text
User 1 ── N Game
User 1 ── N Session N ── 1 Game
Session 1 ── 0..1 Active WorkerLease
Session 1 ── 1 private SessionRoot
```

## 6. Functional Requirements

### 6.1 Identity and authorization

- **AUTH-001**: All non-public APIs and WebSocket connections must be authenticated.
- **AUTH-002**: The API must verify ownership or authorization on every Session, save, and game-file operation and must not rely only on hiding frontend controls.
- **AUTH-003**: A user must not enumerate, read, control, or delete another user's private Sessions or saves.
- **AUTH-004**: Administrative force-stop and resource mutation operations must create audit records.
- **AUTH-005**: The WebSocket endpoint must revalidate identity and access when upgrading a connection and resuming a Session.
- **AUTH-006**: Only while its durable state is uninitialized, a fresh instance must atomically create its first administrator from deployment-provided username, email, and temporary password values. Login must accept email only, and the first login must require changing the temporary password. Concurrent startup may create the administrator at most once. Once completed, the instance must permanently ignore bootstrap configuration and must not rerun bootstrap because administrators are absent, disabled, or have lost their passwords.

### 6.2 Game package and ERB management

- **GAME-001**: The system must support uploading game packages containing `ERB`, `CSV`, configuration, and asset files.
- **GAME-002**: Upload processing must reject path traversal, absolute paths, unsafe symbolic links, archive bombs, and files exceeding configured quotas.
- **GAME-003**: The system must detect and record text-file encodings, covering at least Shift-JIS, UTF-8 with BOM, and UTF-8 without BOM.
- **GAME-004**: Each Game has only one current runnable content tree. That tree must remain immutable until atomically replaced and must record its checksum, activator, activation time, and runtime configuration. The system must not expose GameVersion resources, version labels, version lists, or product-level history rollback.
- **GAME-005**: Upload and inspection must write to the Game's separate ingestion workspace. They must not change current runnable content before validation and atomic activation, and must never change files already copied into an existing Session.
- **GAME-006**: The system must provide directory browsing, read-only text viewing, and file download. Browser-based creation, editing, renaming, deletion, or search of ERB/CSV files is outside MVP scope.
- **GAME-007**: Before activating an ingestion workspace as current content, the system must perform baseline validation for directory layout, encoding, parse errors, missing assets, and prohibited capabilities.
- **GAME-008**: Session creation must copy the Game's current runnable content into a private SessionRoot and record the source digest and runtime-manifest snapshot. Later package replacements or activations must not implicitly alter an existing Session.
- **GAME-009**: Game visibility must support at least private and server-shared modes. Public marketplace publication is outside the MVP scope.
- **GAME-010**: Deleting a Game referenced by any Session must be rejected. An unreferenced Game must first be soft-deleted; ordinary requests must not immediately remove its content recursively.

### 6.3 Session management

- **SESS-001**: A user may create any number of Sessions for the same or different Games. The system must not reject creation because of the total number of previously created Sessions. An instance-wide maximum active Worker count may be configured; active slots are not partitioned by user.
- **SESS-002**: Every active Session must have exactly one valid Runtime owner. Worker replacement must use an increasing epoch to prevent an old Worker from continuing to accept input.
- **SESS-003**: Browser disconnects must not automatically close a Session or clear runtime state.
- **SESS-004**: Without an attached browser, a Session must continue already-started execution, timed input, and internal timers.
- **SESS-005**: A user must be able to view the Session name, Game, source content digest, state, creation time, last activity time, and whether it is waiting for input.
- **SESS-006**: A user must be able to explicitly close a Session. Closure must stop new input, flush files, optionally produce a final autosave, terminate the Worker, and set the state to `CLOSED`; it must not delete or rebuild the SessionRoot.
- **SESS-007**: The API must provide separate idempotent creation, opening, and closure semantics so network retries cannot duplicate a Session, start multiple Workers, or repeat closure side effects.
- **SESS-008**: Except for administrative action, security policy, resource failure, or an explicitly configured deployment policy, the system must not close a Session solely because its connections are idle.
- **SESS-009**: Administrators must be able to inspect active Sessions and force-stop their Workers for failure or maintenance.
- **SESS-010**: After a Worker exits unexpectedly, the Session must transition to `CRASHED` after the heartbeat timeout and must not remain reported as runnable.
- **SESS-011**: A `CLOSED` Session, and a `CRASHED` Session whose old Worker is proven to have lost write access, must be reopenable. Every open reuses the existing SessionRoot, increments the Worker epoch, and creates a new Worker; it must not recopy current Game content or require a new Session.
- **SESS-012**: A Session is a resource that persists from creation until explicit deletion. Opening and closing only acquire or release a Worker. Session deletion must be separate from closure and must never occur automatically because of closure, a crash, an API restart, or a browser disconnect.

### 6.4 Game display and interaction

- **PLAY-001**: Workers must emit structured display events to the API instead of passing unvalidated raw HTML to browser execution.
- **PLAY-002**: The display model must completely support the text, foreground/background color, font and
  layout, line break and temporary-line update, button, tooltip, image, sprite, background-layer, Shape/CBG,
  HTML Island, animation, and audio-control semantics of the pinned Emuera.EM+EE baseline that can be
  represented safely in a browser. Silent no-ops, dropped fields, and plain-text fallbacks must not be
  presented as compatibility.
- **PLAY-003**: The implemented Emuera HTML subset must be parsed with an allowlist. Scripts, event-handler attributes, and arbitrary URLs must not enter the browser DOM.
- **PLAY-004**: Every output event must contain a monotonically increasing Session-local `sequence`.
- **PLAY-005**: The Worker must maintain a bounded ConsoleSnapshot. Real-time batches need only remain until sent or replaced by a newer snapshot; a historical replay window is not required.
- **PLAY-006**: Reconnection must establish a complete ConsoleSnapshot at the current `(workerEpoch, snapshotSequence)` as a new baseline and then receive later real-time batches. Replay from a client acknowledgment is not required.
- **PLAY-007**: Every input request must have a unique `promptId`, and client input must contain a unique `clientMessageId`.
- **PLAY-008**: The Worker must reject stale `promptId` values and old-epoch input, and within a bounded in-memory window of the current Worker must return the original result or an explicit duplicate response for repeated `clientMessageId` values. Deduplication need not survive Worker restart.
- **PLAY-009**: Desktop clients must support keyboard, mouse, and scrolling. Mobile clients must support touch buttons, soft keyboards, viewport changes, and safe areas.
- **PLAY-010**: Display history must have configurable bounds. When the bound is exceeded, the Worker should compact to the latest snapshot and discard invisible old deltas instead of growing memory without limit.
- **PLAY-011**: The same user may view a Session from multiple clients. In the MVP, each `promptId` accepts only the first valid input.
- **PLAY-012**: When the API or browser cannot keep up, the system must use batching, backpressure, or snapshot fallback rather than accumulating messages without limit.

### 6.5 Save management

- **SAVE-001**: Saves must be isolated by user, game, and Session. Every Session must have its own save workspace, and physical save files must not be shared with another Session.
- **SAVE-002**: Relative paths supplied by a game must not escape the assigned save or temporary directory.
- **SAVE-003**: Users must be able to list and download their native saves by Session, and upload, rename, or delete them only while the Session has no active Worker.
- **SAVE-004**: Emuera must write saves directly in the current SessionRoot using its native behavior. CloudEmuera must not add generations, a commit queue, or a second authoritative save copy to the runtime path.
- **SAVE-005**: Session metadata must record its Game, source content digest, Runtime version, runtime-manifest snapshot, and private SessionRoot. Native save files are managed as opaque contents of that Session, without per-save generations.
- **SAVE-006**: Save import must validate file size, path, basic native-file constraints, and Session permission, and re-confirm that the target Session has no active Worker. Semantic compatibility with the Game digest need not be proven.
- **SAVE-007**: MVP does not provide direct save copying between Sessions. A user may download and explicitly upload into another stopped Session, and multiple active Workers must never share the same physical file.
- **SAVE-008**: Autosave and overwrite behavior follows native game and Emuera semantics. System-level history retention is provided by external backup of the whole SessionRoot, not by intercepting each runtime save.
- **SAVE-009**: Save deletion must require confirmation. While a Session is active, no process other than its Worker may modify its saves concurrently.
- **SAVE-010**: Save-content serialization and deserialization must use the native Emuera Runtime implementation. CloudEmuera must not introduce an incompatible game-save format merely to support Web-based save management.
- **SAVE-011**: Every Session must provide Emuera with an independent actual SessionRoot while preserving the engine-visible `CSV/`, `ERB/`, asset, configuration, and save directory structure.
- **SAVE-012**: Before the first Worker starts, Session management must copy the complete validated regular-file tree of the Game's current content into a private SessionRoot. The Worker reads and writes only that copy; Game library content must not be mounted into the runtime directory or share writable inodes with a Session.
- **SAVE-013**: The compatibility layer must support both native Emuera save layouts: `save*.sav`/`global.sav` under GameRoot and the `sav/` directory when `UseSaveFolder:YES`. The game's `emuera.config` is the sole layout authority, and files remain directly in the corresponding location of the current SessionRoot.
- **SAVE-014**: Global saves in native Emuera semantics must be isolated by `User + Game + Session` and must never become server-global or cross-Session shared files.
- **SAVE-015**: From creation onward, SessionRoot is the Session's persistent runtime directory under the mounted data directory. Worker restarts must reuse it, without copying saves to a separate commit store on startup or shutdown.

### 6.6 Administration and operations

- **OPS-001**: Administrators must be able to inspect Session state, current Worker identity/PID, heartbeat, and the most recent error, and force-stop an active Worker. MVP does not require a fine-grained process-resource metrics platform.
- **OPS-002**: The system must support instance-wide limits for active Worker count, archive/expanded size/file count, save-file size, ConsoleSnapshot/WebSocket queue size, and minimum free DataRoot space. User- or process-specific resource quotas, scheduling, reservation, and billing are not required.
- **OPS-003**: The API must expose health, readiness, and version endpoints.
- **OPS-004**: Logs must include correlatable `requestId`, `sessionId`, `workerId`, and `workerEpoch` values, but must not log passwords or full user input by default.
- **OPS-005**: Existing critical audit events for identity, resource mutation, administrative termination, Game-content activation, and save deletion must remain. MVP does not require a general audit-query UI or a complete audit trail for ordinary connections and reads.
- **OPS-006**: Administrators must be able to prevent a known-unsafe Game from creating or reopening Sessions without directly destroying existing SessionRoots or saves.

## 7. Compatibility Requirements

### 7.1 Compatibility levels

The system uses the following compatibility classifications:

| Level | Meaning |
| --- | --- |
| Supported | Covered by automated tests and enforced as a release gate |
| Compatible | Intended to work, with possible documented display or edge-behavior differences |
| Experimental | Available without a stability or completeness commitment |
| Blocked | Prohibited for platform or security reasons |

### 7.2 Runtime requirements

- **COMP-001**: The runtime must be pinned to a recorded Emuera.EM+EE upstream commit and CloudEmuera source-integration version.
- **COMP-002**: A `1824+v18` compatibility suite must cover parsing, variables, function calls, input, saves, HTML, images, and sprites.
- **COMP-003**: A current EM+EE feature suite must be maintained, with unimplemented commands and display capabilities explicitly listed.
- **COMP-004**: Game loading must produce a compatibility report that distinguishes errors, warnings, restricted capabilities, and unknown commands.
- **COMP-005**: Shift-JIS and UTF-8 files must decode deterministically. Behavior must not silently depend on the server's locale.
- **COMP-006**: File-name case behavior must be normalized or diagnosed by the compatibility layer to reduce Windows-to-Linux package differences.
- **COMP-007**: Font measurement, line wrapping, fixed line height, and button hit behavior must have visual regression fixtures.
- **COMP-008**: `CALLSHARP`, arbitrary DLLs, external processes, and unrestricted network access are `Blocked` by default.
- **COMP-009**: Where a game depends on a known v18 behavior that differs in a newer runtime, a bounded and testable compatibility switch may be provided. The project must not maintain an unverifiable global “emulate old bugs” mode.

## 8. Three-Layer Architecture

```text
┌──────────────────────────────────────────────┐
│ Docker container                             │
│                                              │
│ Web/API process                              │
│ Auth │ Game/Save │ Session │ WebSocket       │
│ Worker Manager      │ local IPC              │
│                     ├─ Session Worker process│
│                     └─ Session Worker process│
│                       (one per active Session)│
│                                              │
│ /data  ← mounted persistent data directory  │
│ SQLite │ games │ sessions │ logs │ backups   │
└──────────────────────────────────────────────┘
```

### 8.1 Web layer

- Uses resources only through the API.
- Supports HTTP or HTTPS for control operations and the corresponding WebSocket for real-time events and game input;
  the deployer and an upstream gateway choose whether HTTPS is used, and the application does not force a redirect.
- Stores the Session identifier and current epoch locally, but not authoritative game state; reconnect replaces local display state with a server snapshot.
- Renders structured events through DOM/Canvas/WebAudio and does not execute raw script or HTML supplied by a game.

### 8.2 API layer

The system deploys one API process, whose code should contain at least these modules:

- Identity & Authorization
- Game Package Service
- Save Service
- Session Control Plane
- Realtime Gateway
- Session Registry & instance capacity gate
- Administration & Audit

The API process must not keep active Sessions only in memory. It is the only business process that accesses SQLite at runtime and coordinates HTTP operations, background work, and Worker lifecycles through durable Sessions, WorkerLeases, epochs, and state versions. The same-container entrypoint runs the exclusive Migrator before API startup, and Session Workers do not access SQLite.

The API process directly starts, monitors, and terminates Session Workers and applies only the instance-wide active Worker gate. Exiting the API ends its active Workers. A restarted API does not adopt old Workers; after confirming that they have lost write access to their SessionRoots, it reconciles residual active Sessions to `CRASHED` while preserving each SessionRoot.

In the production single container, the API is the only long-running business process; each Session Worker is an API child process. Docker's lightweight init/PID 1 forwards signals and reaps zombies; the product does not add a resident Supervisor or a second runtime control plane. The container entrypoint runs the exclusive `Migrator` in that same container and starts the API only after it succeeds. The default development topology also keeps only the API long-running and serves the built SPA from it; the Node/Web container only performs one-shot install/build work, while HMR requires an explicit profile.

### 8.3 Worker layer

The Worker layer consists of:

- **API Worker Manager module**: starts, monitors, and terminates Session Worker child processes and checks the instance-wide Worker gate, without loading Emuera Runtime or replacing durable leases with in-memory state.
- **Session Worker process**: one independent operating-system process per active Session, owning one Runtime, one ConsoleSnapshot, and one SessionRoot working directory. This process boundary provides state isolation and termination, not malicious-code execution isolation.

A Session Worker must stop its Runtime and exit within a bound when its local IPC control channel disconnects. It does not continue in the background, and a new API instance does not adopt it.

Every Session Worker must use an actual `SessionRoot` as its process working directory and Emuera GameRoot. At creation, Session management copies the complete validated regular-file tree of the Game's current content; configuration, game-defined directories, saves, and temporary files all live directly in the Session's physical directory:

```text
SessionRoot/
├── CSV/              ← Session-private copy from current Game content
├── ERB/              ← Session-private copy from current Game content
├── resources/        ← Session-private copy from current Game content
├── any-game-dir/     ← every other valid directory is copied too
├── sav/              ← Session-private directory, writable
├── save*.sav         → Session-private save files, writable
├── global.sav        → Session-private save files, writable
└── emuera.config     ← Session-private copy, writable
```

For legacy games that write `save*.sav` and `global.sav` at the GameRoot level, the SessionRoot must provide the corresponding Session-private writable paths. Every Session Worker must run in its own process; an Emuera Runtime must never run inside the API process or another Session Worker process.

### 8.4 Communication protocols

- Browser to API: HTTP or HTTPS + WebSocket; external HTTPS termination and redirects are controlled by the deployer's
  upstream gateway.
- API to Worker: local IPC inside the container, preferably a Unix domain socket. The protocol model must remain transport-independent for testing.
- Large static assets: read from the mounted data directory through the API rather than repeatedly transferring them through real-time messages.
- Every control command must include `sessionId`, `workerEpoch`, a command ID, and a protocol version.

## 9. Session State Machine

```text
CREATING → CLOSED
CLOSED/CRASHED → STARTING → RUNNING
STARTING/RUNNING → STOPPING → CLOSED
STARTING/RUNNING/STOPPING → CRASHED
RUNNING → SUSPENDING → SUSPENDED → RESUMING  (future capability)
```

State definitions:

| State | Description |
| --- | --- |
| CREATING | The API accepted the request and is materializing the persistent SessionRoot |
| STARTING | A Worker is starting and loading the game |
| RUNNING | The Worker is healthy, regardless of whether a browser is currently connected |
| STOPPING | New input is rejected while files are flushed and the Worker terminates |
| CLOSED | The SessionRoot exists without an active Worker; the last run closed normally and the Session may be reopened |
| CRASHED | The SessionRoot exists without an active Worker; the last run ended abnormally and the Session may be reopened after old write access is released |
| SUSPENDED | A future persistent runtime snapshot that consumes no active Worker |

The instance-wide active Worker gate counts `STARTING`, `RUNNING`, and `STOPPING`. `CLOSED`, `CRASHED`, `CREATING`, and `SUSPENDED` do not consume a slot. Open checks the global limit in a database transaction; it does not split active slots by user. Browser connection counts are ephemeral Realtime Gateway data rather than Session state; reaching zero connections neither changes `RUNNING`, stops the Worker, nor releases a Worker slot.

State transitions must be protected by database versioning or transactions. Every `CLOSED/CRASHED → STARTING` transition creates a new WorkerLease with an increased epoch; heartbeats, output, and input responses from an older epoch must be rejected. Reopening reuses the existing SessionRoot without recopying current Game content or restoring pre-crash interpreter memory.

## 10. Reconnection and Message Semantics

### 10.1 Output event

Example:

```json
{
  "protocolVersion": 1,
  "sessionId": "sess_123",
  "workerEpoch": 4,
  "sequence": 1052,
  "type": "display.line.append",
  "payload": {
    "parts": [
      { "type": "text", "text": "Choose an action:" },
      { "type": "button", "text": "[0] Rest", "value": "0" }
    ]
  }
}
```

### 10.2 Resume request

```json
{
  "type": "session.resume",
  "sessionId": "sess_123",
  "lastEpoch": 4
}
```

The Worker/API returns a complete ConsoleSnapshot at sequence `N` for the current epoch and uses it as the new display baseline, then streams real-time batches after `N`. MVP does not replay disconnected history from `lastSequence` or acknowledgments.

### 10.3 Input request

```json
{
  "type": "session.input",
  "sessionId": "sess_123",
  "workerEpoch": 4,
  "promptId": "prompt_88",
  "clientMessageId": "01JXYZ...",
  "value": "0"
}
```

The server must return one deterministic result: accepted, duplicate, stale prompt, no control permission, or invalid format.

## 11. Data and Storage Design

### 11.1 Embedded database inside the container

Use SQLite or an equivalent embedded relational database. Its database file must reside in the mounted data directory, for example `/data/cloudemuera.db`.

Stores:

- Users and roles
- Game metadata, ingestion workspace, current-content digest, and runtime manifest
- Session state and the current WorkerLease
- SessionRoot paths, Session lifecycle state, and save-management audit data
- Administrative policy and compatibility-report summaries

The database file must be backed up together with game files and complete Session directories. Container restarts must not clean or reconstruct existing SessionRoots.

### 11.2 Local physical file system

The container must mount one host data directory at `/data`. All games, Sessions, saves, logs, and backups must use physical files or directories under this directory:

```text
/data/
├── cloudemuera.db
├── games/{gameId}/workspace/       ← internal ingestion/validation workspace
├── games/{gameId}/content/         ← current immutable copy source; no retained version history
├── sessions/{sessionId}/
│   ├── root/                       ← actual SessionRoot
│   └── metadata/
├── logs/
└── backups/
```

Session management must copy every valid regular file and directory in the Game's current manifest and must not discard unknown content through a known-directory allowlist. Symbolic links, hard links, and special files supplied by a game package remain forbidden. A normal byte copy is the baseline; where supported, a reflink preserving copy-on-write semantics may be used with fallback to a normal copy. Hard links are forbidden. Each SessionRoot must be private, persistent, and isolated; Emuera directly reads and writes its complete game copy, configuration, temporary files, and native saves there. Object storage, remote file systems, and external file services must not be runtime or persistence dependencies. Backups must use host-file-system snapshots, directory copies, or equivalent local backup tools.

Production shutdown backups are cold backups: stop the API first, then copy the complete `/data` tree, including
the SQLite main file and its `-wal`/`-shm` files, Data Protection keys, games, sessions, logs, and backups; start
the API only after the copy completes. Restore by replacing `/data` as a whole, running the image entrypoint's
offline `rebind-session-roots` command (which migrates first, validates database markers, and refreshes restored
Game and SessionRoot directory identities), and starting the API. Restoring only SQLite or one SessionRoot is not
supported. On normal SIGTERM shutdown, the default
shared graceful Worker budget is 5 seconds, the shared force-stop budget is 5 seconds, the Host shutdown budget is
15 seconds, and the Compose stop grace period is 20 seconds.

## 12. Security Requirements

- **SEC-001**: Game packages, filenames, and display content must be parsed as unsafe data formats. The deployer must run only games they trust; the system does not promise safe execution of malicious ERB or Runtime content.
- **SEC-002**: The production container may run as root by default for Docker named-volume compatibility; a bind mount may use a deployer-provided UID/GID. The production host HTTP port binds to loopback by default; the deployer and an upstream gateway choose whether HTTPS and redirects are used, and the application does not force a protocol. Cookies are not Secure by default; set `CLOUDEMUERA_SECURITY_SECURE_COOKIES=true` when the public entrypoint is HTTPS. It must not mount the container-management interface, host secrets, or unrelated host directories. Workers should not intentionally use the public network; the execution boundary is the container and the application path checks.
- **SEC-003**: Session management passes only the assigned complete SessionRoot path to a Worker, and normal Worker logic must not access the Game library or another SessionRoot. API and Worker may share a UID, so this is not kernel-enforced hostile-Worker tenant isolation.
- **SEC-004**: ConsoleSnapshot, IPC/WebSocket queues, ZIP expansion, and DataRoot usage must have instance-wide bounds. The production Compose file imposes no CPU limit; memory and PID limits remain optional whole-container deployment settings, and fine-grained process-resource governance is not required.
- **SEC-005**: Archive path traversal, symbolic-link escape, case collisions, and decompression overrun must be prevented.
- **SEC-006**: Browser rendering must encode text and attributes. Emuera HTML must be converted to supported structured nodes.
- **SEC-007**: Local Worker IPC endpoints must not be exposed outside the container. Worker registration must bind the Session, Worker, and epoch issued at launch; a separate cross-instance service-identity challenge protocol is not required.
- **SEC-008**: Saves and assets must be served through an authorized API proxy. Signed or short-lived URLs are not required for MVP.
- **SEC-009**: Logs, metrics, and crash artifacts must avoid exposing credentials and unnecessary user-input content.
- **SEC-010**: Dependency and upstream interpreter updates must pass compatibility regression and security review before becoming the new runtime baseline.

## 13. Non-Functional Requirements

### 13.1 Availability and recovery

- **NFR-001**: Under normal load, reconnecting to a running Session and displaying its initial snapshot should complete within 2 seconds at P95, excluding abnormal external user-network conditions.
- **NFR-002**: During a normal API shutdown, the API must gracefully stop its Workers within a bound and force termination after the bound. The product defaults are a shared 5-second graceful Worker budget, a shared 5-second force-stop budget, a 15-second Host shutdown budget, and a 20-second Compose stop grace period. After an unexpected API exit or control-channel disconnect, Workers must immediately begin bounded shutdown or be reclaimed by a parent/child or process-group fallback. Affected active Sessions must reconcile to `CRASHED` while preserving their SessionRoots.
- **NFR-003**: A Session Worker must not continue running after its control channel disconnects, and it must not be adopted by a new API instance. The user may explicitly reopen the same Session after reconciliation.
- **NFR-004**: A Worker crash must preserve the SessionRoot as found and must not affect current Game content, its workspace, or another Session; the Session must be clearly marked `CRASHED`. A file being overwritten by the native writer is not guaranteed to remain valid.
- **NFR-005**: Arbitrary execution-point recovery and transactional recovery of every save are not initial-phase SLAs. Only persistence of SessionRoot and diagnostic availability are guaranteed.

### 13.2 Performance and capacity

- **NFR-006**: Under normal load, Session list and detail API requests should complete within 300 ms at P95, excluding large-file transfers.
- **NFR-007**: For a connected Session, transport from API input receipt to Worker receipt should complete within 200 ms at P95, excluding ERB execution time.
- **NFR-008**: Workers must batch high-frequency `PRINT` output and bound memory. A slow client must not block the interpreter loop or grow queues without limit.
- **NFR-009**: The instance-wide maximum active Worker count should be chosen through manual or repeatable representative-game validation and set explicitly in deployment configuration.
- **NFR-010**: The system must expose basic Session/Worker state, recent heartbeat, snapshot size, and queue-overflow diagnostics. A full capacity-planning metrics suite is not required.

### 13.3 Maintainability

- **NFR-011**: Interpreter core, platform abstractions, Worker protocol, and Web renderer must be tested separately rather than relying only on browser end-to-end tests.
- **NFR-012**: The Chinese and English requirements must retain identical requirement identifiers. Changes to one document must trigger a parity check against the other.
- **NFR-013**: Every message protocol must include a version field and be forward-compatible with unknown optional fields.
- **NFR-014**: Merging upstream EM+EE changes must generate a change report and run both the v18 and current EM+EE suites.
- **NFR-018**: Automated identity checks running in the same checkout must not read or modify the manual `.env`, `./data`, or Compose project. They must use an explicit temporary env file, an isolated DataRoot, a unique project name, and isolated ports.

### 13.4 Accessibility and client compatibility

- **NFR-015**: The Web UI should follow key WCAG 2.1 AA interaction requirements, including keyboard access, visible focus, and sufficient contrast.
- **NFR-016**: The product must support maintained major versions of Chrome, Firefox, Safari, and Edge at release time.
- **NFR-017**: Mobile interaction must not depend on hover, and buttons and input areas must be touch-friendly.

## 14. Failure Model

| Failure | Expected behavior |
| --- | --- |
| Browser refresh/network loss | The Session remains RUNNING; the Realtime Gateway eventually detects the broken stream, and reconnection restores the snapshot and current prompt |
| API-process exit or restart | Workers stop within bounds or are reclaimed; after proving old Workers have exited, the new API reconciles active Sessions to CRASHED while preserving SessionRoots |
| API–Worker local IPC interruption | The Worker stops the Runtime and exits within a bound; the Session reconciles to CRASHED without re-registration |
| Worker crash | Session becomes CRASHED and its SessionRoot is preserved; after the old Worker exits, the same Session can be reopened and continue from native saves, without an exact-instruction recovery guarantee |
| Docker-container or host restart | Active Workers are treated as failed; Game content, workspaces, and SessionRoots remain in the mounted data directory, and the same Sessions may be reopened after recovery |
| Mounted data directory unavailable | New Sessions and save writes are rejected, with an explicit persistence failure |
| Duplicate client input | Deduplicated using promptId and clientMessageId |
| Delayed messages from an old Worker arrive | Fenced by an older epoch and unable to produce valid new state |

## 15. MVP Acceptance Scenarios

- **AC-001**: One user starts two Sessions for the same game; their variables, display, input, and saves do not affect one another.
- **AC-002**: A user closes the page while awaiting input and logs in again after the configured test interval; the latest display and same prompt are available.
- **AC-003**: On a normal API shutdown, Workers exit gracefully within the configured bound. After the API is force-terminated or the control channel disconnects, Workers immediately begin exit or are reclaimed by their process group. After API recovery, affected Sessions are `CRASHED`, active Worker slots are released, and SessionRoots remain as found. Once old Worker write access is proven released, the user can reopen the same Session and load an existing native save.
- **AC-004**: Two users creating Sessions from the same current Game content cannot access or overwrite each other's Sessions or saves.
- **AC-005**: Repeating the same input message executes it only once.
- **AC-006**: After the user explicitly closes a Session, its Worker exits within the configured bound, the Session becomes CLOSED, and later input is rejected. Reopening the same Session reuses its SessionRoot with a higher epoch and can load the save created before closure.
- **AC-007**: After a Worker is force-killed, its Session becomes CRASHED within the heartbeat window and its SessionRoot is not cleaned up. After the old-Worker exit barrier completes, the same Session can be reopened and inspect or load native saves already present there.
- **AC-008**: A representative `1824+v18` test game completes loading, input, save, load, and primary display scenarios.
- **AC-009**: A representative current EM+EE test game runs all declared Supported features, while unsupported features produce explicit diagnostics.
- **AC-010**: A game package containing path traversal, symbolic-link escape, or archive-bomb characteristics is rejected without writing outside the protected ingestion directory.
- **AC-011**: Both desktop and mobile browsers can create a Session, submit game input, reconnect, and download a save.
- **AC-012**: During sustained high output, Worker and browser memory remain within configured bounds and reconnection still produces a consistent snapshot.
- **AC-013**: A representative game can save and load through native Emuera logic in both root-level and `sav/` layouts, while physical save files appear only in the owning Session's private area.
- **AC-014**: When two users and two Sessions of the same user use the same current Game content, Game library files remain untouched by Workers, and Global saves are isolated by `User + Game + Session`; later package replacements do not change those SessionRoots.

## 16. Suggested Delivery Phases

### Phase 0: Compatibility and runtime validation

- Pin an EM+EE upstream commit.
- Separate the interpreter from WinForms/GDI+ through `IGameConsole`, `RuntimePaths`, file, clock, audio, and image abstractions.
- Build minimal v18 and EM+EE test-game suites.
- Verify that a headless single-Session Worker can run to INPUT, accept input, and use the native format to save and load in both root-level and `sav/` layouts.

### Phase 1: Single-node MVP

- One Docker container, one API control-plane process, SQLite, and one mounted data directory.
- An in-process API Worker Manager directly manages one independent child process per Session; only the API accesses SQLite at runtime.
- WebSocket reconnection, ConsoleSnapshot, save isolation, and structured Web rendering and input for every
  Supported capability in the pinned Emuera.EM+EE baseline.
- Game package upload, an internal ingestion workspace, atomic current-content activation, and baseline diagnostics; no browser-based file writes.

### Phase 2: Self-hosted hardening

- Docker image, health checks, logs, filesystem backups, and upgrade procedures.
- Critical audit, offline backup, basic diagnostics, and administrative force-stop; no resource-metrics platform or general audit console.
- The Emuera HTML, drawing, sprite, audio, and mobile compatibility promised by the MVP must not be deferred
  to this phase.

### Phase 3: Suspension and recovery

- Investigate interpreter snapshots at INPUT safe points.
- Support SUSPENDED/RESUMING to release active memory for Sessions left running without clients for a configured policy interval.
- Investigate in-memory recovery from interpreter safe points under verifiable conditions. The MVP already cold-starts the same SessionRoot and loads native saves, but does not describe that as exact-instruction recovery.

## 17. Open Questions

Identity is confirmed as local accounts with email-only login, revocable Cookie Sessions, and a first-administrator bootstrap that reads `.env` only for an uninitialized instance. ADR-0001 records the triggers for reconsidering OIDC. Remaining open questions:

1. What are the default instance-wide active Worker, archive/expanded content, save-file, snapshot/queue, and minimum-free-space limits?
2. Does multi-tab access need an explicit controller lease, or is first-valid-input sufficient?
3. What compatibility level will the MVP promise for Emuera HTML, sprites, CBG, and audio?
4. May administrators configure a maximum Session lifetime, or only stop Sessions for resource and security reasons?
5. Do games and saves need a portable cross-server import/export format?
6. Which representative v18 and EM+EE games can legally be used in automated compatibility tests?
7. Must bundled fonts be retained, and how will font and game-asset licenses be handled?

## 18. Primary Risks

| Risk | Impact | Mitigation direction |
| --- | --- | --- |
| EM+EE coupling to UI/platform code | Higher headless-Worker effort | Extract platform interfaces first and build differential tests |
| Global static Emuera state | Cross-Session contamination in one process | Use one process per Session initially |
| Coupled game-root and save paths | Shared resources may become writable or saves may cross user boundaries | Complete per-Session copies, no shared writable inode, and isolated SessionRoots |
| Complex display semantics | Games run but UI is misaligned | Structured display model and visual regression tests |
| Damaged or maliciously constructed packages | File escape, disk exhaustion, or browser injection | Protected ingestion, immutable current content, copy validation, instance-wide bounds, and structured display |
| Deployer runs a malicious game | A same-UID Worker may read other DataRoot resources | Support only trusted participants and trusted games; hostile tenancy requires reintroducing kernel isolation |
| Permanently resident Sessions | Memory cost grows with abandoned Sessions | Instance-wide active Worker limit, admin force-stop, and future suspension snapshots |
| No exact recovery after Worker crash | Unsaved progress is lost and a natively overwritten file may be invalid | Whole-directory backups, game-native autosave, explicit failure semantics, and safe-point snapshot research |
| Continued upstream evolution | Difficult merges or regressions | Pinned commits, thin adapter layer, and dual compatibility suites |
| Unclear game and font licensing | Illegal hosting or redistribution | Preserve ownership metadata, require deployment authorization, and avoid public exposure by default |

## 19. References

- [Emuera.EM+EE documentation](https://evilmask.gitlab.io/emuera.em.doc/en/index.html)
- [Emuera.EM+EE changelog](https://evilmask.gitlab.io/emuera.em.doc/en/Changelog/index.html)
- [gEmuera](https://github.com/wwwXiaoHan17/gEmuera)
- [uEmuera](https://github.com/xerysherry/uEmuera)
- [lispcoc/gemuera IGameConsole](https://github.com/lispcoc/gemuera/blob/main/godot/src/Bridge/IGameConsole.cs)

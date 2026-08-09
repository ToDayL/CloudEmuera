# CloudEmuera Requirements and High-Level Design

| Item | Value |
| --- | --- |
| Document status | Draft v0.2 |
| Date | 2026-08-03 |
| Chinese counterpart | [requirements.zh-CN.md](./requirements.zh-CN.md) |
| Intended audience | Product, frontend, backend, runtime, operations, and test engineers |

## 1. Purpose

This document defines the initial product requirements, system boundaries, and high-level technical design for CloudEmuera. CloudEmuera deploys Emuera text games on a remote server so that multiple users can upload and manage their own games, start multiple isolated sessions, manage their own saves, and reconnect from a desktop or mobile browser while the session remains alive after the browser disconnects.

The terms “must,” “should,” and “may” denote mandatory, recommended, and optional capabilities. Numbered requirements are used for implementation traceability and acceptance. The English and Chinese documents use the same identifiers.

## 2. Background and Design Decisions

### 2.1 Background

Traditional Emuera is a single-user desktop application with substantial coupling among the interpreter, window, input, rendering, file, and save lifecycles. uEmuera and gEmuera demonstrate that retaining the C# interpreter while replacing the platform and display layers is viable. However, both remain client-oriented and do not directly address multi-user access, browser reconnection, resource isolation, or server operations.

### 2.2 Version baseline

CloudEmuera will use Emuera.EM+EE as the runtime baseline, with an explicit compatibility target for games built for `Emuera 1824+v18`. The initial research baseline is `Emuera.NET 1824+v24+EMv18+EEv56`. Production builds must record the exact upstream commit, vendored-source revision, and CloudEmuera integration-layer version in a runtime manifest instead of recording only the moving term “latest.”

## 3. Goals and Non-Goals

### 3.1 Product goals

- Play Era/Emuera games through modern desktop and mobile browsers.
- Let players upload, manage, and version their own game packages containing ERB, CSV, and assets.
- Let one user run multiple Sessions for the same or different games concurrently.
- Keep Sessions alive independently from browser connections and restore the latest display and input state on reconnect.
- Isolate saves, configuration, temporary files, and runtime state across users and Sessions.
- Support current Emuera.EM+EE as far as practical while covering common `1824+v18` games.
- Provide an observable, resource-bounded, and backup-friendly architecture for a player-operated single-container deployment.

### 3.2 MVP scope

- Local accounts or one external identity provider.
- Game package upload, validation, browsing, editing, and versioning.
- Session creation, listing, connection, reconnection, and explicit closure.
- Text, styles, buttons, basic HTML, images, sprites, and basic audio events.
- User input, timed input, and mobile soft keyboards.
- Per-Session isolated save spaces, with save import, export, rename, and deletion.
- Administrative Worker inspection and termination of unhealthy Sessions.
- A single Docker container deployment in which Web/API, the Worker Supervisor, and Session Workers run inside the container and persist through a mounted data directory.

### 3.3 Non-goals

- A public game marketplace, content discovery, ratings, or community features.
- Video desktop streaming or remote transport of Godot/Unity-rendered frames.
- Arbitrary local DLL execution, process execution, or unrestricted network access.
- Arbitrary-instruction process snapshots or resuming from the same instruction after a Worker crash in the first phase.
- Perfect compatibility with every Emuera fork, non-standard patch, or game that depends on historical bugs.
- Collaborative gameplay where multiple players jointly control one Session.
- Multiple containers, multiple API instances, multiple Worker Hosts, cross-host horizontal scaling, or seamless migration.

## 4. Roles and Permissions

### 4.1 Player

- View games the player is authorized to access.
- Upload game packages and manage owned games and their versions.
- Set the visibility of owned games.
- Create and manage owned Sessions.
- Connect to owned Sessions and submit input.
- Manage owned saves.

### 4.2 Administrator

- Manage users and quotas.
- Inspect Worker and Session health and resource usage.
- Force-stop Sessions for security, resource, or maintenance reasons.
- Manage backup, retention, and compatibility policies.

## 5. Core Domain Model

| Entity | Description |
| --- | --- |
| User | Identity, roles, quotas, and preferences |
| Game | Stable game identity, player owner, and visibility |
| GameVersion | Immutable ERB/CSV/asset snapshot and runtime requirements |
| Session | A reconnectable game instance created by a user for one GameVersion |
| WorkerLease | Routing, lease, and fencing epoch for the Session's current Worker |
| ConsoleSnapshot | Current bounded display tree, input prompt, and output sequence |
| OutputEvent | An ordered delta applied to a ConsoleSnapshot |

Relationship constraints:

```text
User 1 ── N Session N ── 1 GameVersion N ── 1 Game
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
- **GAME-004**: Every published GameVersion must be immutable and record content checksums, creator, creation time, and runtime configuration.
- **GAME-005**: Browser edits to a published version must create a draft or new version and must not change the files pinned by active Sessions.
- **GAME-006**: The system must provide directory browsing, text viewing, ERB/CSV text editing, search, and file download.
- **GAME-007**: Before publication, the system must perform baseline validation for directory layout, encoding, parse errors, missing assets, and prohibited capabilities.
- **GAME-008**: Session creation must pin an explicit GameVersion. Later publications must not implicitly alter a running Session.
- **GAME-009**: Game visibility must support at least private and server-shared modes. Public marketplace publication is outside the MVP scope.
- **GAME-010**: Deleting a version still referenced by a Session or save must be rejected or implemented as recoverable logical deletion.

### 6.3 Session management

- **SESS-001**: A user may create any number of Sessions for the same or different GameVersions. The system must not reject creation because of the total number of previously created Sessions, but must limit the number of Sessions that are simultaneously active and consuming Workers.
- **SESS-002**: Every active Session must have exactly one valid Runtime owner. Worker replacement must use an increasing epoch to prevent an old Worker from continuing to accept input.
- **SESS-003**: Browser disconnects must not automatically close a Session or clear runtime state.
- **SESS-004**: Without an attached browser, a Session must continue already-started execution, timed input, and internal timers.
- **SESS-005**: A user must be able to view the Session name, game version, state, creation time, last activity time, and whether it is waiting for input.
- **SESS-006**: A user must be able to explicitly close a Session. Closure must stop new input, flush files, optionally produce a final autosave, terminate the Worker, and set the state to `CLOSED`.
- **SESS-007**: The API must provide idempotent creation and closure semantics so network retries cannot duplicate resource creation or closure.
- **SESS-008**: Except for administrative action, security policy, resource failure, or an explicitly configured deployment policy, the system must not close a Session solely because its connections are idle.
- **SESS-009**: Administrators must be able to inspect and force-stop runaway, over-quota, or policy-violating Sessions.
- **SESS-010**: After a Worker exits unexpectedly, the Session must transition to `CRASHED` after the heartbeat timeout and must not remain reported as runnable.

### 6.4 Game display and interaction

- **PLAY-001**: Workers must emit structured display events to the API instead of passing unvalidated raw HTML to browser execution.
- **PLAY-002**: The display model must support text, foreground/background colors, font styles, line breaks, buttons, tooltips, images, sprites, background layers, and basic audio control.
- **PLAY-003**: The implemented Emuera HTML subset must be parsed with an allowlist. Scripts, event-handler attributes, and arbitrary URLs must not enter the browser DOM.
- **PLAY-004**: Every output event must contain a monotonically increasing Session-local `sequence`.
- **PLAY-005**: The Worker must maintain a bounded ConsoleSnapshot and short-term output deltas for reconnection.
- **PLAY-006**: Reconnection must return a snapshot at a defined sequence followed by later deltas, or all deltas beginning after the client's last acknowledged sequence. There must be no loss window between snapshot and subscription.
- **PLAY-007**: Every input request must have a unique `promptId`, and client input must contain a unique `clientMessageId`.
- **PLAY-008**: The Worker must reject stale `promptId` values and return the original result or an explicit duplicate response for repeated `clientMessageId` values; it must not execute an input twice.
- **PLAY-009**: Desktop clients must support keyboard, mouse, and scrolling. Mobile clients must support touch buttons, soft keyboards, viewport changes, and safe areas.
- **PLAY-010**: Display history must have configurable bounds. When the bound is exceeded, the Worker should compact to the latest snapshot and discard invisible old deltas instead of growing memory without limit.
- **PLAY-011**: The same user may view a Session from multiple clients. In the MVP, each `promptId` accepts only the first valid input.
- **PLAY-012**: When the API or browser cannot keep up, the system must use batching, backpressure, or snapshot fallback rather than accumulating messages without limit.

### 6.5 Save management

- **SAVE-001**: Saves must be isolated by user, game, and Session. Every Session must have its own save workspace, and physical save files must not be shared with another Session.
- **SAVE-002**: Relative paths supplied by a game must not escape the assigned save or temporary directory.
- **SAVE-003**: Users must be able to list and download their native saves by Session, and upload, rename, or delete them only while the Session has no active Worker.
- **SAVE-004**: Emuera must write saves directly in the current SessionRoot using its native behavior. CloudEmuera must not add generations, a commit queue, or a second authoritative save copy to the runtime path.
- **SAVE-005**: Session metadata must record its GameVersion, Runtime version, and private SessionRoot. Native save files are managed as opaque contents of that Session, without per-save generations.
- **SAVE-006**: Save import must validate file size, paths, format, permission for the target GameVersion and Session, and re-confirm that the target Session has no active Worker.
- **SAVE-007**: Copying a save from one Session to another must be explicit. Multiple active Workers must not write the same physical file concurrently.
- **SAVE-008**: Autosave and overwrite behavior follows native game and Emuera semantics. System-level history retention is provided by external backup of the whole SessionRoot, not by intercepting each runtime save.
- **SAVE-009**: Save deletion must require confirmation. While a Session is active, no process other than its Worker may modify its saves concurrently.
- **SAVE-010**: Save-content serialization and deserialization must use the native Emuera Runtime implementation. CloudEmuera must not introduce an incompatible game-save format merely to support Web-based save management.
- **SAVE-011**: Every Session must provide Emuera with an independent actual SessionRoot while preserving the engine-visible `CSV/`, `ERB/`, asset, configuration, and save directory structure.
- **SAVE-012**: Before the first Worker starts, Session management must copy the complete validated regular-file tree of the immutable GameVersion into a private SessionRoot. The Worker reads and writes only that copy; the original GameVersion must not be mounted into the runtime directory or share writable inodes with a Session.
- **SAVE-013**: The compatibility layer must support both native Emuera save layouts: `save*.sav`/`global.sav` under GameRoot and the `sav/` directory when `UseSaveFolder:YES`. The game's `emuera.config` is the sole layout authority, and files remain directly in the corresponding location of the current SessionRoot.
- **SAVE-014**: Global saves in native Emuera semantics must be isolated by `User + Game + Session` and must never become server-global or cross-Session shared files.
- **SAVE-015**: From creation onward, SessionRoot is the Session's persistent runtime directory under the mounted data directory. Worker restarts must reuse it, without copying saves to a separate commit store on startup or shutdown.

### 6.6 Administration and operations

- **OPS-001**: Administrators must be able to inspect the in-container Worker Supervisor, Session Worker processes, Sessions, heartbeats, CPU, memory, disk, and output rates.
- **OPS-002**: The system must support configuration of per-user active runtime Session counts, per-Session memory/CPU/disk, game-package size, save size, and output-rate limits; it must not impose a total Session-creation limit.
- **OPS-003**: The API must expose health, readiness, and version endpoints.
- **OPS-004**: Logs must include correlatable `requestId`, `sessionId`, `workerId`, and `workerEpoch` values, but must not log passwords or full user input by default.
- **OPS-005**: Audit events must be recorded for Session creation, connection, closure, crash, administrative termination, game publication, and save deletion.
- **OPS-006**: Administrators must be able to prevent a known-unsafe GameVersion from creating new Sessions without directly destroying existing saves.

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
│                    │ local IPC               │
│                    ▼                         │
│ Worker Supervisor process                    │
│                    │                         │
│                    ├─ Session Worker process │
│                    └─ Session Worker process │
│                       (one per active Session)│
│                                              │
│ /data  ← mounted persistent data directory  │
│ SQLite │ games │ sessions │ logs │ backups   │
└──────────────────────────────────────────────┘
```

### 8.1 Web layer

- Uses resources only through the API.
- Uses HTTPS for control operations and WebSocket for real-time events and game input.
- Stores the Session identifier, most recently acknowledged output sequence, and client message identifiers, but not authoritative game state.
- Renders structured events through DOM/Canvas/WebAudio and does not execute raw script or HTML supplied by a game.

### 8.2 API layer

The system deploys one API process, whose code should contain at least these modules:

- Identity & Authorization
- Game Package Service
- Save Service
- Session Control Plane
- Realtime Gateway
- Session Registry & Scheduler
- Administration & Audit

The API process must not keep active Sessions only in memory. After an API-process restart, the Worker Supervisor must keep Session Workers alive and rebuild Session state and local IPC connections from durable metadata in the mounted data directory.

The API process and Worker Supervisor process must be managed as independent processes by the container's process manager. Restarting the API process must not terminate the Worker Supervisor or its Session Workers.

### 8.3 Worker layer

The Worker layer should distinguish:

- **Worker Supervisor process**: runs inside the container and starts, monitors, limits, and terminates Session Worker child processes.
- **Session Worker process**: one independent operating-system process per active Session, owning one Runtime, one ConsoleSnapshot, and one Session file sandbox.

A Session Worker must continue running during an API-process restart or temporary local-IPC interruption and retain bounded output. It should re-register with `sessionId + workerEpoch` after the control channel recovers.

Every Session Worker must use an actual `SessionRoot` as its process working directory and Emuera GameRoot. At creation, Session management copies the complete validated regular-file tree of the published GameVersion; configuration, game-defined directories, saves, and temporary files all live directly in the Session's physical directory:

```text
SessionRoot/
├── CSV/              ← Session-private copy from GameVersion
├── ERB/              ← Session-private copy from GameVersion
├── resources/        ← Session-private copy from GameVersion
├── any-game-dir/     ← every other valid directory is copied too
├── sav/              ← Session-private directory, writable
├── save*.sav         → Session-private save files, writable
├── global.sav        → Session-private save files, writable
└── emuera.config     ← Session-private copy, writable
```

For legacy games that write `save*.sav` and `global.sav` at the GameRoot level, the SessionRoot must provide the corresponding Session-private writable paths. Every Session Worker must run in its own process; an Emuera Runtime must never run inside the API process or another Session Worker process.

### 8.4 Communication protocols

- Browser to API: HTTPS + WebSocket.
- API to Worker: local IPC inside the container, preferably a Unix domain socket. The protocol model must remain transport-independent for testing.
- Large static assets: read from the mounted data directory through the API rather than repeatedly transferring them through real-time messages.
- Every control command must include `sessionId`, `workerEpoch`, a command ID, and a protocol version.

## 9. Session State Machine

```text
CREATING → STARTING → RUNNING ↔ DETACHED
                         │          │
                         ├──────────┤
                         ▼          ▼
                      STOPPING → CLOSED

STARTING/RUNNING/DETACHED → CRASHED
CRASHED → RECOVERING → RUNNING       (future capability)
RUNNING/DETACHED → SUSPENDING → SUSPENDED → RESUMING  (future capability)
```

State definitions:

| State | Description |
| --- | --- |
| CREATING | The API accepted the request but has not assigned a Worker |
| STARTING | A Worker is starting and loading the game |
| RUNNING | The Worker is healthy and at least one real-time connection is attached |
| DETACHED | The Worker is healthy and no real-time connection is attached |
| STOPPING | New input is rejected while files are flushed and the Worker terminates |
| CLOSED | Closure by the user or administrator has completed |
| CRASHED | The Worker exited unrecoverably or lost its lease |
| SUSPENDED | A future persistent runtime snapshot that consumes no active Worker |

The active runtime quota counts `CREATING`, `STARTING`, `RUNNING`, `DETACHED`, and `STOPPING` states. `CREATING` reserves quota, and `STOPPING` remains counted until its Worker is released. `CLOSED`, `CRASHED`, and `SUSPENDED` do not consume active runtime quota. `DETACHED` has no browser connection but still consumes a Worker and must therefore be counted.

State transitions must be protected by database versioning or transactions. WorkerLease must use an increasing epoch; heartbeats, output, and input responses from an older epoch must be rejected.

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
  "lastSequence": 1040
}
```

If deltas remain available, the Worker/API may return `1041..current`. Otherwise it must return a complete ConsoleSnapshot at sequence `N` and stream deltas beginning at `N+1`.

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

- Users, roles, and quotas
- Game and immutable GameVersion metadata
- Session state and the current WorkerLease
- SessionRoot paths, Session lifecycle state, and save-management audit data
- Administrative policy and compatibility-report summaries

The database file must be backed up together with game files and complete Session directories. Container restarts must not clean or reconstruct existing SessionRoots.

### 11.2 Local physical file system

The container must mount one host data directory at `/data`. All games, Sessions, saves, logs, and backups must use physical files or directories under this directory:

```text
/data/
├── cloudemuera.db
├── games/{gameId}/{version}/       ← immutable GameVersion tree and Session copy source
├── sessions/{sessionId}/
│   ├── root/                       ← actual SessionRoot
│   └── metadata/
├── logs/
└── backups/
```

Session management must copy every valid regular file and directory in the published manifest and must not discard unknown content through a known-directory allowlist. Symbolic links, hard links, and special files supplied by a game package remain forbidden. A normal byte copy is the baseline; where supported, a reflink preserving copy-on-write semantics may be used with fallback to a normal copy. Hard links are forbidden. Each SessionRoot must be private, persistent, and isolated; Emuera directly reads and writes its complete game copy, configuration, temporary files, and native saves there. Object storage, remote file systems, and external file services must not be runtime or persistence dependencies. Backups must use host-file-system snapshots, directory copies, or equivalent local backup tools.

## 12. Security Requirements

- **SEC-001**: Every game package must be treated as untrusted input.
- **SEC-002**: By default, a Session Worker must not access the public network, host secrets, other users' files, or the container-management interface.
- **SEC-003**: The original GameVersion must not be exposed to a Worker. A Worker may access and write only its assigned complete SessionRoot copy and must not access another GameVersion or SessionRoot.
- **SEC-004**: CPU, memory, process count, open file count, disk, and output-rate limits must be enforced for Workers.
- **SEC-005**: Archive path traversal, symbolic-link escape, case collisions, and decompression overrun must be prevented.
- **SEC-006**: Browser rendering must encode text and attributes. Emuera HTML must be converted to supported structured nodes.
- **SEC-007**: Local Worker IPC endpoints must not be exposed outside the container and must authenticate the API service identity.
- **SEC-008**: Save-download and asset URLs must be short-lived and permission-bound, or proxied through an authorized API.
- **SEC-009**: Logs, metrics, and crash artifacts must avoid exposing credentials and unnecessary user-input content.
- **SEC-010**: Dependency and upstream interpreter updates must pass compatibility regression and security review before becoming the new runtime baseline.

## 13. Non-Functional Requirements

### 13.1 Availability and recovery

- **NFR-001**: Under normal load, reconnecting to a running Session and displaying its initial snapshot should complete within 2 seconds at P95, excluding abnormal external user-network conditions.
- **NFR-002**: Restarting the API process must not actively terminate healthy Session Workers; the Worker Supervisor must continue managing them.
- **NFR-003**: During a temporary local-IPC interruption, a Session Worker must continue running and attempt to re-register. If its output buffer reaches the limit, it must retain the latest ConsoleSnapshot.
- **NFR-004**: A Worker crash must preserve the SessionRoot as found and must not affect the GameVersion or another Session; the Session must be clearly marked `CRASHED`. A file being overwritten by the native writer is not guaranteed to remain valid.
- **NFR-005**: Arbitrary execution-point recovery and transactional recovery of every save are not initial-phase SLAs. Only persistence of SessionRoot and diagnostic availability are guaranteed.

### 13.2 Performance and capacity

- **NFR-006**: Under normal load, Session list and detail API requests should complete within 300 ms at P95, excluding large-file transfers.
- **NFR-007**: For a connected Session, transport from API input receipt to Worker receipt should complete within 200 ms at P95, excluding ERB execution time.
- **NFR-008**: Workers must batch high-frequency `PRINT` output and bound memory. A slow client must not block the interpreter loop or grow queues without limit.
- **NFR-009**: Concurrent capacity must be determined with repeatable representative-game load tests, not estimated only from idle Worker counts.
- **NFR-010**: The system must expose per-Session memory, CPU, event rate, snapshot size, and input wait time for capacity planning.

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
| Browser refresh/network loss | Session transitions to or remains DETACHED; reconnection restores the snapshot and current prompt |
| API-process restart | Worker Supervisor and Session Workers remain alive; the API locates Sessions through durable metadata |
| Short API–Worker local-IPC interruption | Worker buffers within bounds and reconnects; epoch and sequence are reconciled afterward |
| Worker crash | Session becomes CRASHED; SessionRoot is preserved as found; an in-progress native write and exact instruction state are not recoverable guarantees |
| Docker-container or host restart | Active Workers are treated as failed; GameVersions and SessionRoots in the mounted data directory remain |
| Mounted data directory unavailable | New Sessions and save writes are rejected, with an explicit persistence failure |
| Duplicate client input | Deduplicated using promptId and clientMessageId |
| Old Worker reconnects | Fenced by an older epoch and unable to produce valid new state |

## 15. MVP Acceptance Scenarios

- **AC-001**: One user starts two Sessions for the same game; their variables, display, input, and saves do not affect one another.
- **AC-002**: A user closes the page while awaiting input and logs in again after the configured test interval; the latest display and same prompt are available.
- **AC-003**: During an API restart the Worker does not exit, and the user can reconnect after API recovery.
- **AC-004**: Two users running the same GameVersion cannot access or overwrite each other's Sessions or saves.
- **AC-005**: Repeating the same input message executes it only once.
- **AC-006**: After the user explicitly closes a Session, its Worker exits within the configured bound, the Session becomes CLOSED, and later input is rejected.
- **AC-007**: After a Worker is force-killed, its Session becomes CRASHED within the heartbeat window, SessionRoot is not cleaned up, and native save files present there remain inspectable or downloadable.
- **AC-008**: A representative `1824+v18` test game completes loading, input, save, load, and primary display scenarios.
- **AC-009**: A representative current EM+EE test game runs all declared Supported features, while unsupported features produce explicit diagnostics.
- **AC-010**: A game package containing path traversal, symbolic-link escape, or archive-bomb characteristics is rejected without writing outside the sandbox.
- **AC-011**: Both desktop and mobile browsers can create a Session, submit game input, reconnect, and download a save.
- **AC-012**: During sustained high output, Worker and browser memory remain within configured bounds and reconnection still produces a consistent snapshot.
- **AC-013**: A representative game can save and load through native Emuera logic in both root-level and `sav/` layouts, while physical save files appear only in the owning Session's private area.
- **AC-014**: When two users and two Sessions of the same user run the same GameVersion, GameVersion files remain read-only, and Global saves are isolated by `User + Game + Session` rather than shared across users or Sessions.

## 16. Suggested Delivery Phases

### Phase 0: Compatibility and runtime validation

- Pin an EM+EE upstream commit.
- Separate the interpreter from WinForms/GDI+ through `IGameConsole`, `RuntimePaths`, file, clock, audio, and image abstractions.
- Build minimal v18 and EM+EE test-game suites.
- Verify that a headless single-Session Worker can run to INPUT, accept input, and use the native format to save and load in both root-level and `sav/` layouts.

### Phase 1: Single-node MVP

- One Docker container, one API process, one Worker Supervisor, SQLite, and one mounted data directory.
- One API process, one Worker Supervisor process, and one independent child process per Session inside the container.
- WebSocket reconnection, ConsoleSnapshot, save isolation, and basic Web rendering.
- Game package upload, editing, immutable publication, and baseline diagnostics.

### Phase 2: Self-hosted hardening

- Docker image, health checks, logs, filesystem backups, and upgrade procedures.
- Resource quotas, audit, metrics, backups, and an administration console.
- Broader HTML, sprite, audio, and mobile compatibility.

### Phase 3: Suspension and recovery

- Investigate interpreter snapshots at INPUT safe points.
- Support SUSPENDED/RESUMING to release active memory for long-detached Sessions.
- Support in-container Session recovery after a Worker crash only under verifiable conditions.

## 17. Open Questions

Identity is confirmed as local accounts with email-only login, revocable Cookie Sessions, and a first-administrator bootstrap that reads `.env` only for an uninitialized instance. ADR-0001 records the triggers for reconsidering OIDC. Remaining open questions:

1. What are the default per-user active runtime Session and game/save storage quotas?
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
| Malicious uploaded packages | File escape or resource exhaustion | Sandbox, read-only mounts, quotas, and upload validation |
| Permanently resident Sessions | Memory cost grows with abandoned Sessions | Active runtime quotas, admin controls, and future suspension snapshots |
| No exact recovery after Worker crash | Unsaved progress is lost and a natively overwritten file may be invalid | Whole-directory backups, game-native autosave, explicit failure semantics, and safe-point snapshot research |
| Continued upstream evolution | Difficult merges or regressions | Pinned commits, thin adapter layer, and dual compatibility suites |
| Unclear game and font licensing | Illegal hosting or redistribution | Preserve ownership metadata, require deployment authorization, and avoid public exposure by default |

## 19. References

- [Emuera.EM+EE documentation](https://evilmask.gitlab.io/emuera.em.doc/en/index.html)
- [Emuera.EM+EE changelog](https://evilmask.gitlab.io/emuera.em.doc/en/Changelog/index.html)
- [gEmuera](https://github.com/wwwXiaoHan17/gEmuera)
- [uEmuera](https://github.com/xerysherry/uEmuera)
- [lispcoc/gemuera IGameConsole](https://github.com/lispcoc/gemuera/blob/main/godot/src/Bridge/IGameConsole.cs)

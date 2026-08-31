# CloudEmuera project context

This file is for Claude Code and other automated development agents working in this repository. Before making changes, read this file, internal-docs/requirements.zh-CN.md, internal-docs/design.zh-CN.md, and the ADRs relevant to the task. If they conflict, follow the current user instruction and the requirements record, and update the internal documentation when appropriate.

## Project goal and current phase

CloudEmuera deploys the Emuera.EM+EE text-game runtime to a remote server so players can manage game packages from a desktop or mobile browser, run isolated and reconnectable Sessions, and manage native Emuera saves.

Phase 0 runtime separation and compatibility proof are complete. The project is in the Phase 1 standalone MVP implementation phase. The repository already contains the solution and application scaffolding, health/version endpoints, API/Worker/Migrator entry points, the historical Supervisor smoke path, the Session state machine and tests, React and Playwright scaffolding, UID/GID-aware Docker Compose development, pinned Emuera source, runtime fixtures, structured Console/Input, persistent private SessionRoots, SQLite migrations, game/session/save/admin pages, and license notices.

The Game library backend slice, parser-only Validator, dirfd/fsync storage, and recovery hardening are complete. P1-05 through P1-12 delivered persistent Worker management, browser realtime, native save management, and the production Session UI. P1-01 delivered the SQLite persistence baseline; P1-02 local identity, authorization, and audit; P1-03 secure package ingestion; P1-04 the single Game workspace/current-content model; and P1-13 the Worker boundary, instance capacity limits, and production Docker deployment. The next task is P1-14: complete single-container process management, signal forwarding, and recovery semantics.

## Confirmed technical plan

- Backend: .NET 10 LTS, ASP.NET Core, EF Core 10, and SQLite.
- Frontend: React 19, TypeScript 7, Vite 8, TanStack Query, and React Router.
- Browser realtime: native WebSocket; HTTP handles resources and management operations.
- Container communication: gRPC over Unix Domain Sockets between API and Worker.
- Process model: one Web/API control plane directly manages one independent Worker per active Session; there is no independent Supervisor.
- Database ownership: only the API business process accesses SQLite at runtime; the Migrator runs exclusively before API startup.
- Persistence: SQLite stores metadata; the mounted data directory stores Game workspace/current content, SessionRoots, and saves.
- Deployment: a single-container MVP and a Docker Compose development environment.
- Sandbox direction: Linux namespaces, cgroups, seccomp, and read-only/private filesystem boundaries.
- Licensing: CloudEmuera code uses Apache-2.0; Emuera.EM+EE retains its zlib/libpng license.

Do not replace a confirmed choice with an independent Supervisor, SignalR, PostgreSQL, Redis, a message queue, Kubernetes, in-process multi-Session execution, or multi-host scheduling without an ADR and verification evidence.

## Core architecture constraints

1. Each active Session has exactly one valid Worker. Incrementing epoch fencing rejects heartbeats, output, and input results from old Workers; only the current Worker may write the SessionRoot.
2. Browser disconnects do not change persistent Session state or stop the Worker. Connection count is transient Realtime Gateway state; there is no DETACHED Session state and no conversion of browser absence into suspension.
3. A Session is a persistent SessionRoot, not a one-shot Worker. CLOSED and a fully reclaimed CRASHED Session can be reopened with the same Session ID and directory. Open/close only acquire or release a Worker; later Game edits never change an existing SessionRoot.
4. The product has no GameVersion. Each Game has at most one editable workspace and one read-only current content. Validated content atomically replaces current content; there are no version lists, labels, or historical rollback resources.
5. Session creation copies the complete valid ordinary-file tree of the Game current content and records a source digest/manifest snapshot. Library content never shares writable inodes with a Session.
6. The runtime supports both root-level saves and the sav/ Emuera layout and does not define a replacement save format.
7. Workers emit only structured, validated Console events and never pass arbitrary game HTML directly to the browser.
8. Output uses monotonically increasing sequence numbers. Browser input carries workerEpoch + clientMessageId and is linearized by the Worker in the current input slot; duplicate input must not execute twice.
9. Authorization is checked at every server-side resource-operation boundary and never depends on frontend-hidden routes.
10. Uploads and file operations defend against path traversal, absolute paths, symlink escape, Unicode/case collisions, decompression bombs, and TOCTOU races.
11. Session, WorkerLease, epoch, and quota correctness cannot rely only on API memory. API exit must stop Workers within a bound, and a new API must confirm old write access is released before reconciling active Sessions as CRASHED.

## Repository layout and solution responsibilities

src/                  Product code and the pinned Emuera source
tests/                .NET unit and integration tests
e2e/                  Playwright end-to-end tests
internal-docs/        Internal requirements, design, ADRs, and development plans
docker/               Dockerfiles, Compose files, and production examples
scripts/              Development, validation, and upstream-source maintenance scripts
data/                 Local runtime data, not committed to Git

- CloudEmuera.Domain: domain entities, value objects, and pure business constraints.
- CloudEmuera.Application: use cases, ports, authorization, and transaction orchestration.
- CloudEmuera.Contracts: HTTP, WebSocket, and shared version contracts.
- CloudEmuera.Infrastructure: EF Core, filesystem, and external implementations.
- CloudEmuera.Ipc: API/Worker protobuf and gRPC contracts.
- CloudEmuera.RuntimeAdapter: platform-independent Console/Input/File/Clock/Media contracts.
- CloudEmuera.EmueraRuntime: vendored Emuera source, headless host, and platform wiring.
- CloudEmuera.Api: HTTP/WebSocket, Worker IPC, and Worker Manager host.
- CloudEmuera.Supervisor: historical P0-06 implementation; remove it after the P1-05 migration.
- CloudEmuera.Worker: single-Session runtime host.
- CloudEmuera.Migrator: database and data-layout migration.
- CloudEmuera.Web: browser client.

Keep Domain <- Application <- external implementations and RuntimeAdapter <- EmueraRuntime <- Worker. Domain must not reference EF Core, ASP.NET Core, the filesystem, or upstream UI types.

## Development environment and required commands

Use the repository scripts:

~~~bash
./scripts/dev-up.sh
./scripts/dev-down.sh
./scripts/check.sh
./scripts/verify-dev-user.sh
./scripts/verify-third-party.sh
~~~

Builds and tests must run through the development Docker environment, not a host .NET SDK, Node.js, pnpm, or an unconfigured docker compose. Full validation is ./scripts/dev-up.sh followed by ./scripts/check.sh; run ./scripts/dev-down.sh afterwards. Targeted tests must also use docker compose -f docker/compose.dev.yml and the locked/frozen restore rules.

The standard targeted commands include:

~~~bash
docker compose -f docker/compose.dev.yml run --rm api dotnet test CloudEmuera.slnx --no-restore --configuration Release
docker compose -f docker/compose.dev.yml run --rm api dotnet test tests/CloudEmuera.Domain.Tests --no-restore --configuration Release
docker compose -f docker/compose.dev.yml run --rm api dotnet test tests/CloudEmuera.RuntimeAdapter.Tests --no-restore --configuration Release --filter 'Category=RuntimePaths|Category=Architecture'
docker compose -f docker/compose.dev.yml run --rm api dotnet test tests/CloudEmuera.RuntimeCompatibility.Tests --no-restore --configuration Release --filter 'Category=RuntimeBridge'
bash -lc './scripts/test-runtime-compat.sh --scenario input-roundtrip'
~~~

All development containers and images must use the host UID/GID. New Compose services must accept CLOUDEMUERA_UID and CLOUDEMUERA_GID, set the Compose user explicitly, make HOME/caches/bind-mounted directories writable, and be covered by scripts/verify-dev-user.sh, including a real host-ownership check.

NuGet and pnpm dependencies must be locked. Update packages.lock.json or pnpm-lock.yaml when dependencies change. Do not commit bin/, obj/, node_modules/, dist/, runtime data, or secrets.

## Dev Session and runtime trace debugging

The development Compose environment stores `/data` in a named volume. The
repository's `./data` directory is not necessarily the data directory used by
the running API. Always resolve a Session from the database before opening its
files or logs; do not infer paths from the repository layout.

~~~bash
# Inspect the running services and identify the API container.
docker compose -f docker/compose.dev.yml ps
api_container="$(docker compose -f docker/compose.dev.yml ps -q api)"

# Copy the SQLite database read-only, including WAL/SHM when present. Keep the
# three files together before querying the copy.
trace_tmp="$(mktemp -d)"
docker cp "$api_container:/data/cloudemuera.db" "$trace_tmp/cloudemuera.db"
docker cp "$api_container:/data/cloudemuera.db-wal" "$trace_tmp/cloudemuera.db-wal"
docker cp "$api_container:/data/cloudemuera.db-shm" "$trace_tmp/cloudemuera.db-shm"

# Locate a Session by a stable identifier or a carefully scoped name. Record
# the state, worker fencing data, root path, and display configuration.
python3 - "$trace_tmp/cloudemuera.db" <<'PY'
import sqlite3
import sys

connection = sqlite3.connect(sys.argv[1])
connection.row_factory = sqlite3.Row
for row in connection.execute(
    """select id, name, state, worker_epoch, waiting_for_input,
              current_prompt_id, last_output_sequence, session_root_path,
              font_face_id, font_size, line_height, width_mode, custom_width
       from sessions where id = ?""",
    ("<session-id>",),
):
    print(dict(row))
PY
~~~

`session_root_path` is relative to `/data`. A Worker's runtime traces are
stored in the sibling `metadata` directory rather than inside the SessionRoot:

~~~text
/data/<session_root_path>                         # SessionRoot
/data/<session_root_path parent>/metadata/runtime-debug.jsonl
/data/<session_root_path parent>/metadata/worker-error.jsonl
~~~

Use the following evidence hierarchy when correlating a runtime or UI report:

- `runtime-debug.jsonl` is the primary structured trace for runtime behavior,
  output, and layout-related events. Use `worker-error.jsonl` for Worker
  failures. Treat `emuera.log` as supplemental rather than authoritative for
  structured Console behavior.
- Use `runtime_width` to compare configured, browser, effective, and drawable
  widths. Read font face, font size, line height, width mode, and Session paths
  from the database record being investigated.
- Use `erb_output` to map runtime output to `sourceFile`, `sourceLine`,
  `instruction`, and input-wait state; use `erb_wait` to identify input points;
  use `console_operation` to correlate output sequence, line IDs, and node
  summaries. Escaped path separators in JSON may require normalized search
  patterns.
- Treat a trace as evidence of emitted output, not necessarily complete visual
  geometry. When geometry matters, combine the trace with the generating ERB,
  the persisted display configuration, the structured node fields, and—when
  authenticated browser access is available—DOM measurements such as
  `getBoundingClientRect()`, `overflow`, `scrollHeight`, and `clientHeight`.
- For interaction reports, verify the event source and payload at every
  boundary (DOM, realtime message, IPC, adapter, and runtime), and distinguish
  a control being rendered or hit from the input being accepted and interpreted
  with the intended semantics. If a trace does not record client attempts,
  treat that as an observability gap and use boundary-level logs or a copied
  deterministic fixture rather than inferring that no input was sent.

For any layout investigation, record the Session ID, Worker epoch, relevant
trace lines, source location, display settings, coordinate units, and the
rendering boundary where the discrepancy appears. Keep diagnosis separate from
changes to the running Session, and do not attempt interactive login when the
available credentials are not valid.

## Runtime crash triage

When a Session is `CRASHED`, treat the newest fatal runtime record as the
starting point. Capture its timestamp, `workerEpoch`, `lastOutputSequence`,
phase, error code, exception type, and complete message before interpreting the
failure. A Session can retain failures from several Worker attempts; older
records are history, and a later `connection_closed:Cancelled` record is often
cleanup after the fatal event rather than the cause.

Read the raw JSONL records, not only a shortened dashboard or a final log line.
Correlate `worker-error.jsonl` with the surrounding `runtime-debug.jsonl`
events, especially `erb_output`, `erb_wait`, and `console_operation`. Use the
source file, source line, instruction, and output sequence to inspect the exact
ERB call site and its immediate caller chain. If a message is truncated, retain
the raw record and use the stack's first project-owned frame plus the local
source to recover the missing context.

Classify the failure boundary before changing code: game/ERB execution,
upstream compatibility, native or resource handling, Worker lifecycle, IPC, or
browser rendering. For native-resource failures, explicitly inspect ownership,
disposal, cache lifetime, repeated initialization, and cross-Worker static
state; lazy native failures frequently surface later than the operation that
created the invalid state. For lifecycle failures, compare epochs and sequence
numbers rather than assuming the most recent connection event is authoritative.

At compatibility boundaries, keep execution data (such as input values, paths,
and identifiers) exact, and project only display-only delimiters or whitespace
into the canonical presentation form required by the next contract. Do not
weaken a global validator just to accept one legacy display convention.

Reproduce a suspected runtime failure with the smallest deterministic fixture
that preserves the same operation order and lifecycle (including repeated
create/dispose or reopen paths when relevant). Cover the first-use path, the
repeated/lifecycle path, and a bounded failure path. Keep the live SessionRoot
and its traces read-only; make code changes and regression tests against a
copied or temporary fixture, then validate through the development Docker
environment.

## Upstream source rules

Emuera.EM+EE is stored as ordinary Git files under src/CloudEmuera.EmueraRuntime/Upstream, from commit 2175f8a629257efb08214e093704b3a3d3d06d05.

- Use a dedicated commit for the initial import and every upstream upgrade; never follow a floating branch.
- Prefer stable contracts in CloudEmuera.RuntimeAdapter and real interpreter wiring in CloudEmuera.EmueraRuntime.
- Record modifications in MODIFICATIONS.md and retain prominent notices in changed upstream files.
- Update UPSTREAM.md, RuntimeBaseline, validation scripts, third-party notices, and compatibility records together.
- Retain all original zlib/libpng and bundled-component copyright and license notices.

## Tests and definition of done

Every implementation task must have executable validation in internal-docs/development-plan.zh-CN.md and must satisfy these conditions:

1. New behavior has an automated test or repeatable validation script.
2. Test names or comments map to the relevant requirement IDs.
3. The normal path, boundary conditions, and one major failure path are covered.
4. Changes involving concurrency, filesystems, authorization, or recovery include race, fault, or malicious-input tests.
5. ./scripts/check.sh passes.
6. Documentation, protocol versions, migrations, and license notices are updated with the change.

Prefer domain unit tests, runtime compatibility tests, protocol contract tests, filesystem security tests, component integration tests, browser tests, fault injection, performance, and visual regression. Never disable warnings, vulnerability audits, or analyzers globally just to make checks pass.

## Development order

Follow the numbered order in internal-docs/development-plan.zh-CN.md. Phase 0 and P1-01 through P1-13 are complete; the immediate priority is P1-14: complete API/Worker process management, signal forwarding, reclamation, and recovery on the verified production image and Compose Migrator gate. P1-02 automated identity validation must remain isolated from the manually maintained docker/.env, ./data, and Compose project.

When a decision is pending, create an ADR before implementation with context, options, decision, consequences, and a verification plan. Do not silently encode a product or security decision in temporary code.

## Git commit conventions

Every commit must satisfy all of the following:

1. Include a DCO signoff. Use git commit -s; the final message must contain Signed-off-by: <name> <email> matching the Git committer identity.
2. Use <type>(<scope>): <summary>. The scope is required; the type must be feat, fix, docs, refactor, test, build, ci, chore, perf, style, or revert. Use an imperative summary without a period and keep it at most 72 characters.
3. Put a blank line between title and body. Explain context, behavior changes, or verification when needed, and use Git trailer format for footers.
4. Keep one logical change per commit and verify the staged contents and signoff before committing.

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

# Contributing

## Development workflow

1. Create a short-lived branch from `main`.
2. Keep each change focused and add tests for state machines, protocols, and security boundaries.
3. When changing a requirement ID, check the corresponding internal requirement record.
4. When changing HTTP, WebSocket, or IPC behavior, update the machine contract and compatibility tests.
5. Run `./scripts/check.sh` before submitting.

## Code conventions

- Use nullable reference types in C# and treat compiler warnings as errors.
- Enable TypeScript strict mode and do not use unbounded arrays in realtime state paths.
- Never log passwords, authentication tokens, or complete user input.
- Vendored upstream source may be modified directly, but must retain its original license/copyright notices and be recorded in `src/CloudEmuera.EmueraRuntime/MODIFICATIONS.md`.
- Every new dependency must document its purpose, license, and why the standard library is insufficient.

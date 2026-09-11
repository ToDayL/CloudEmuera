# CloudEmuera production deployment

[English](deployment.md) | [中文](deployment.zh-CN.md)

This guide covers the production Compose deployment in `docker/compose.yml`. For the shortest
path from a checkout to a running instance, start with the [Quick Start](../README.md#deploy-once-with-docker)
in the repository README.

CloudEmuera is intended for self-hosted instances and trusted game packages. The current MVP is not
an internet-facing sandbox for untrusted users or untrusted game code.

## Prerequisites

- Docker 28 or newer
- Docker Compose v2
- A host directory or Docker named volume with enough space for the game library, SessionRoots,
  saves, and SQLite data

The production Compose file runs the Migrator before the API in one container. Do not start a
separate Migrator or API service against the same data directory.

## Prepare `docker/.env`

From the repository root:

```bash
cd docker
cp .env.example .env
```

The production Compose file reads `docker/.env`. Keep this file out of version control and replace
the temporary administrator password after the first login.

### Required bootstrap settings

Compose requires these three values to be present on every production start. The application uses
them only while a fresh data directory is in the `BOOTSTRAP_REQUIRED` state. The first login uses
the configured email address; the bootstrap password may be any non-blank value, while the password
chosen after login must be at least eight characters long.

```dotenv
CLOUDEMUERA_BOOTSTRAP_ADMIN_USERNAME=admin
CLOUDEMUERA_BOOTSTRAP_ADMIN_EMAIL=you@example.com
CLOUDEMUERA_BOOTSTRAP_ADMIN_PASSWORD=change-this-password
```

### Common production settings

The following table covers the settings normally needed for a production deployment. Values not
listed here use application or Compose defaults; the repository template is
[`docker/.env.example`](../docker/.env.example).

| Variable | Default | Purpose |
| --- | --- | --- |
| `CLOUDEMUERA_DATA_PATH` | Docker volume `cloudemuera-data` | Host directory to bind to the persistent `/data` directory. |
| `CLOUDEMUERA_UID` / `CLOUDEMUERA_GID` | `0` / `0` | Container user and group. Set these for a bind mount so files belong to the intended host account. |
| `CLOUDEMUERA_HTTP_BIND_ADDRESS` | `127.0.0.1` | Host address for the HTTP port. Use `0.0.0.0` only for intentional direct LAN/public exposure. |
| `CLOUDEMUERA_HTTP_PORT` | `28647` | Host port exposed by Docker. Change this if the host port is already in use. |
| `CLOUDEMUERA_CONTAINER_PORT` | `28647` | Port used by the application inside the container; normally leave it unchanged. |
| `CLOUDEMUERA_SECURITY_SECURE_COOKIES` | `false` | Set to `true` when an HTTPS reverse proxy is the public entrypoint. |
| `CLOUDEMUERA_PRODUCTION_IMAGE` | `cloudemuera:local` | Optional image name when using a pre-built image instead of the local build. |
| `CLOUDEMUERA_MEMORY_LIMIT` | `2g` | Optional whole-container memory limit. |
| `CLOUDEMUERA_PIDS_LIMIT` | `512` | Optional whole-container process limit. |

The `CLOUDEMUERA_*` capacity, realtime, and Worker settings are deployment-level tuning options,
not normally required for a first deployment. The production Compose file uses the application
defaults for them.

## Persistent data and mounts

`/data` is the application's persistent directory. It contains the SQLite database, game content,
SessionRoots, saves, authentication keys, and other persistent state. Back up the complete data
directory together, especially the database and `/data/keys`.

### Docker named volume (default)

Leave `CLOUDEMUERA_DATA_PATH` unset. Compose creates and manages the `cloudemuera-data` named
volume, and the production container runs as root by default. This is the simplest deployment and
does not require `CLOUDEMUERA_UID` or `CLOUDEMUERA_GID`.

### Host bind mount

Use a dedicated host directory when you need the data to live at a known path or to be managed by
host backup tooling. Relative paths are resolved from the `docker/` directory; an absolute path is
recommended.

Set the path and the host account IDs in `docker/.env`:

```dotenv
CLOUDEMUERA_DATA_PATH=/srv/cloudemuera-data
CLOUDEMUERA_UID=1000
CLOUDEMUERA_GID=1000
```

Replace `1000` with the values returned by `id -u` and `id -g` for the host account that should own
the data, then create and prepare the directory before starting Compose:

```bash
sudo mkdir -p /srv/cloudemuera-data
sudo chown 1000:1000 /srv/cloudemuera-data
```

Compose does not recursively change bind-mount ownership. The directory must already be writable
by the configured UID/GID. Use a dedicated data directory; do not mount a host home directory, the
repository, a Docker socket, or a directory containing unrelated secrets.

## Network exposure

The host port binds to loopback by default:

```dotenv
CLOUDEMUERA_HTTP_BIND_ADDRESS=127.0.0.1
CLOUDEMUERA_HTTP_PORT=28647
```

For direct access from another device on a trusted local network, set the bind address explicitly:

```dotenv
CLOUDEMUERA_HTTP_BIND_ADDRESS=0.0.0.0
```

Then recreate the service with `docker compose up -d` and open
`http://<server-address>:28647`. Use a firewall to limit who can reach the port.

For an internet-facing deployment, keep the application bound to loopback and put an HTTPS reverse
proxy in front of it:

```dotenv
CLOUDEMUERA_HTTP_BIND_ADDRESS=127.0.0.1
CLOUDEMUERA_SECURITY_SECURE_COOKIES=true
```

The reverse proxy must forward both HTTP requests and WebSocket upgrades. The application does not
perform HTTPS redirects; the proxy owns TLS termination and public protocol policy. Change only
`CLOUDEMUERA_HTTP_PORT` when the host port is occupied. Change `CLOUDEMUERA_CONTAINER_PORT` only
when intentionally changing the application port inside the container.

## Start, stop, and update

Run these commands from the `docker/` directory:

```bash
docker compose up -d --build
docker compose ps
docker compose logs -f api
```

Environment changes are applied by running `docker compose up -d` again. To stop the service while
preserving all data:

```bash
docker compose stop
```

`docker compose down` removes the container and network but preserves the named data volume. Do not
use `docker compose down -v` unless you intentionally want to delete the managed data volume.

To update a source checkout, pull the desired revision and rebuild:

```bash
git pull
docker compose up -d --build
```

The Migrator runs automatically before the API starts. Keep a consistent backup of the complete
`/data` directory before upgrades.

## Backups and restore

Stop CloudEmuera before copying persistent data so the SQLite database and filesystem data are
consistent. For a bind mount, back up the configured `CLOUDEMUERA_DATA_PATH`; for a named volume,
use your Docker volume backup procedure for `cloudemuera-data`. Restore the complete directory or
volume, including `/data/keys`, while the service is stopped, then start it again.

Do not delete the volume as part of routine cleanup. A lost `/data/keys` directory invalidates
existing login cookies; the accounts and application data remain in the database, but users must
log in again.

## Troubleshooting

Validate Compose interpolation without printing the resolved configuration:

```bash
docker compose config --quiet
```

- If Compose reports a missing bootstrap variable, set all three bootstrap values in `docker/.env`.
- If a bind mount reports permission denied, verify the directory owner, `CLOUDEMUERA_UID`, and
  `CLOUDEMUERA_GID`; Compose does not repair ownership.
- If the host port is already in use, change `CLOUDEMUERA_HTTP_PORT` and run `docker compose up -d`.
- If the service is behind HTTPS, set `CLOUDEMUERA_SECURITY_SECURE_COOKIES=true`; keep it `false`
  for plain HTTP.

.PHONY: dev-up dev-down verify-dev-user restore build test check

dev-up:
	./scripts/dev-up.sh

dev-down:
	./scripts/dev-down.sh

verify-dev-user:
	./scripts/verify-dev-user.sh

restore:
	docker compose -f docker/compose.dev.yml run --rm api dotnet restore CloudEmuera.slnx

build:
	docker compose -f docker/compose.dev.yml run --rm api dotnet build CloudEmuera.slnx --no-restore

test:
	docker compose -f docker/compose.dev.yml run --rm api dotnet test CloudEmuera.slnx --no-restore

check:
	./scripts/check.sh

.PHONY: build test test-unit test-integration test-e2e compose-full compose-fast migrate run-api run-workers lint security-scan

build:
	dotnet build

test:
	dotnet test

test-unit:
	dotnet test tests/DubbingPlatform.UnitTests

test-integration:
	dotnet test tests/DubbingPlatform.IntegrationTests

test-e2e:
	dotnet test tests/DubbingPlatform.E2ETests

compose-full:
	docker compose --profile full config && docker compose --profile full up -d --build

compose-fast:
	docker compose --profile fast config && docker compose --profile fast up -d --build

migrate:
	dotnet ef database update --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api

run-api:
	dotnet run --project src/DubbingPlatform.Api

run-workers:
	dotnet run --project src/DubbingPlatform.Workers

lint:
	dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true

generate-api:
	node tools/generate-client.mjs

check-api-drift:
	node tools/check-api-drift.mjs

security-scan:
	trivy config . || echo "trivy not installed - install to scan"

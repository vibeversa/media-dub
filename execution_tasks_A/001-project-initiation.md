# Task 1 — Project Initiation

## Goal

Create a buildable .NET 10 modular-monolith solution with all required projects, build props, tooling, and repository conventions so `dotnet build` succeeds with zero placeholder failures.

## Context

Greenfield repository. Binding decisions: .NET 10 runtime, ASP.NET Core API, .NET Worker Services, EF Core + Npgsql + EFCore.NamingConventions, MassTransit + RabbitMQ, StackExchange.Redis, AWSSDK.S3, Serilog, OpenTelemetry + Prometheus, Polly, FluentValidation, xUnit + Testcontainers + WireMock.Net + Microsoft.AspNetCore.Mvc.Testing. Architecture: modular monolith API plus separate worker hosts by workload class; PostgreSQL is system of record; Redis ephemeral only. Internal IDs are ULID-compatible UUIDs stored as PostgreSQL `uuid`; public IDs are prefixed strings (`tenant_`, `prj_`, `run_`, `asset_`, `upl_`, `seg_`, `spk_`, `ctx_`, `voice_`, `art_`, `cnt_`, `exe_`, `prov_`, `qc_`, `rev_`, `exp_`, `job_`). Database naming snake_case. Timeline values integer milliseconds. Delivery is phased but no production capability removed.

## Starting State

Empty repository. No `.sln`, no `src/`, no `tests/`. .NET 10 SDK is installed. No database or broker required for this task.

## Scope

Must implement: solution file, 10 projects, Directory.Build.props, global.json, .editorconfig, .gitignore, local tool manifest with dotnet-ef, all NuGet references, placeholder Program/DbContext stubs so every project compiles, configuration placeholder files.
Out of scope: Dockerfiles, docker-compose, Makefile, real domain entities, DbContext modeling, messaging, storage, providers, API endpoints, workers logic, tests logic, CI/CD, K8s.

## Instructions

1. Create `DubbingPlatform.sln` at repo root.
2. Create projects with exact paths and types:
   - `src/DubbingPlatform.Domain` — classlib net10.0
   - `src/DubbingPlatform.Contracts` — classlib net10.0
   - `src/DubbingPlatform.Application` — classlib net10.0
   - `src/DubbingPlatform.Infrastructure` — classlib net10.0
   - `src/DubbingPlatform.Api` — webapi net10.0
   - `src/DubbingPlatform.Workers` — worker net10.0
   - `tests/DubbingPlatform.UnitTests` — xunit
   - `tests/DubbingPlatform.IntegrationTests` — xunit
   - `tests/DubbingPlatform.ContractTests` — xunit
   - `tests/DubbingPlatform.E2ETests` — xunit
   Add all to solution via `dotnet sln add`.
3. Enforce references:
   - Domain → nothing
   - Contracts → nothing
   - Application → Domain, Contracts
   - Infrastructure → Application, Domain, Contracts
   - Api → Application, Infrastructure, Contracts
   - Workers → Application, Infrastructure, Contracts
   - All test projects may reference all src projects. Verify with `dotnet list reference`; fail if violation.
4. Create `Directory.Build.props` at root:
   ```xml
   <Project>
     <PropertyGroup>
       <TargetFramework>net10.0</TargetFramework>
       <Nullable>enable</Nullable>
       <ImplicitUsings>enable</ImplicitUsings>
       <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
       <AnalysisLevel>latest</AnalysisLevel>
       <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
     </PropertyGroup>
   </Project>
   ```
5. Create `global.json`: `{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}`.
6. Create `.editorconfig` with root=true, charset=utf-8, LF, 4-space C#, `dotnet_sort_system_directives_first=true`, `dotnet_separate_import_directive_groups=false`.
7. Create `.gitignore` (standard .NET: bin/, obj/, .vs/, TestResults/, *.user, .env).
8. Create `.config/dotnet-tools.json` with `dotnet-ef` version `9.0.0` (or latest compatible with .NET 10 at execution time; record chosen version in file).
9. Add NuGet packages:
   - Domain: none extra
   - Contracts: none extra (MassTransit.Abstractions if needed for message interfaces — decision: no MassTransit ref in Contracts to keep dependency-free; use plain records)
   - Application: FluentValidation, Polly
   - Infrastructure: Microsoft.EntityFrameworkCore, Npgsql.EntityFrameworkCore.PostgreSQL, EFCore.NamingConventions, MassTransit, MassTransit.RabbitMQ, MassTransit.EntityFrameworkCore, StackExchange.Redis, AWSSDK.S3, Serilog, Serilog.AspNetCore, OpenTelemetry, OpenTelemetry.Exporter.Prometheus.AspNetCore, Polly
   - Api: Serilog.AspNetCore, Microsoft.AspNetCore.Authentication.JwtBearer, Swashbuckle.AspNetCore, FluentValidation.AspNetCore, OpenTelemetry.Exporter.Prometheus.AspNetCore, OpenTelemetry.Extensions.Hosting
   - Workers: MassTransit, Serilog, OpenTelemetry
   - Tests: xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, Testcontainers.PostgreSql, Testcontainers.RabbitMq, Testcontainers.Redis, Testcontainers.Minio (or generic), WireMock.Net, Microsoft.AspNetCore.Mvc.Testing, FluentAssertions (allowed), Moq or NSubstitute (choose Moq, document choice)
10. Create minimal compilable stubs: each src project gets one placeholder class (e.g., `Placeholder.cs` with empty static class); Api gets `Program.cs` with `WebApplication.CreateBuilder` + `Run`; Workers gets `Program.cs` with `Host.CreateApplicationBuilder` + `Run`. No warnings.
11. Create placeholder `src/DubbingPlatform.Api/appsettings.json`, `appsettings.Development.json`, and `src/DubbingPlatform.Workers/appsettings.json` with empty sections: ConnectionStrings, RabbitMq, Redis, Storage, Providers, Media, Timing, Retry, Quota, RateLimit, Observability, Auth, Retention, Privacy, Features, Deployment (values filled in later tasks; must exist as `{}` objects).
12. Run `dotnet tool restore`, `dotnet restore`, `dotnet build`.

## Requirements

- R1: Solution contains exactly the 10 listed projects.
- R2: Dependency rules as above hold.
- R3: `Directory.Build.props` has net10.0, nullable, implicit usings, warnings-as-errors, latest analysis, code-style-in-build.
- R4: `global.json` pins .NET 10 SDK.
- R5: Tool manifest restores dotnet-ef.
- R6: All listed NuGet families present.
- R7: No project fails compilation.
- R8: Placeholder appsettings contain all 15 named sections.

## Edge Cases and Error Handling

- Missing .NET 10 SDK: fail with message stating required SDK version from global.json; do not downgrade TFM.
- Duplicate project names: remove and recreate; ensure sln has no duplicates.
- NuGet restore offline: retry once, then fail with explicit package name; do not silently omit packages.
- Tool manifest conflict: keep existing tools, add dotnet-ef; do not delete other tools if present.
- Warnings: must be zero; warnings-as-errors on, so fix all nullable warnings in stubs.

## Security and Safety Requirements

- No secrets in code or appsettings (use empty strings / placeholders).
- No hardcoded connection strings with credentials.
- `.gitignore` must exclude `.env` and `appsettings.Production.json` secrets if created later (include pattern).

## Testing

- No functional tests required in this task. Create one smoke test per test project to verify harness:
  - `tests/DubbingPlatform.UnitTests/SmokeTests.cs` — `[Fact] Smoke_Passes()` asserts `true`.
  - Same for IntegrationTests, ContractTests, E2ETests (distinct namespaces).
- Run `dotnet test --filter Smoke_Passes` must pass 4/4.

## Validation

```bash
dotnet tool restore
dotnet restore
dotnet build
dotnet test --filter Smoke_Passes
dotnet list src/DubbingPlatform.Api/DubbingPlatform.Api.csproj reference
dotnet list src/DubbingPlatform.Infrastructure/DubbingPlatform.Infrastructure.csproj reference
```

Expected: build succeeds with 0 warnings, 0 errors; 4 smoke tests pass.

## Completion Criteria

- `DubbingPlatform.sln` exists with 10 projects.
- `Directory.Build.props`, `global.json`, `.editorconfig`, `.gitignore`, `.config/dotnet-tools.json` exist.
- All projects build.
- Placeholder appsettings with 15 sections exist in Api and Workers.

## Traceability

- Plan Section 1 actions 1–9, 18; files list solution/src/tests/props/global/editorconfig/gitignore/tools; Assumptions 1–4, 8–9; dependency rules.

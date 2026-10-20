# FhirServer

An open-source [HL7 FHIR](https://hl7.org/fhir/) server written in [F#](https://fsharp.org/) on ASP.NET Core.

| Build | Status |
|-------|--------|
| Local | `make build` · `make test` |

FhirServer helps you stand up a standards-oriented FHIR API quickly: create, read, update, search, history, validate, and related operations—backed by an envelope store (PostgreSQL or SQLite) and schema validation generated from official HL7 packages (no Firely / `Hl7.Fhir.*` runtime dependency).

The goal is a small, readable codebase that developers can run locally, extend, and contribute to—similar in spirit to other open FHIR implementations, but focused on F#, hexagonal architecture, and generated FHIR Schema validation.

## Why FhirServer?

- **FHIR R5** resources and `CapabilityStatement` from a generated catalog
- **REST API** for CRUD, search (`_include` / `_revinclude` / chaining), history, `$validate`, `$everything`, `$lastn`, `$dump`, `$export`, and Bundle transaction/batch
- **Envelope persistence** — one current + history table per resource type; pluggable SQL dialects
- **Schema validation** — cardinality, choice types, bindings, and primitives via `FhirServer.Validation` + embedded schemas
- **Optional BALP audit** — persist `AuditEvent` for REST operations when enabled
- **Developer-friendly** — Docker Compose for Postgres, Makefile targets, Fantomas + FSharpLint, in-process E2E tests

## Architecture (layers)

```
Hosting / composition   AppHost.fs, Program.fs
HTTP adapters           CapabilityStatement, Codec, Auth, Audit middleware
Application             ResourceHandlers (use cases)
Domain                  types, SearchEngine, ResourceValidator, IResourceStore
Persistence             EnvelopeResourceStore + Postgres / SQLite dialects
Schemas & validation    packages/FhirServer.Schemas · FhirServer.Validation
```

Persistence is a port (`IResourceStore`) with a shared envelope implementation; dialects only supply SQL. Schema JSON and the validator are produced by [`generator/`](generator/README.md) from `hl7.fhir.r5.core` and synced into `packages/`.

See [docs/Architecture.md](docs/Architecture.md) for details.

## Documentation

| Topic | Doc |
|-------|-----|
| Getting started | [docs/GettingStarted.md](docs/GettingStarted.md) |
| Architecture | [docs/Architecture.md](docs/Architecture.md) |
| Features & API | [docs/Features.md](docs/Features.md) |
| Configuration | [docs/Configuration.md](docs/Configuration.md) |
| Development | [docs/Development.md](docs/Development.md) |
| Schema generator | [generator/README.md](generator/README.md) |
| Schemas package | [packages/FhirServer.Schemas/README.md](packages/FhirServer.Schemas/README.md) |
| Validation package | [packages/FhirServer.Validation/README.md](packages/FhirServer.Validation/README.md) |

## Quick start

**Requirements:** [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker (optional, for Postgres).

```bash
# Postgres (recommended)
make db-up
# appsettings: Store:Provider=postgres
# ConnectionStrings:Fhir=Host=localhost;Port=5432;Database=fhir;Username=fhir;Password=fhir

dotnet run --project src/FhirServer
# → http://localhost:5050
```

SQLite works out of the box for local experiments (`Store:Provider=sqlite`, default `data/fhir.db`).

```bash
curl -s http://localhost:5050/metadata | jq .software
curl -s -X POST http://localhost:5050/Patient \
  -H 'Content-Type: application/fhir+json' \
  -d '{"resourceType":"Patient","name":[{"family":"Garcia","given":["Ana"]}],"gender":"female"}' \
  | jq .
```

More examples: [docs/GettingStarted.md](docs/GettingStarted.md).

## Repository layout

```
src/FhirServer/          ASP.NET Core FHIR API
packages/                Embedded R5 schemas + validation engine
generator/               FhirFsGen — StructureDefinition → schema + F# validator
tests/FhirServer.E2E/    In-process end-to-end tests
docs/                    Project documentation
compose.yaml             Postgres for local development
```

## Status & roadmap

This project is an **MVP**. It is useful for learning, demos, and as a foundation for a custom FHIR service. It is **not** a certified, production-hardened clinical platform out of the box.

**Not yet in scope:** full IG/profile StructureDefinition conformance, SMART on FHIR, `$import`, Subscriptions, `$document`.

Contributions that move those areas forward are welcome—see [CONTRIBUTING.md](CONTRIBUTING.md).

## Contributing

We welcome issues, discussions, and pull requests.

- Read [CONTRIBUTING.md](CONTRIBUTING.md)
- Follow the [Code of Conduct](CODE_OF_CONDUCT.md)
- Report security issues privately via [SECURITY.md](SECURITY.md)

## License

This project is licensed under the [MIT License](LICENSE).

## Trademark

FHIR® is the registered trademark of HL7 and is used with the permission of HL7.

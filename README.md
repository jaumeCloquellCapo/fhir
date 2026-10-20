# FhirServer

An open-source [HL7 FHIR](https://hl7.org/fhir/) server written in [F#](https://fsharp.org/) on ASP.NET Core.

| Build | Status |
|-------|--------|
| API | [![API](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/api.yml/badge.svg)](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/api.yml) |
| Generator | [![Generator](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/generator.yml/badge.svg)](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/generator.yml) |
| Local | `make build` · `make test` |

FhirServer helps you stand up a standards-oriented FHIR API quickly: create, read, update, search, history, validate, and related operations—backed by a PostgreSQL envelope store and schema validation generated from official HL7 packages (no Firely / `Hl7.Fhir.*` runtime dependency).

The goal is a small, readable codebase that developers can run locally, extend, and contribute to—similar in spirit to other open FHIR implementations, but focused on F#, hexagonal architecture, and generated FHIR Schema validation.

## Why FhirServer?

- **FHIR R5** resources and `CapabilityStatement` from a generated catalog
- **REST API** — CRUD, history, Bundle transaction/batch, `$validate`, `$everything`, `$document`, `$lastn`, `$dump`, `$export` / `$import`
- **Search (SQL-indexed)** — catalog from R5 `SearchParameter` codegen; `_include` / `_revinclude`, multi-hop chaining, nested `_has`, composites, `_filter` subset, modifiers (`:exact` / `:contains` / `:missing` / `:not` / `:above` / `:below`), date & quantity prefixes (incl. `ap`)
- **Terminology MVP** — `$expand`, `$lookup`, `$validate-code` over stored `ValueSet` / `CodeSystem`
- **SMART App Launch MVP** — discovery + `/auth/token` (`client_credentials`); Bearer/Basic scopes enforced with auth on by default
- **Subscriptions MVP** — rest-hook notifications + local `/_hooks/{id}` capture sink
- **Envelope persistence** — one current + history table per resource type on PostgreSQL
- **Schema validation** — cardinality, choices, bindings, primitives, FHIRPath constraint subset, and **core constraint profiles** (`meta.profile`) via generated `FhirServer.Validation` + embedded schemas
- **Optional BALP audit** — persist `AuditEvent` for REST operations when enabled
- **Swagger UI** — interactive OpenAPI docs at `/swagger` for every endpoint
- **Developer-friendly** — Docker Compose for Postgres, Makefile targets, Fantomas + FSharpLint, in-process E2E tests

## Architecture (layers)

```
Hosting / composition   AppHost.fs, Program.fs
HTTP adapters           CapabilityStatement, Codec, Auth, Audit middleware
Application             ResourceHandlers (use cases)
Domain                  types, SearchEngine, ResourceValidator, IResourceStore
Persistence             EnvelopeResourceStore + SQL dialect (Postgres)
Schemas & validation    packages/FhirServer.Schemas · FhirServer.Validation
```

Persistence is a port (`IResourceStore`) with a shared envelope implementation backed by PostgreSQL. Schema JSON and the validator are produced by [`generator/`](generator/README.md) from `hl7.fhir.r5.core` and synced into `packages/`.

See [docs/Architecture.md](docs/Architecture.md) for details.

## Documentation

| Topic | Doc |
|-------|-----|
| Getting started | [docs/GettingStarted.md](docs/GettingStarted.md) |
| **Swagger UI** | [http://localhost:5050/swagger](http://localhost:5050/swagger) (local) |
| Architecture | [docs/Architecture.md](docs/Architecture.md) |
| Features & API | [docs/Features.md](docs/Features.md) |
| Configuration | [docs/Configuration.md](docs/Configuration.md) |
| Development | [docs/Development.md](docs/Development.md) |
| Schema generator | [generator/README.md](generator/README.md) |
| Schemas package | [packages/FhirServer.Schemas/README.md](packages/FhirServer.Schemas/README.md) |
| Validation package | [packages/FhirServer.Validation/README.md](packages/FhirServer.Validation/README.md) |

## Quick start

**Requirements:** [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker (for Postgres).

```bash
make db-up
# ConnectionStrings:Fhir=Host=localhost;Port=5432;Database=fhir;Username=fhir;Password=fhir

dotnet run --project src/FhirServer
# → http://localhost:5050
```

## Swagger UI

Interactive OpenAPI docs for every FHIR endpoint (CRUD, search, history, `$validate`, `$export`, Bundle, …):

| Surface | URL |
|---------|-----|
| **Swagger UI** | [http://localhost:5050/swagger](http://localhost:5050/swagger) |
| OpenAPI JSON | [http://localhost:5050/swagger/v1/swagger.json](http://localhost:5050/swagger/v1/swagger.json) |

`dotnet run` opens Swagger in the browser by default (`launchUrl: swagger`). Auth is **on** by default — use **Authorize** in the UI (HTTP Basic `fhir` / `change-me`) or a SMART Bearer from `/auth/token`.

```bash
curl -s -u fhir:change-me http://localhost:5050/metadata | jq .software
curl -s -u fhir:change-me -X POST http://localhost:5050/Patient \
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

**In scope today (see [docs/Features.md](docs/Features.md)):** R5 CRUD + SQL search (SearchParameter catalog, modifiers / `_has` / composites / `_filter` subset / date·quantity), terminology ops, SMART authorize + PKCE + `client_credentials` with patient/user/system scopes (v2 c/r/u/d/s), multi-client + optional ephemeral DCR (`Smart:EnableDcr`), Subscriptions rest-hook (loopback/`/_hooks` only by default), core profile + constraint validation from `make generate-r5-all`.

**Not yet:** EHR `launch` handshake / JWT access tokens; subscription webhook retries/signing; full FHIRPath search extractors; packaging arbitrary external IGs beyond R5 core profiles.

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

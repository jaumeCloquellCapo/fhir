# FhirServer

An open-source [HL7 FHIR](https://hl7.org/fhir/) backend for healthcare applications, written in [F#](https://fsharp.org/) on ASP.NET Core.

| Build | Status |
|-------|--------|
| API | [![API](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/api.yml/badge.svg)](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/api.yml) |
| Generator | [![Generator](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/generator.yml/badge.svg)](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/generator.yml) |
| Local | `make build` · `make test` |

FhirServer is a **FHIR R5 server** you can run locally or embed in your own stack: REST API, PostgreSQL persistence, schema validation from official HL7 packages, search, terminology, SMART auth, and subscriptions—without a Firely / `Hl7.Fhir.*` runtime dependency.

Use it to prototype clinical apps, learn FHIR end-to-end, or as a foundation for a custom FHIR service.

## What you get

| Component | What it does |
|-----------|----------------|
| **FHIR REST API** | CRUD, history, Bundle transaction/batch, `$validate`, `$everything`, `$document`, `$lastn`, `$dump`, `$export` / `$import` |
| **Search** | SQL-indexed SearchParameters; `_include` / `_revinclude`, chaining, `_has`, composites, `_filter` subset, modifiers & date/quantity prefixes |
| **PostgreSQL store** | JSONB envelopes: current + history tables per resource type, txid versioning, side search indexes |
| **Validation** | FHIR Schema from `hl7.fhir.r5.core` — cardinality, choices, bindings, primitives, FHIRPath constraint subset, core profiles via `meta.profile` |
| **Terminology** | `$expand`, `$lookup`, `$validate-code` over stored `ValueSet` / `CodeSystem` |
| **SMART App Launch** | Discovery + authorize (PKCE) + `/auth/token`; Bearer/Basic scopes (patient/user/system, v2 c/r/u/d/s) |
| **Subscriptions** | Rest-hook notifications + local `/_hooks/{id}` capture sink |
| **Audit (optional)** | BALP-oriented `AuditEvent` for REST operations |
| **Swagger UI** | Interactive OpenAPI at `/swagger` |

Full interaction matrix: [docs/Features.md](docs/Features.md).

## Quick start

**Requirements:** [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker (Postgres).

```bash
make db-up
dotnet run --project src/FhirServer
# → http://localhost:5050
```

Auth is **on** by default — HTTP Basic `fhir` / `change-me`, or a SMART Bearer from `/auth/token`.

```bash
curl -s -u fhir:change-me http://localhost:5050/metadata | jq .software
curl -s -u fhir:change-me -X POST http://localhost:5050/Patient \
  -H 'Content-Type: application/fhir+json' \
  -d '{"resourceType":"Patient","name":[{"family":"Garcia","given":["Ana"]}],"gender":"female"}' \
  | jq .
```

More: [docs/GettingStarted.md](docs/GettingStarted.md) · Swagger: [http://localhost:5050/swagger](http://localhost:5050/swagger)

## Platform overview

```
┌──────────────────────────────────────────────────────────────┐
│                     FHIR REST API                            │
│   CRUD · Search · History · Bundle · $ops · Bulk · Subs      │
├────────────┬─────────────┬──────────────┬────────────────────┤
│ Validation │ Terminology │ SMART / Auth │ Audit (optional)   │
│ FHIR Schema│ ValueSet /  │ scopes ·     │ BALP AuditEvent    │
│ + profiles │ CodeSystem  │ PKCE · Basic │                    │
├────────────┴─────────────┴──────────────┴────────────────────┤
│              PostgreSQL  ·  JSONB envelopes + search indexes │
└──────────────────────────────────────────────────────────────┘
```

How storage, validation, and request flow fit together: [docs/Architecture.md](docs/Architecture.md).

Schemas and the validator are produced offline by [`generator/`](generator/README.md) from `hl7.fhir.r5.core` and synced into `packages/`.

## Documentation

**Index:** [docs/SUMMARY.md](docs/SUMMARY.md) — API · Auth · Database · Validation · Config

| Topic | Doc |
|-------|-----|
| Getting started | [docs/GettingStarted.md](docs/GettingStarted.md) |
| Features & API | [docs/Features.md](docs/Features.md) |
| Architecture | [docs/Architecture.md](docs/Architecture.md) |
| Configuration | [docs/Configuration.md](docs/Configuration.md) |
| Development | [docs/Development.md](docs/Development.md) |
| Schema generator | [generator/README.md](generator/README.md) |
| Schemas / validation packages | [packages/README.md](packages/README.md) |

## Repository layout

```
src/FhirServer/          FHIR API (ASP.NET Core)
packages/                Embedded R5 schemas + validation engine
generator/               HL7 package → FHIR Schema + F# validator
tests/FhirServer.E2E/    In-process end-to-end tests
docs/                    Product & developer docs
compose.yaml             Postgres for local development
```

## Status & roadmap

**MVP** — useful for learning, demos, and as a foundation for a custom FHIR service. Not a certified, production-hardened clinical platform out of the box.

**In scope today:** R5 CRUD + SQL search, terminology ops, SMART authorize + PKCE + `client_credentials` with scopes, multi-client + optional DCR, Subscriptions rest-hook (loopback/`/_hooks` by default), core profile validation from `make generate-r5-all`. Details in [docs/Features.md](docs/Features.md).

**Not yet:** EHR `launch` / JWT access tokens; subscription webhook retries/signing; full FHIRPath search extractors; packaging arbitrary external IGs beyond R5 core profiles.

Contributions welcome — [CONTRIBUTING.md](CONTRIBUTING.md).

## Contributing

- [CONTRIBUTING.md](CONTRIBUTING.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)
- Security: [SECURITY.md](SECURITY.md)

## License

[MIT License](LICENSE).

## Trademark

FHIR® is the registered trademark of HL7 and is used with the permission of HL7.

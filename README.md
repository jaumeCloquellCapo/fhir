# FhirServer

An open-source [HL7 FHIR](https://hl7.org/fhir/) backend for healthcare applications, written in [F#](https://fsharp.org/) on ASP.NET Core.

| Build | Status |
|-------|--------|
| API | [![API](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/api.yml/badge.svg)](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/api.yml) |
| Generator | [![Generator](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/generator.yml/badge.svg)](https://github.com/jaumeCloquellCapo/fhir/actions/workflows/generator.yml) |
| Local | `make build` · `make test` |

FhirServer is a **FHIR R5-only server** (`hl7.fhir.r5.core@5.0.0`) you can run locally or embed in your own stack: REST API, PostgreSQL persistence, schema validation from official HL7 packages, search, terminology, SMART auth / authorization, multi-tenant ORGBAC, and subscriptions. Earlier FHIR releases are not supported yet — see [Docs/FhirVersion.md](Docs/FhirVersion.md).

Use it to prototype clinical apps, learn FHIR end-to-end, or as a foundation for a custom FHIR service.

## What you get

| Component | What it does |
|-----------|----------------|
| **FHIR REST API** | CRUD, history, Bundle transaction/batch, `$validate`, `$everything`, `$document`, `$lastn`, `$dump`, `$export` / `$import` |
| **Search** | SQL-indexed SearchParameters; `_include` / `_revinclude`, chaining, `_has`, composites, `_filter` subset, modifiers & date/quantity prefixes |
| **PostgreSQL store** | JSONB envelopes: current + history tables per resource type, txid versioning, side search indexes |
| **Validation** | FHIR Schema from `hl7.fhir.r5.core` — cardinality, choices, bindings, primitives, FHIRPath constraint subset, core profiles via `meta.profile` |
| **Terminology** | `$expand`, `$lookup`, `$validate-code` over stored `ValueSet` / `CodeSystem` |
| **Auth** | Always-on on protected routes: HTTP Basic and/or SMART Bearer; discovery + authorize (PKCE) + `/auth/token`; optional DCR |
| **Authz** | SMART v2 scopes (`patient` / `user` / `system`, permissions `c`/`r`/`u`/`d`/`s`); patient-compartment filtering; client `AllowedScopes` intersection |
| **Multi-tenancy (ORGBAC)** | Organization-scoped isolation via Postgres RLS; root API = exact tenant; `/Organization/{orgId}/fhir/...` = hierarchy (`partOf`); `owned` / `shared` / `system-shared` modes |
| **Subscriptions** | Rest-hook notifications + local `/_hooks/{id}` capture sink |
| **Audit (optional)** | BALP-oriented `AuditEvent` for REST operations |
| **Swagger UI** | Interactive OpenAPI at `/swagger` |

Full interaction matrix: [Docs/Features.md](Docs/Features.md).

## Quick start

**Requirements:** [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker (Postgres).

```bash
make db-up
dotnet run --project FhirServer
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

More: [Docs/GettingStarted.md](Docs/GettingStarted.md) · Swagger: [http://localhost:5050/swagger](http://localhost:5050/swagger)

## Platform overview

```
┌──────────────────────────────────────────────────────────────────┐
│                        FHIR REST API                             │
│   CRUD · Search · History · Bundle · $ops · Bulk · Subs          │
├────────────┬─────────────┬────────────────┬──────────────────────┤
│ Validation │ Terminology │ Auth / Authz   │ Multi-tenancy        │
│ FHIR Schema│ ValueSet /  │ Basic · SMART  │ ORGBAC · RLS         │
│ + profiles │ CodeSystem  │ scopes · PKCE  │ org hierarchy        │
├────────────┴─────────────┴────────────────┴──────────────────────┤
│  Audit (optional) · PostgreSQL JSONB envelopes + search indexes  │
└──────────────────────────────────────────────────────────────────┘
```

How storage, validation, and request flow fit together: [Docs/Architecture.md](Docs/Architecture.md).

Schemas and the validator are produced offline by [`Generator/`](Generator/README.md) from `hl7.fhir.r5.core` and synced into `Packages/`.

## Documentation

**Index:** [Docs/SUMMARY.md](Docs/SUMMARY.md) — API · Auth · Database · Validation · Config

| Topic | Doc |
|-------|-----|
| Getting started | [Docs/GettingStarted.md](Docs/GettingStarted.md) |
| Features & API | [Docs/Features.md](Docs/Features.md) |
| Auth / SMART / scopes | [Docs/Features.md#smart-on-fhir](Docs/Features.md#smart-on-fhir) · [Docs/Configuration.md#auth-basic--smart-bearer](Docs/Configuration.md#auth-basic--smart-bearer) |
| Multi-tenancy (ORGBAC) | [Docs/Features.md#organization-based-access-orgbac](Docs/Features.md#organization-based-access-orgbac) |
| Architecture | [Docs/Architecture.md](Docs/Architecture.md) |
| Configuration | [Docs/Configuration.md](Docs/Configuration.md) |
| Development | [Docs/Development.md](Docs/Development.md) |
| Schema generator | [Generator/README.md](Generator/README.md) |
| Schemas / validation packages | [Packages/README.md](Packages/README.md) |

## Repository layout

```
FhirServer/              FHIR API (ASP.NET Core)
Packages/                Embedded R5 schemas + validation engine
Generator/               HL7 package → FHIR Schema + F# validator
Tests/FhirServer.E2E/    In-process end-to-end tests
Docs/                    Product & developer docs
compose.yaml             Postgres for local development
```

## Status & roadmap

**MVP** — useful for learning, demos, and as a foundation for a custom FHIR service. Not a certified, production-hardened clinical platform out of the box.

**In scope today:** R5 CRUD + SQL search, terminology ops, SMART authorize + PKCE + `client_credentials` with scopes (authz), multi-client + optional DCR, ORGBAC multi-tenancy (exact + hierarchical org APIs, Postgres RLS), Subscriptions rest-hook (loopback/`/_hooks` by default), core profile validation from `make generate-r5-all`. Details in [Docs/Features.md](Docs/Features.md).

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

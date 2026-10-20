# Features & API

Supported FHIR HTTP interactions for the current MVP. Content type: **`application/fhir+json`**.

Base URL (local default): `http://localhost:5050`

## System

| Method | Path | Notes |
|--------|------|-------|
| GET | `/` | Short plain-text tip |
| GET | `/health` | Health check (`status: pass`) |
| GET | `/$version` | Build / version JSON |
| GET | `/metadata` | `CapabilityStatement` |

## Instance & type interactions

| Method | Path | Notes |
|--------|------|-------|
| GET | `/{type}/{id}` | Read — **410** if soft-deleted |
| POST | `/{type}` | Create — **422** if schema-invalid; supports `If-None-Exist` |
| PUT | `/{type}/{id}` | Update — `If-Match` → **412** on version mismatch |
| PATCH | `/{type}/{id}` | JSON Patch (root-level subset) |
| DELETE | `/{type}/{id}` | Soft-delete via history — **200** + body |
| GET | `/{type}?…` | Search (see below) |
| GET | `/{type}/{id}/_history` | Instance history |
| GET | `/{type}/_history` | Type history |
| GET | `/_history` | System history |
| GET | `/{type}/{id}/_history/{vid}` | VRead |
| POST | `/` | Bundle **transaction** / **batch** (URN + conditional `Type?…`) |

## Operations

| Method | Path | Notes |
|--------|------|-------|
| POST | `/$validate` | Validate without persist — always **200** + `OperationOutcome` |
| POST | `/{type}/$validate` | Same, scoped to type |
| GET | `/{type}/{id}/$everything` | Patient clinical summary `Bundle` |
| GET | `/Observation/$lastn` | Last N Observations per code (`max`, `patient`, …) |
| GET | `/{type}/$dump` | NDJSON stream of all resources of that type |
| GET/POST | `/$export?_type=Patient,Observation` | Bulk export — **202** + `Content-Location` |
| GET | `/$export-status/{jobId}` | Bulk export status JSON (`output` URLs) |
| GET | `/$export-files/{jobId}/{type}` | NDJSON file for one type in a job |

## Search (subset)

| Feature | Examples / notes |
|---------|------------------|
| Common | `_id`, `_lastUpdated`, `_count`, `_sort` |
| `_include` | `Observation?_include=Observation:patient` |
| `_revinclude` | `Patient?_revinclude=Observation:patient` |
| Chaining | `Observation?subject.name=Garcia`, `patient.family=…` (best-effort for well-known refs) |

Resource-specific search parameters are advertised in `/metadata` where implemented. Full FHIR search (modifiers, composites, arbitrary reverse chains) is **not** claimed.

## Validation

- Structural / cardinality / choice / binding checks via generated FHIR Schema + `SchemaEngine`
- Create/update fail with **422** + `OperationOutcome` when invalid
- `$validate` does not persist

## Audit (optional)

When `Audit:Enabled=true`, the server records BALP-oriented `AuditEvent` resources for REST operations (skips `/AuditEvent` to avoid loops). See [Configuration.md](Configuration.md).

## Out of scope (roadmap)

- Full IG / profile StructureDefinition conformance
- SMART on FHIR
- `$import` / `$document`
- Subscriptions
- XML (`application/fhir+xml`) as a first-class format

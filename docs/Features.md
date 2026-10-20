# Features & API

Supported FHIR HTTP interactions for the current MVP. Content type: **`application/fhir+json`**.

Base URL (local default): `http://localhost:5050`

## Interactive docs

| Surface | URL |
|---------|-----|
| Swagger UI | `/swagger` |
| OpenAPI JSON | `/swagger/v1/swagger.json` |

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
| POST | `/$import` · `/$load` | Sync NDJSON import (create/update) → `Parameters` |
| GET | `/Patient/{id}/$document` | FHIR Document Bundle (`Composition` + clinical resources) |
| GET/POST | `/ValueSet/$expand` · `/ValueSet/{id}/$expand` | Expand stored ValueSet (+ CodeSystem concepts) |
| GET/POST | `/CodeSystem/$lookup` | Lookup code → `Parameters` |
| GET/POST | `/ValueSet/$validate-code` · `/{id}/$validate-code` | Validate code membership → `Parameters` |

## Search (subset)

Indexed in SQL (`search_string` / `search_token` / `search_ref` / `search_date` / `search_quantity`) on write; queries use SQL filters + `COUNT`/`LIMIT`/`OFFSET`.

| Feature | Examples / notes |
|---------|------------------|
| Common | `_id`, `_lastUpdated` (`ge`/`le`/… on envelope `ts`), `_count`, `_sort` |
| Patient | `family`, `given`, `name`, `gender`, `birthdate`, `identifier` |
| Observation | `status`, `code`, `patient` / `subject`, `date`, `value-quantity` |
| Clinical set | `patient` / `subject` (Encounter, Condition, …) |
| Modifiers | `:exact`, `:contains`, `:missing`, token `:not` |
| `_include` | `Observation?_include=Observation:patient` |
| `_revinclude` | `Patient?_revinclude=Observation:patient` |
| Chaining | `Observation?subject.name=Garcia`, `patient.family=…` |
| `_has` | `Patient?_has:Observation:patient:code=…` |

Resource-specific search parameters are advertised in `/metadata` where implemented. Composites / full `_filter` grammar remain **out of scope**.

## Validation

- Structural / cardinality / choice / binding checks via generated FHIR Schema + `SchemaEngine`
- Create/update fail with **422** + `OperationOutcome` when invalid
- `$validate` does not persist

## Audit (optional)

When `Audit:Enabled=true`, the server records BALP-oriented `AuditEvent` resources for REST operations (skips `/AuditEvent` to avoid loops). See [Configuration.md](Configuration.md).

## SMART on FHIR (MVP)

| Method | Path | Notes |
|--------|------|-------|
| GET | `/.well-known/smart-configuration` | Discovery document |
| POST | `/auth/token` | `client_credentials` (Basic or form client_id/secret) |
| GET | `/auth/authorize` | Stub **501** (use client_credentials) |

When `Auth:Enabled=true`, API calls accept **Bearer** (SMART token) or **HTTP Basic**. Bearer tokens enforce **system** scopes (`system/*.read`, `system/Patient.write`, …); Basic keeps full access.

## Subscriptions (MVP)

Create an active `Subscription` with `channelType.code=rest-hook` and `endpoint` pointing at `/_hooks/{sinkId}` (or any URL). Resource create/update/delete/patch posts a `subscription-notification` Bundle.

| Method | Path | Notes |
|--------|------|-------|
| POST/GET/DELETE | `/_hooks/{sinkId}` | Local capture sink for e2e / demos |

## Validation depth

- Base resource schemas + **FHIRPath constraint subset** + **core constraint profiles** are generated into `FhirServer.Schemas` / `FhirServer.Validation` (`make generate-r5-all`).
- When `meta.profile` names a loaded core profile, the server re-validates against that profile snapshot schema.
- Custom/external IGs beyond the R5 core package are not packaged by default (run the generator against additional packages to add them).

## Out of scope (roadmap)

- Full SMART authorize / PKCE / EHR launch UX
- External webhook reliability (retries, signing)
- XML (`application/fhir+xml`) as a first-class format

# Features & API

What FhirServer exposes as a FHIR R5 backend. Content type: **`application/fhir+json`**. Base URL (local): `http://localhost:5050`.

### At a glance

| Area | Capabilities |
|------|----------------|
| **Core FHIR** | CRUD, conditional create, history / vread, Bundle transaction & batch, soft-delete |
| **Search** | Catalog SearchParameters, modifiers, `_include` / `_revinclude`, chaining, `_has`, composites, `_filter` subset |
| **Operations** | `$validate`, `$everything`, `$document`, `$lastn`, `$dump`, `$export` / `$import` |
| **Terminology** | `$expand`, `$lookup`, `$validate-code` |
| **Access** | SMART App Launch MVP (PKCE + `client_credentials`), HTTP Basic, optional org/tenant RLS |
| **Reactive** | Subscriptions rest-hook + `/_hooks` sink |
| **Quality** | FHIR Schema validation + core profiles; optional BALP `AuditEvent` |

Interaction tables below; gaps in [Out of scope](#out-of-scope-roadmap).

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

Param definitions come from R5 `SearchParameter` resources via `make generate-r5-all` / `--search-catalog` → `GeneratedSearchCatalog.fs` (Supported extractor subset). Engine, indexer, and `/metadata` share that catalog.

| Feature | Examples / notes |
|---------|------------------|
| Common | `_id`, `_lastUpdated` (`ge`/`le`/… on envelope `ts`), `_count`, `_sort` |
| Catalog | Supported params for Patient, Organization, Observation, Encounter, clinical set, … (see `/metadata`) |
| Modifiers | `:exact`, `:contains`, `:missing`, token `:not` / `:above` / `:below` |
| Date / quantity | prefixes `eq`/`ne`/`gt`/`lt`/`ge`/`le`/`sa`/`eb`/`ap` (±10%, dates ±12h floor) |
| Composites | e.g. `Observation?code-value-quantity=loinc\|8480-6$gt100\|…` (`$`-joined components) |
| `_filter` | subset: `and` / `or` / `not (…)` / `eq|ne|gt|lt|ge|le|co|sw` on indexed params |
| `_include` | `Observation?_include=Observation:patient` |
| `_revinclude` | `Patient?_revinclude=Observation:patient` |
| Chaining | multi-hop e.g. `Observation?subject.organization.name=…` |
| `_has` | nested e.g. `Organization?_has:Patient:organization:_has:Observation:patient:code=…` |

Unsupported SearchParameter expressions are omitted from `/metadata`. Full `_filter` paths (`param[filter].chain`, `_has:` inside `_filter`) remain out of scope.

## Validation

- Structural / cardinality / choice / binding checks via generated FHIR Schema + `SchemaEngine`
- Create/update fail with **422** + `OperationOutcome` when invalid
- `$validate` does not persist

## Audit (optional)

When `Audit:Enabled=true`, the server records BALP-oriented `AuditEvent` resources for REST operations (skips `/AuditEvent` to avoid loops). See [Configuration.md](Configuration.md).

## SMART on FHIR

| Method | Path | Notes |
|--------|------|-------|
| GET | `/.well-known/smart-configuration` | Discovery (includes `registration_endpoint`) |
| GET/POST | `/auth/authorize` | `authorization_code` + PKCE S256; `patient=` for standalone launch (else HTML form) |
| POST | `/auth/token` | `client_credentials` (system scopes only) or `authorization_code` + `code_verifier` |
| POST | `/auth/register` | Dynamic client registration (in-memory; lost on restart) |

Auth is always required on protected routes. API calls accept **Bearer** (SMART token) or **HTTP Basic**. Bearer tokens enforce **patient/**, **user/**, and **system/** scopes with SMART v2 permissions (`c`/`r`/`u`/`d`/`s`; aliases `read`→`rs`, `write`→`cud`). Patient-context tokens filter the Patient compartment on search/read/write. Basic uses `Auth:BasicScopes` (default `system/*.cruds`) with the same scope checks.

Clients are configured under `Smart:Clients` (legacy `Smart:ClientId` / `ClientSecret` / `Scopes` still load as one client). Requested scopes are intersected with each client's `AllowedScopes`.

### Organization-based access (ORGBAC)

Aidbox-compatible hierarchical organization access, enforced with **Postgres RLS** (`SET LOCAL ROLE fhir_app`, `app.tenant_id`, `app.orgbac_mode`):

- **Root API** (`/Patient`, …): token `organization` (from `Smart:Clients[].OrganizationId`) uses **exact** tenant isolation.
- **Org-scoped API** (`/Organization/{orgId}/fhir/...`): hierarchical visibility via `Organization.partOf` (parent sees own + descendants). Token `organization` must match `{orgId}` (Basic admin / unrestricted may use any org path).
- **Modes** (envelope `tenant_mode` + Aidbox meta extensions):
  - `owned` (default) — visible to owning org and ancestors on the org API
  - `shared` — `https://aidbox.app/tenant-resource-mode` = `shared`; readable by descendant org APIs; writable only from the owning org API
  - `system-shared` — create on **root** API only; readable from every org API; read-only there
- Creates under an org API stamp `https://aidbox.app/tenant-organization-id`. HTTP Basic stays **unrestricted** unless `Auth:OrganizationId` is set. Resource ids are globally unique across tenants.

## Subscriptions (MVP)

Create an active `Subscription` with `channelType.code=rest-hook` and `endpoint` pointing at `/_hooks/{sinkId}` (or any URL). Resource create/update/delete/patch posts a `subscription-notification` Bundle.

| Method | Path | Notes |
|--------|------|-------|
| POST/GET/DELETE | `/_hooks/{sinkId}` | Local capture sink for e2e / demos |

## Validation depth

- Base resource schemas + **FHIRPath constraint subset** (`exists` / `empty` / `count` / `where` / …) + **core constraint profiles** are generated into `FhirServer.Schemas` / `FhirServer.Validation` (`make generate-r5-all`).
- When `meta.profile` names a loaded core profile, the server re-validates against that profile snapshot schema.
- `$validate?profile=` (or `_profile`) forces the same core-profile checks without requiring `meta.profile`.
- Bindings: **required** → fatal; **extensible** → warning; **preferred** / **example** → information (when the ValueSet is embedded).
- Relative `Reference.reference` values (`Type/id`) are resolved against the store on `$validate` and on create/update → **warning** if missing (writes are not blocked by dangling refs).
- Custom/external IGs beyond the R5 core package are not packaged by default (run the generator against additional packages to add them).

## Out of scope (roadmap)

- EHR launch token handshake / JWT access tokens / refresh tokens
- Persisting dynamically registered clients across restarts
- External webhook reliability (retries, signing)
- Full FHIRPath (incl. `%terminologies`, `select`/`implies`, …) and external terminology server
- QuestionnaireResponse vs Questionnaire validation; slicing / fixed / pattern
- Full FHIRPath search extractors / `_filter` embedded chain & `_has`
- XML (`application/fhir+xml`) as a first-class format

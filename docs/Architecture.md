# Architecture

FhirServer uses a hexagonal / DDD-style layout so HTTP, persistence, and validation stay replaceable.

## Layers

```
┌─────────────────────────────────────────────────────────┐
│  Hosting          Program.fs · AppHost.fs (DI, routes)  │
├─────────────────────────────────────────────────────────┤
│  Infrastructure   HTTP · Capability · Persistence       │
├─────────────────────────────────────────────────────────┤
│  Application      ResourceHandlers · Audit recorder     │
├─────────────────────────────────────────────────────────┤
│  Domain           Types · Search · Validator · Ports    │
└─────────────────────────────────────────────────────────┘
         │                              │
         ▼                              ▼
   FhirServer.Validation         IResourceStore
   FhirServer.Schemas            EnvelopeResourceStore
                                 + Postgres / SQLite dialect
```

| Area | Responsibility |
|------|----------------|
| **Domain** | FHIR resource JSON model helpers, catalog, search logic, `IResourceStore` port, validation facade |
| **Application** | Use cases (`create`, `read`, `search`, Bundle, …) and BALP audit event building |
| **Infrastructure** | ASP.NET endpoints, auth, audit middleware, SQL dialects + migrations |
| **Packages** | Embedded `*.schema.json`, valuesets, generated catalog and schema validator |
| **Generator** | Offline tool: HL7 package → IR → schemas + F# validator source |

## Persistence model

Resources are stored as **envelopes**, not as fully relational FHIR element columns:

- One **current** table per resource type (e.g. `patient`)
- One **history** table (e.g. `patient_history`) with PK `(id, txid)`
- Global `txid_counter` → FHIR `meta.versionId` / ETag
- Body column holds JSON (`jsonb` on Postgres; text on SQLite), omitting duplicated id / meta / resourceType where the codec owns those fields

`EnvelopeResourceStore` implements `IResourceStore` once. `IEnvelopeDialect` / `IEnvelopeSession` supply dialect-specific SQL. Migrations share `MigrationRunner` (`PostgresMigrations` / `SqliteMigrations`).

```mermaid
erDiagram
    schema_migrations {
        integer version PK
        text name
        timestamptz applied_at
    }
    txid_counter {
        integer id PK
        bigint value
    }
    resource {
        text id PK
        bigint txid
        timestamptz cts
        timestamptz ts
        text resource_type
        text status
        jsonb resource
    }
    resource_history {
        text id PK
        bigint txid PK
        timestamptz cts
        timestamptz ts
        text resource_type
        text status
        jsonb resource
    }
    txid_counter ||--o{ resource : allocates
    txid_counter ||--o{ resource_history : allocates
    resource ||--o{ resource_history : versions
```

## Validation pipeline

1. Official package `hl7.fhir.r5.core` (via [`generator/`](../generator/README.md))
2. StructureDefinition snapshot → nested IR → `*.schema.json` + valuesets
3. `make sync-schemas` copies artifacts into `packages/`
4. At runtime, `ResourceValidator` → `SchemaEngine.validateJson`
5. Writes return **422** on schema failure; `$validate` returns an `OperationOutcome` (HTTP 200)

Full IG / profile StructureDefinition conformance is not implemented yet (canonical `meta.profile` format is checked).

## Request flow (create)

```mermaid
sequenceDiagram
  participant Client
  participant HTTP as HTTP adapter
  participant App as ResourceHandlers
  participant Val as SchemaEngine
  participant Store as EnvelopeResourceStore

  Client->>HTTP: POST /Patient
  HTTP->>App: create(store, type, body)
  App->>Val: validateJson
  alt invalid
    App-->>HTTP: 422 OperationOutcome
  else valid
    App->>Store: insert envelope + txid
    Store-->>App: resource with id / meta
    App-->>HTTP: 201 + Location
  end
```

## Design principles

- Prefer **small modules and explicit ports** over a large framework surface
- Keep **one store implementation**; dialects are SQL-only
- Generate **schemas from HL7 packages** instead of hand-maintaining hundreds of resource shapes
- Stay **Firely-free** at runtime so the validation story is owned in-repo

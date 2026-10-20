---
name: act-swagger
description: >-
  Keep Swagger/OpenAPI docs in sync when changing HTTP endpoints or FHIR resource
  handlers. Use whenever adding, removing, or modifying routes in AppHost.fs,
  ResourceHandlers, Responses, search/operations endpoints, or OpenApiDocs.fs.
  MUST BE USED whenever an endpoint or resource surface changes.
---

# Act Swagger

Whenever you touch an **endpoint** or a **resource** handler, update Swagger in the same change. Do not leave undocumented routes.

## When this applies

- New / changed / removed `MapGet` / `MapPost` / `MapPut` / `MapDelete` / `MapMethods` in `FhirServer/AppHost.fs`
- Changes to `ResourceHandlers`, responses, status codes, or request/response shapes used by those routes
- New FHIR operations (`$…`), search params exposed via HTTP, bulk/export/import, metadata, SMART, or system routes
- Edits to `FhirServer/Infrastructure/Http/OpenApiDocs.fs`

## Required workflow

1. Implement the endpoint / handler change.
2. Pipe the route builder through the matching `OpenApiDocs.document*` helper **before** `|> ignore`.
3. Pick the correct **tag**, **summary**, **description**, and **response profile**.
4. If no helper fits (new status codes, content type, or schema), extend `OpenApiDocs.fs` first, then document the route.
5. Confirm every new/changed route has OpenAPI metadata (no bare `Map*` without `OpenApiDocs`).

## Documentation pattern

```fsharp
app.MapGet(
    "/path",
    Func<...>(fun ... -> ...)
)
|> OpenApiDocs.documentFhir
    [| OpenApiDocs.TagCrud |]
    "Short summary"
    "One-line description of behavior, status codes, and notable headers."
|> ignore
```

### Helpers → use when

| Helper | Typical use |
|--------|-------------|
| `documentText` | Plain text (e.g. root) |
| `documentJson` | Non-FHIR JSON (health, version, SMART token) |
| `documentFhir` | FHIR resource read / single resource JSON |
| `documentFhirSearch` | Search / history Bundle |
| `documentFhirCreate` | Create → 201 |
| `documentFhirUpdate` | Update / patch |
| `documentFhirDelete` | Delete |
| `documentFhirValidate` | `$validate` → OperationOutcome |
| `documentFhirBundle` | Transaction/batch Bundle |
| `documentNdjson` | NDJSON streams |
| `documentBulkAccepted` | Bulk kickoff → 202 |
| `documentNoContent` | 204 / empty |

### Tags (reuse literals from `OpenApiDocs`)

`TagSystem` · `TagMetadata` · `TagValidate` · `TagCrud` · `TagSearch` · `TagHistory` · `TagOperations` · `TagBulk` · `TagBundle`

Prefer an existing tag. Add a new `[<Literal>]` tag in `OpenApiDocs` only when the surface is a distinct API group.

## Checklist (copy and tick)

```
- [ ] Every new/changed Map* has OpenApiDocs.document*
- [ ] Tag / summary / description match real behavior
- [ ] Response profile matches status codes + content type
- [ ] OpenApiDocs extended if profile/schema/tag missing
- [ ] Removed routes: no orphan docs / dead helpers left unused without reason
```

## Do not

- Ship a route without Swagger metadata
- Invent ad-hoc `.WithTags` / `.WithSummary` outside `OpenApiDocs` (keep docs centralized)
- Change response shapes without updating the matching `document*` profile or schemas in `OpenApiDocs.fs`

## Key files

- `FhirServer/AppHost.fs` — route registration + `OpenApiDocs.document*` pipes
- `FhirServer/Infrastructure/Http/OpenApiDocs.fs` — helpers, tags, schemas, `configureSwagger`
- `FhirServer/Application/ResourceHandlers.fs` — resource behavior reflected in descriptions

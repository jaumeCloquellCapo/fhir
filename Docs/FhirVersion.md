# FHIR version

**This repository currently targets FHIR R5 only** (`hl7.fhir.r5.core@5.0.0`).

| Surface | R5 source of truth |
|---------|-------------------|
| CapabilityStatement `fhirVersion` | `ResourceCatalog.fhirVersion` ← `GeneratedResourceCatalog` |
| Schemas / bindings / constraints | `Packages/FhirServer.Schemas` (from `make generate-r5-all`) |
| Schema validator | `Packages/FhirServer.Validation` |
| SearchParameter catalog | `GeneratedSearchCatalog` |
| Resource type list | `GeneratedResourceCatalog.supportedResourceTypes` |
| Generator package pin | `Generator/FhirFsGen/PackageCache.fs` → `hl7.fhir.r5.core` / `5.0.0` |

Wire format: **`application/fhir+json`**. Clients should treat the server as R5 (see `/metadata`).

## R5-specific behaviour (examples)

Do **not** assume R4 shapes in new code or tests unless you are explicitly adding multi-version support:

- Questionnaire `item.type`: use **`coding`** (R4 `choice` / `open-choice` were removed). Open answers use **`answerConstraint`** (`optionsOrString`, etc.).
- QuestionnaireResponse checks in `QuestionnaireValidation` follow R5; a few R4 aliases (`choice`, `open-choice`) are tolerated only as compatibility shims on the *response* side, not as create-time Questionnaire codes.
- Search catalogs, profiles, and ValueSet bindings embedded in packages are R5.

## Adding earlier versions later (R4, …)

When multi-version lands, expect at least:

1. **Separate generated artifacts** per release (e.g. `out/r4` + `out/r5`, or versioned packages) — do not overwrite R5 schemas with R4.
2. **Explicit version selection** at the edge (`fhirVersion` / path / tenant config) — one process may serve multiple versions, but each request must bind to one catalog + validator.
3. **No silent mixing** of R4 and R5 StructureDefinitions in the same `SchemaEngine` load.
4. Update this doc, `/metadata`, OpenAPI description, and generator pins (`PackageCache`) together.

Until then: **all runtime validation, search, and resource catalogs are R5.**

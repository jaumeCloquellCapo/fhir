# Wave 1 — parallel schema generation (historical)

> **Superseded.** Full R5 concrete resource generation is complete: see [`TODO-R5.md`](TODO-R5.md) (**158 / 158**). Prefer `make generate-r5-all` over per-resource agent waves.

This file records the first parallel batch that unblocked the pipeline.

Progress at wave close: **9 / 158** (then finished via `--all`).

| Resource | Status |
|----------|--------|
| Patient | ✅ |
| Observation | ✅ |
| Encounter | ✅ |
| Practitioner | ✅ |
| Organization | ✅ |
| Account | ✅ |
| ActivityDefinition | ✅ |
| ActorDefinition | ✅ |
| AllergyIntolerance | ✅ |

Schemas: `Generator/out/r5/schemas/` → synced into `Packages/FhirServer.Schemas/Data/`.

# Packages

Runtime FHIR Schema artifacts used by the server for validation and the resource catalog.

| Project | Role | Doc |
|---------|------|-----|
| **[FhirServer.Schemas](FhirServer.Schemas/)** | Embedded R5 `*.schema.json` + profiles + `valuesets.json` + catalog | [README](FhirServer.Schemas/README.md) |
| **[FhirServer.Validation](FhirServer.Validation/)** | Generated validator + `SchemaEngine` (Firely-free) | [README](FhirServer.Validation/README.md) |

Produced from official HL7 packages by [`Generator/`](../Generator/README.md); see [Docs/Architecture.md](../Docs/Architecture.md) for how validation fits the platform.

```bash
make sync-schemas      # copy Generator/out/r5 → these packages
make generate-r5-all   # regenerate all R5 resources, then sync
```

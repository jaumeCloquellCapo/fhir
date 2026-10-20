# Packages

Independent libraries consumed by `src/FhirServer`:

| Project | Role | Doc |
|---------|------|-----|
| **[FhirServer.Schemas](FhirServer.Schemas/)** | Embedded R5 `*.schema.json` + `valuesets.json` + catalog | [README](FhirServer.Schemas/README.md) |
| **[FhirServer.Validation](FhirServer.Validation/)** | `GeneratedSchemaValidator` + `SchemaEngine` | [README](FhirServer.Validation/README.md) |

How artifacts are **generated**: [`generator/README.md`](../generator/README.md) (UML pipeline).

Refresh from the generator:

```bash
make sync-schemas      # copy generator/out/r5 → these packages
make generate-r5-all   # regenerate all R5 resources, then sync
```

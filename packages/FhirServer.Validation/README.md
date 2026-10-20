# FhirServer.Validation

Firely-free FHIR Schema validator. Compiles the generated engine and loads R5 schemas / value sets from **`FhirServer.Schemas`**.

> Source of truth for schemas and the validator template: [`generator/`](../../generator/README.md). Do not edit `Generated/GeneratedSchemaValidator.fs` by hand — regenerate + `make sync-schemas`.

## Layout

```
FhirServer.Validation/
  SchemaEngine.fs                         # thin entry: load + validateJson
  Generated/GeneratedSchemaValidator.fs   # copied from generator/out/r5
  FhirServer.Validation.fsproj

FhirServer.Schemas/                       # sibling package
  Data/*.schema.json                      # embedded resources
  Data/valuesets.json
  Generated/GeneratedResourceCatalog.fs
  SchemaPackage.fs                        # assembly marker
```

## How artifacts are generated (UML)

The validator package does **not** run the generator. It **consumes** outputs produced by `FhirFsGen` and synced by Make.

```mermaid
flowchart TB
  subgraph Generator["generator/FhirFsGen"]
    PKG["hl7.fhir.r5.core@5.0.0<br/>StructureDefinition JSON"]
    IR["SnapshotToIr → ResourceSchema IR"]
    SCH["EmitSchemaJson → *.schema.json"]
    VS["ValueSetLoader → valuesets.json"]
    TPL["ValidatorTemplate.fs"]
    EV["EmitValidator → GeneratedSchemaValidator.fs"]
  end

  subgraph Sync["make sync-schemas"]
    CP1["cp schemas + valuesets + catalog<br/>→ FhirServer.Schemas"]
    CP2["cp GeneratedSchemaValidator.fs<br/>→ FhirServer.Validation/Generated/"]
  end

  subgraph Runtime["at process start"]
    SE["SchemaEngine"]
    GSV["GeneratedSchemaValidator"]
    SP["SchemaPackage.assembly"]
  end

  PKG --> IR --> SCH
  IR --> VS
  TPL --> EV
  SCH --> CP1
  VS --> CP1
  EV --> CP2
  CP1 --> SP
  CP2 --> GSV
  SP --> SE
  GSV --> SE
```

Full generation sequence and IR class diagram: [`generator/README.md`](../../generator/README.md#how-generation-works-uml).

### What each emit step produces

| Generator step | Output | Landed in |
|----------------|--------|-----------|
| `EmitSchemaJson.writeResource` | `schemas/<Type>.schema.json` | `FhirServer.Schemas/Data/` |
| `EmitSchemaJson.writeValueSets` | `valuesets.json` | `FhirServer.Schemas/Data/` |
| `EmitFsharp.writeCatalog` | `GeneratedResourceCatalog.fs` | `FhirServer.Schemas/Generated/` |
| `EmitValidator.writeSchemaValidatorFs` | copy of `ValidatorTemplate.fs` | `Generated/GeneratedSchemaValidator.fs` |

`EmitValidator` does not codegen logic from IR — it ships the shared engine template. Schemas drive behaviour at runtime.

## Runtime validation (UML)

```mermaid
sequenceDiagram
  participant API as ResourceHandlers
  participant RV as ResourceValidator
  participant SE as SchemaEngine
  participant GSV as GeneratedSchemaValidator
  participant SCH as FhirServer.Schemas<br/>embedded JSON

  Note over SE,SCH: once at module init
  SE->>GSV: loadFromEmbeddedAssembly(SchemaPackage.assembly)
  GSV->>SCH: read *.schema.json + valuesets.json
  GSV-->>SE: Map schemas, Map valueSets

  API->>RV: validateJson(expectedType?, body)
  RV->>SE: validateJson(body)
  SE->>GSV: validateJson(schemas, valueSets, body)
  GSV->>GSV: parse JSON → lookup resourceType schema
  GSV->>GSV: cardinality, required, arrays,<br/>choices [x], primitives,<br/>required bindings, nested types
  GSV-->>SE: ValidationIssue list
  SE-->>RV: ValidationIssue list
  RV-->>API: map + optional resourceType check
```

### Component view

```mermaid
flowchart LR
  subgraph Server["src/FhirServer"]
    RH["ResourceHandlers<br/>$validate / write"]
    RV["ResourceValidator"]
  end

  subgraph Val["FhirServer.Validation"]
    SE["SchemaEngine"]
    GSV["GeneratedSchemaValidator"]
  end

  subgraph Sch["FhirServer.Schemas"]
    SP["SchemaPackage"]
    DATA["Embedded<br/>*.schema.json<br/>valuesets.json"]
  end

  RH --> RV --> SE --> GSV
  SE --> SP
  GSV --> DATA
  SP --> DATA
```

## Refresh

```bash
make generate          # Patient (+ sync)
make generate-r5-all   # all R5 resources (+ sync)
make sync-schemas      # copy generator/out/r5 → packages/ only
```

## Public API

```fsharp
open FhirServer.Validation

// Empty list = valid
let issues = SchemaEngine.validateJson fhirJsonBody
```

Server facade: `FhirServer.Domain.Services.ResourceValidator` (maps issues → `OperationOutcome`, checks path `resourceType`).

# FHIR F# Generator (R5)

Generates **FHIR Schema JSON** + **Firely-free** `GeneratedSchemaValidator.fs` from official `hl7.fhir.r5.core@5.0.0` package JSON ([packages.fhir.org](https://packages.fhir.org)).

Resource index: [hl7.org/fhir/resourcelist.html](https://hl7.org/fhir/resourcelist.html) (R5 / v5.0.0).

> **No Firely / `Hl7.Fhir.*` in this generator.** StructureDefinitions are plain JSON from the NPM package.

## Validation strategy

| Concern | Who owns it |
|---------|-------------|
| Structure, cardinality, arrays, choices `[x]`, primitives (incl. date/dateTime/instant/uri/…), required bindings, complex datatypes, `contentReference`, ele-1, `meta.profile` format | **`FhirServer.Validation`** + schemas from **`FhirServer.Schemas`** |
| `$validate` / write 422 | FhirServer `ResourceValidator` → `SchemaEngine.validateJson` |
| Core constraint profiles (`derivation=constraint`) + FHIRPath constraint subset | Generator emits `schemas/profiles/` + `profiles-index.json`; validator applies when `meta.profile` matches |
| Extra / custom IGs beyond `hl7.fhir.r5.core` | Generate from additional packages, then `make sync-schemas` |
| This generator | Package → IR → resource + complex-type + **profile** schemas + valuesets + validator source |

## Layout

```
Generator/
  README.md
  TODO-R5.md
  FhirFsGen/
  cache/                  # hl7.fhir.r5.core (gitignored)
  out/r5/
    schemas/*.schema.json
    schemas/profiles/*.schema.json
    profiles-index.json
    valuesets.json
    manifest.json
    GeneratedResourceCatalog.fs
    GeneratedSchemaValidator.fs

Packages/
  FhirServer.Schemas/     # embedded *.schema.json + profiles + valuesets + catalog
  FhirServer.Validation/  # GeneratedSchemaValidator + SchemaEngine
```

---

## How generation works (UML)

### End-to-end activity

```mermaid
flowchart TB
  subgraph Source["Source"]
    REG["packages.fhir.org<br/>hl7.fhir.r5.core@5.0.0"]
  end

  subgraph Gen["Generator/FhirFsGen"]
    PC["PackageCache<br/>download + extract .tgz"]
    SL["StructureLoader<br/>StructureDefinition-*.json"]
    IR["SnapshotToIr<br/>snapshot → nested IR"]
    ES["EmitSchemaJson<br/>*.schema.json"]
    VS["ValueSetLoader<br/>required bindings → codes"]
    EV["EmitValidator<br/>copy ValidatorTemplate.fs"]
    EF["EmitFsharp<br/>catalog + TODO-R5.md"]
  end

  subgraph Out["Generator/out/r5"]
    SCH["schemas/*.schema.json"]
    VSET["valuesets.json"]
    MAN["manifest.json"]
    CAT["GeneratedResourceCatalog.fs"]
    VAL["GeneratedSchemaValidator.fs"]
  end

  subgraph Pkgs["Packages/ via make sync-schemas"]
    PS["FhirServer.Schemas<br/>Data/ + Generated/"]
    PV["FhirServer.Validation<br/>Generated/"]
  end

  REG --> PC --> SL --> IR
  IR --> ES --> SCH
  IR --> VS --> VSET
  IR --> EF --> CAT
  IR --> EF --> MAN
  EV --> VAL
  SCH --> PS
  VSET --> PS
  CAT --> PS
  VAL --> PV
```

### Module dependencies (components)

```mermaid
flowchart LR
  PROG["Program.fs<br/>CLI + orchestration"]

  PROG --> PC["PackageCache"]
  PROG --> SL["StructureLoader"]
  PROG --> IRM["SnapshotToIr"]
  PROG --> VSL["ValueSetLoader"]
  PROG --> ESJ["EmitSchemaJson"]
  PROG --> EFS["EmitFsharp"]
  PROG --> EV["EmitValidator"]

  IRM --> IR["Ir<br/>ElementSchema / ResourceSchema"]
  VSL --> IR
  ESJ --> IR
  EFS --> IR

  EV --> TPL["ValidatorTemplate.fs<br/>runtime engine source"]
```

### Per-resource sequence

```mermaid
sequenceDiagram
  participant CLI as Program
  participant PC as PackageCache
  participant SL as StructureLoader
  participant IR as SnapshotToIr
  participant ES as EmitSchemaJson
  participant VS as ValueSetLoader
  participant EV as EmitValidator
  participant EF as EmitFsharp

  CLI->>PC: ensurePackage(cache, hl7.fhir.r5.core@5.0.0)
  PC-->>CLI: packageDir

  loop each target resource (+ all complex types)
    CLI->>SL: loadStructureDefinition(packageDir, name)
    SL-->>CLI: StructureDefinition JSON
    CLI->>IR: fromStructureDefinition(fhirVersion, sd)
    IR-->>CLI: ResourceSchema (IR)
    CLI->>ES: writeResource(schema, schemas/Name.schema.json)
  end

  Note over CLI: unless --schema-only

  CLI->>ES: writeManifest(manifest.json)
  CLI->>EF: writeCatalog(GeneratedResourceCatalog.fs)
  CLI->>EV: writeSchemaValidatorFs<br/>(copy ValidatorTemplate.fs)
  CLI->>VS: collectRequiredBindings + resolveMany
  CLI->>ES: writeValueSets(valuesets.json)
  CLI->>EF: writeTodoMarkdown(TODO-R5.md)
```

### IR shape (class diagram)

```mermaid
classDiagram
  class ResourceSchema {
    +string FhirVersion
    +string TypeName
    +string Url
    +string Kind
    +Map~string,ElementSchema~ Elements
    +string list Required
  }

  class ElementSchema {
    +string Name
    +string Path
    +int Min
    +int option Max
    +string list Types
    +bool Array
    +ElementBinding option Binding
    +string list option Choices
    +string option ContentReference
    +Map~string,ElementSchema~ Elements
  }

  class ElementBinding {
    +BindingStrengthKind Strength
    +string ValueSet
  }

  class GenerationManifest {
    +string FhirVersion
    +string GeneratedAtUtc
    +ResourceSchema list Resources
  }

  ResourceSchema "1" *-- "*" ElementSchema : elements
  ElementSchema "1" *-- "*" ElementSchema : nested
  ElementSchema "0..1" --> ElementBinding : binding
  GenerationManifest "1" *-- "*" ResourceSchema : resources
```

### Sync into packages

```mermaid
flowchart LR
  OUT["Generator/out/r5"]

  OUT -->|"*.schema.json<br/>valuesets.json<br/>GeneratedResourceCatalog.fs"| S["FhirServer.Schemas"]
  OUT -->|"GeneratedSchemaValidator.fs"| V["FhirServer.Validation"]

  S -->|"EmbeddedResource"| ASM["Schemas assembly"]
  V -->|"compile + SchemaEngine"| ENG["validateJson at runtime"]
  ASM --> ENG
```

Runtime details: see [`Packages/FhirServer.Validation/README.md`](../Packages/FhirServer.Validation/README.md).

---

## Pipeline (short)

```
packages.fhir.org / hl7.fhir.r5.core@5.0.0
  → StructureDefinition snapshot (JSON)
  → IR
  → schemas/<Resource>.schema.json          # 158 resources
  → schemas/<DataType>.schema.json          # 42 complex types (HumanName, …)
  → valuesets.json                          # required bindings (resources + datatypes)
  → GeneratedSchemaValidator.fs             # from ValidatorTemplate.fs
  → GeneratedResourceCatalog.fs             # resources only
  → GeneratedSearchCatalog.fs               # Supported SearchParameter extractors

make sync-schemas / generate*:
  → Packages/FhirServer.Schemas/Data + Generated/
  → Packages/FhirServer.Validation/Generated/
```

## Usage

```bash
make generate          # Patient (+ sync into Packages/)
make generate-r5-all   # all 158 resources + sync
make sync-schemas      # copy out/r5 → Packages/ without regenerating
make generate-todo     # refresh TODO from schemas on disk

dotnet run --project Generator/FhirFsGen -- --resource Patient --out Generator/out/r5
dotnet run --project Generator/FhirFsGen -- --all --out Generator/out/r5
dotnet run --project Generator/FhirFsGen -- --schema-only --resource Observation
dotnet run --project Generator/FhirFsGen -- --search-catalog --out Generator/out/r5
```

## Schema shape

FHIR Schema nesting:

- `elements`, `array`, `required`, `binding`
- `choices` — JSON field names for `[x]` (e.g. `valueQuantity`, `valueString`)

namespace FhirFsGen

/// Intermediate representation derived from StructureDefinition snapshots (FHIR R5).
module Ir =

    type BindingStrengthKind =
        | Required
        | Extensible
        | Preferred
        | Example

    type ElementBinding =
        { Strength: BindingStrengthKind
          ValueSet: string }

    type ElementSchema =
        { Name: string
          Path: string
          Min: int
          /// None means unbounded (*).
          Max: int option
          Types: string list
          Array: bool
          Binding: ElementBinding option
          /// FHIR choice `[x]`: JSON field names (e.g. valueQuantity, valueString).
          /// Present on the *parent* object as siblings, not under `Name`.
          Choices: string list option
          /// Snapshot contentReference (e.g. #Questionnaire.item) — resolved at validate time.
          ContentReference: string option
          /// Nested backbone / complex children keyed by element name.
          Elements: Map<string, ElementSchema> }

    type ValueSetCodes =
        { Url: string
          Codes: string list }

    type ResourceSchema =
        { FhirVersion: string
          TypeName: string
          Url: string
          Kind: string
          Elements: Map<string, ElementSchema>
          Required: string list }

    type GenerationManifest =
        { FhirVersion: string
          GeneratedAtUtc: string
          Resources: ResourceSchema list }

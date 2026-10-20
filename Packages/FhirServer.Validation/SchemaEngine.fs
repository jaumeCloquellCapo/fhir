namespace FhirServer.Validation

open FhirServer.Schemas

/// Loads schemas/value sets/profiles from FhirServer.Schemas and exposes validateJson.
module SchemaEngine =

    let private schemas, valueSets, profiles =
        GeneratedSchemaValidator.loadFromEmbeddedAssembly SchemaPackage.assembly

    /// Validate a FHIR JSON document. Returns empty list when valid.
    let validateJson (body: string) =
        GeneratedSchemaValidator.validateJson schemas valueSets profiles body

    /// Validate and also apply forced profile URLs (e.g. `$validate?profile=`).
    let validateJsonWithProfiles (body: string) (forcedProfiles: string list) =
        GeneratedSchemaValidator.validateJsonWithProfiles schemas valueSets profiles body forcedProfiles

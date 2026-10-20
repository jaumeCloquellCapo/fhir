namespace FhirServer.Validation

open FhirServer.Schemas

/// Loads schemas/value sets/profiles from FhirServer.Schemas and exposes validateJson.
module SchemaEngine =

    let private schemas, valueSets, profiles =
        GeneratedSchemaValidator.loadFromEmbeddedAssembly SchemaPackage.assembly

    /// Validate a FHIR JSON document. Returns empty list when valid.
    let validateJson (body: string) =
        GeneratedSchemaValidator.validateJson schemas valueSets profiles body

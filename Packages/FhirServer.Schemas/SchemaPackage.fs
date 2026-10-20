namespace FhirServer.Schemas

open System.Reflection

/// Marker type so consumers can resolve the assembly that embeds schema JSON.
type SchemaPackageMarker = class end

/// Entry point for the assembly embedding `*.schema.json` and `valuesets.json`.
module SchemaPackage =

    let assembly: Assembly = typeof<SchemaPackageMarker>.Assembly

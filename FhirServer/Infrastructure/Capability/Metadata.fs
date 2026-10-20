namespace FhirServer.Infrastructure.Capability

open System
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Schemas

module Metadata =

    let private searchParam name searchType documentation =
        FhirJson.mkObject [
            "name", FhirJson.jStr name
            "type", FhirJson.jStr searchType
            "documentation", FhirJson.jStr documentation
        ]

    let private interaction code =
        FhirJson.mkObject [ "code", FhirJson.jStr code ]

    let private fhirSearchType =
        function
        | GeneratedSearchCatalog.IndexKind.String -> "string"
        | GeneratedSearchCatalog.IndexKind.Token -> "token"
        | GeneratedSearchCatalog.IndexKind.Ref -> "reference"
        | GeneratedSearchCatalog.IndexKind.Date -> "date"
        | GeneratedSearchCatalog.IndexKind.Quantity -> "quantity"
        | GeneratedSearchCatalog.IndexKind.Composite -> "composite"

    let private catalogParamsFor (resourceType: string) =
        GeneratedSearchCatalog.forResource resourceType
        |> List.map (fun p ->
            let doc =
                if String.length p.Documentation > 200 then
                    p.Documentation.Substring(0, 197) + "..."
                else
                    p.Documentation

            searchParam p.Code (fhirSearchType p.Kind) doc)

    let private resourceOperations (resourceType: string) =
        let ops = JsonArray()

        ops.Add(
            FhirJson.mkObject [
                "name", FhirJson.jStr "validate"
                "definition", FhirJson.jStr "http://hl7.org/fhir/OperationDefinition/Resource-validate"
            ]
        )

        ops.Add(
            FhirJson.mkObject [
                "name", FhirJson.jStr "dump"
                "definition", FhirJson.jStr "https://fhirserver.local/OperationDefinition/dump"
            ]
        )

        if resourceType = "Patient" then
            ops.Add(
                FhirJson.mkObject [
                    "name", FhirJson.jStr "everything"
                    "definition", FhirJson.jStr "http://hl7.org/fhir/OperationDefinition/Patient-everything"
                ]
            )

            ops.Add(
                FhirJson.mkObject [
                    "name", FhirJson.jStr "document"
                    "definition", FhirJson.jStr "http://hl7.org/fhir/OperationDefinition/Composition-document"
                ]
            )

        if resourceType = "Observation" then
            ops.Add(
                FhirJson.mkObject [
                    "name", FhirJson.jStr "lastn"
                    "definition", FhirJson.jStr "http://hl7.org/fhir/OperationDefinition/Observation-lastn"
                ]
            )

        ops

    let private resourceComponent (resourceType: string) (extraParams: JsonObject list) =
        let searchParams = JsonArray()
        searchParams.Add(searchParam "_id" "token" "Logical id")
        searchParams.Add(searchParam "_lastUpdated" "date" "When the resource last changed")
        searchParams.Add(searchParam "_count" "number" "Number of results")
        searchParams.Add(searchParam "_sort" "string" "Sort order")
        searchParams.Add(searchParam "_include" "string" "Include referenced resources")
        searchParams.Add(searchParam "_revinclude" "string" "Reverse include referencing resources")
        searchParams.Add(
            searchParam
                "_filter"
                "special"
                "Subset of FHIR _filter grammar: and/or/not, eq/ne/gt/lt/ge/le/co/sw on indexed params"
        )

        for p in extraParams do
            searchParams.Add(FhirJson.deepClone p)

        let interactions = JsonArray()
        interactions.Add(interaction "read")
        interactions.Add(interaction "vread")
        interactions.Add(interaction "create")
        interactions.Add(interaction "update")
        interactions.Add(interaction "delete")
        interactions.Add(interaction "search-type")
        interactions.Add(interaction "history-instance")
        interactions.Add(interaction "history-type")

        FhirJson.mkObject [
            "type", FhirJson.jStr resourceType
            "interaction", interactions
            "versioning", FhirJson.jStr "versioned"
            "readHistory", FhirJson.jBool true
            "updateCreate", FhirJson.jBool true
            "conditionalCreate", FhirJson.jBool true
            "searchParam", searchParams
            "operation", resourceOperations resourceType
        ]

    let build (baseUrl: string) : Types.FhirResource =
        let resources = JsonArray()

        for resourceType in ResourceCatalog.supportedResourceTypes do
            resources.Add(resourceComponent resourceType (catalogParamsFor resourceType))

        let systemInteractions = JsonArray()
        systemInteractions.Add(interaction "transaction")
        systemInteractions.Add(interaction "batch")
        systemInteractions.Add(interaction "history-system")
        systemInteractions.Add(interaction "search-system")

        let operations = JsonArray()

        operations.Add(
            FhirJson.mkObject [
                "name", FhirJson.jStr "validate"
                "definition", FhirJson.jStr "http://hl7.org/fhir/OperationDefinition/Resource-validate"
            ]
        )

        operations.Add(
            FhirJson.mkObject [
                "name", FhirJson.jStr "export"
                "definition", FhirJson.jStr "http://hl7.org/fhir/uv/bulkdata/OperationDefinition/export"
            ]
        )

        operations.Add(
            FhirJson.mkObject [
                "name", FhirJson.jStr "import"
                "definition", FhirJson.jStr "https://fhirserver.local/OperationDefinition/import"
            ]
        )

        operations.Add(
            FhirJson.mkObject [
                "name", FhirJson.jStr "load"
                "definition", FhirJson.jStr "https://fhirserver.local/OperationDefinition/load"
            ]
        )

        operations.Add(
            FhirJson.mkObject [
                "name", FhirJson.jStr "expand"
                "definition", FhirJson.jStr "http://hl7.org/fhir/OperationDefinition/ValueSet-expand"
            ]
        )

        operations.Add(
            FhirJson.mkObject [
                "name", FhirJson.jStr "lookup"
                "definition", FhirJson.jStr "http://hl7.org/fhir/OperationDefinition/CodeSystem-lookup"
            ]
        )

        operations.Add(
            FhirJson.mkObject [
                "name", FhirJson.jStr "validate-code"
                "definition", FhirJson.jStr "http://hl7.org/fhir/OperationDefinition/ValueSet-validate-code"
            ]
        )

        let securityServices = JsonArray()

        securityServices.Add(
            FhirJson.mkObject [
                "coding",
                JsonArray(
                    FhirJson.mkObject [
                        "system", FhirJson.jStr "http://hl7.org/fhir/restful-security-service"
                        "code", FhirJson.jStr "SMART-on-FHIR"
                        "display", FhirJson.jStr "SMART-on-FHIR"
                    ]
                )
            ]
        )

        let security =
            FhirJson.mkObject [
                "cors", FhirJson.jBool true
                "service", securityServices
                "description",
                FhirJson.jStr
                    "SMART App Launch: /.well-known/smart-configuration, /auth/authorize (PKCE + Basic when Auth on), /auth/token (client_credentials + authorization_code), optional /auth/register when Smart:EnableDcr. Bearer/Basic scopes patient|user|system with v2 c/r/u/d/s."
            ]

        let rest =
            FhirJson.mkObject [
                "mode", FhirJson.jStr "server"
                "security", security
                "interaction", systemInteractions
                "resource", resources
                "operation", operations
            ]

        let formats = JsonArray()
        formats.Add(FhirJson.jStr "json")
        formats.Add(FhirJson.jStr "application/fhir+json")

        let patchFormats = JsonArray()
        patchFormats.Add(FhirJson.jStr "application/json-patch+json")

        let rests = JsonArray()
        rests.Add(rest)

        FhirJson.mkObject [
            "resourceType", FhirJson.jStr "CapabilityStatement"
            "status", FhirJson.jStr "active"
            "date", FhirJson.jStr (System.DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"))
            "kind", FhirJson.jStr "instance"
            "fhirVersion", FhirJson.jStr ResourceCatalog.fhirVersion
            "format", formats
            "patchFormat", patchFormats
            "software", FhirJson.mkObject [ "name", FhirJson.jStr "FhirServer"; "version", FhirJson.jStr "0.3.0" ]
            "implementation",
            FhirJson.mkObject [
                "description", FhirJson.jStr $"FHIR {ResourceCatalog.fhirVersion} server in F#"
                "url", FhirJson.jStr (baseUrl.TrimEnd('/'))
            ]
            "rest", rests
        ]

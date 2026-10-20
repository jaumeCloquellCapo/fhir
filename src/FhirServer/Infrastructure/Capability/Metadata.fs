namespace FhirServer.Infrastructure.Capability

open System.Text.Json.Nodes
open FhirServer.Domain

module Metadata =

    let private searchParam name searchType documentation =
        FhirJson.mkObject [
            "name", FhirJson.jStr name
            "type", FhirJson.jStr searchType
            "documentation", FhirJson.jStr documentation
        ]

    let private interaction code =
        FhirJson.mkObject [ "code", FhirJson.jStr code ]

    let private patientParams = [
        searchParam "family" "string" "Family name"
        searchParam "given" "string" "Given name"
        searchParam "name" "string" "A portion of either family or given name"
        searchParam "gender" "token" "Gender"
        searchParam "birthdate" "date" "Birth date"
        searchParam "identifier" "token" "Identifier"
    ]

    let private observationParams = [
        searchParam "patient" "reference" "Subject patient"
        searchParam "subject" "reference" "Subject"
        searchParam "code" "token" "Code"
        searchParam "status" "token" "Status"
        searchParam "date" "date" "Effective date"
    ]

    let private patientRefParams = [
        searchParam "patient" "reference" "Subject patient"
        searchParam "subject" "reference" "Subject"
    ]

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

        for p in extraParams do
            // JsonNode can have only one parent — clone shared param templates.
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

        let clinicalWithPatient =
            set [
                "Encounter"
                "Condition"
                "AllergyIntolerance"
                "MedicationRequest"
                "Procedure"
                "DiagnosticReport"
                "Immunization"
                "CarePlan"
                "DocumentReference"
            ]

        for resourceType in ResourceCatalog.supportedResourceTypes do
            let extras =
                match resourceType with
                | "Patient" -> patientParams
                | "Observation" -> observationParams
                | rt when clinicalWithPatient.Contains rt -> patientRefParams
                | _ -> []

            resources.Add(resourceComponent resourceType extras)

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
                    "SMART App Launch MVP: /.well-known/smart-configuration and /auth/token (client_credentials). Optional HTTP Basic when Auth:Enabled=true."
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

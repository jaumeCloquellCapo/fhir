namespace FhirServer.Domain.Services

open System
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Validation

/// Thin facade over SchemaEngine (Firely-free schema validation).
module ResourceValidator =

    let private mapIssue (i: GeneratedSchemaValidator.ValidationIssue) : ValidationIssue = {
        Diagnostics = i.Diagnostics
        Fatal = i.Fatal
        Severity =
            if String.IsNullOrWhiteSpace i.Severity then
                if i.Fatal then "fatal" else "warning"
            else
                i.Severity
    }

    let private withExpectedType (expectedType: string option) (body: string) (issues: ValidationIssue list) =
        match expectedType with
        | None -> issues
        | Some rt ->
            try
                match JsonNode.Parse(body) with
                | :? JsonObject as node ->
                    let resourceType =
                        if isNull node["resourceType"] then
                            null
                        else
                            node["resourceType"].GetValue<string>()

                    if String.IsNullOrWhiteSpace resourceType then
                        {
                            Diagnostics = "resourceType is required."
                            Fatal = true
                            Severity = "fatal"
                        }
                        :: issues
                    elif resourceType <> rt then
                        {
                            Diagnostics = $"Body resourceType '{resourceType}' does not match path '{rt}'."
                            Fatal = true
                            Severity = "fatal"
                        }
                        :: issues
                    else
                        issues
                | _ ->
                    {
                        Diagnostics = "Request body must be a JSON object to check resourceType."
                        Fatal = true
                        Severity = "fatal"
                    }
                    :: issues
            with
            | :? System.Text.Json.JsonException as ex ->
                {
                    Diagnostics = $"Unable to parse JSON while checking resourceType: {ex.Message}"
                    Fatal = true
                    Severity = "fatal"
                }
                :: issues
            | ex ->
                {
                    Diagnostics = $"Unable to check resourceType against path '{rt}': {ex.Message}"
                    Fatal = true
                    Severity = "fatal"
                }
                :: issues

    let validateJsonWithProfiles
        (expectedType: string option)
        (body: string)
        (forcedProfiles: string list)
        : ValidationIssue list =
        SchemaEngine.validateJsonWithProfiles body forcedProfiles
        |> List.map mapIssue
        |> withExpectedType expectedType body

    let validateJson (expectedType: string option) (body: string) : ValidationIssue list =
        validateJsonWithProfiles expectedType body []

    let validateWithProfiles
        (expectedType: string option)
        (resource: FhirResource)
        (forcedProfiles: string list)
        : ValidationIssue list =
        if isNull (resource :> obj) then
            [
                {
                    Diagnostics = "Request body is required."
                    Fatal = true
                    Severity = "fatal"
                }
            ]
        else
            validateJsonWithProfiles expectedType (FhirJson.toJson resource) forcedProfiles

    let validate (expectedType: string option) (resource: FhirResource) : ValidationIssue list =
        validateWithProfiles expectedType resource []

    let hasFatal (issues: ValidationIssue list) =
        issues |> List.exists (fun i -> i.Fatal)

    let toOperationOutcome (issues: ValidationIssue list) : FhirResource =
        if issues = [] then
            FhirJson.operationOutcome [ "information", "informational", "all ok" ]
        else
            let rows =
                issues
                |> List.map (fun i ->
                    let sev =
                        match i.Severity.ToLowerInvariant() with
                        | "fatal" -> "fatal"
                        | "error" -> "error"
                        | "information" -> "information"
                        | _ -> "warning"

                    sev, "invalid", i.Diagnostics)

            let oo = FhirJson.operationOutcome rows

            if hasFatal issues then
                oo["id"] <- "invalid"

            oo

    let ensureValid (expectedType: string option) (resource: FhirResource) : FhirResult<unit> =
        let issues = validate expectedType resource

        if hasFatal issues then
            Error(ValidationFailed issues)
        else
            Ok()

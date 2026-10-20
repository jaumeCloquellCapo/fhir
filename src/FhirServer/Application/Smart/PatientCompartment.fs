namespace FhirServer.Application.Smart

open System
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types

/// Patient compartment membership and search-query binding for patient/ / user-as-Patient tokens.
module PatientCompartment =

    let normalizePatientId (value: string) =
        let v = value.Trim()

        if v.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase) then
            v.Substring("Patient/".Length)
        else
            v

    let patientReference (patientId: string) = $"Patient/{normalizePatientId patientId}"

    let private referenceMatchesPatient (reference: string) (patientId: string) =
        let id = normalizePatientId patientId
        let expected = patientReference id

        reference = expected
        || reference.Equals(id, StringComparison.Ordinal)
        || reference.EndsWith("/" + id, StringComparison.Ordinal)

    let private refFieldOk (resource: FhirResource) (field: string) (patientId: string) =
        match resource[field] with
        | :? JsonObject as obj ->
            match FhirJson.propString obj "reference" with
            | Some r -> referenceMatchesPatient r patientId
            | None -> false
        | :? JsonArray as arr ->
            arr
            |> Seq.exists (fun n ->
                match n with
                | :? JsonObject as obj ->
                    match FhirJson.propString obj "reference" with
                    | Some r -> referenceMatchesPatient r patientId
                    | None -> false
                | _ -> false)
        | _ -> false

    /// Resource types that participate via patient/subject references (MVP subset).
    let private compartmentFields (resourceType: string) =
        match resourceType with
        | "Patient" -> []
        | "Observation" -> [ "subject"; "patient" ]
        | "Encounter"
        | "Condition"
        | "AllergyIntolerance"
        | "MedicationRequest"
        | "Procedure"
        | "DiagnosticReport"
        | "Immunization"
        | "CarePlan"
        | "DocumentReference"
        | "Composition" -> [ "patient"; "subject" ]
        | _ -> [ "patient"; "subject" ]

    let belongsTo (patientId: string) (resource: FhirResource) =
        let id = normalizePatientId patientId
        let rt = FhirJson.resourceType resource

        if rt.Equals("Patient", StringComparison.OrdinalIgnoreCase) then
            FhirJson.id resource = id
        else
            let fields = compartmentFields rt
            fields |> List.exists (fun f -> refFieldOk resource f id)

    let private queryValues (query: Map<string, string list>) (key: string) =
        match Map.tryFind key query with
        | Some xs -> xs
        | None -> []

    let private valuesConflict (values: string list) (patientId: string) =
        let id = normalizePatientId patientId
        let expected = patientReference id

        values
        |> List.exists (fun v ->
            let n = normalizePatientId v
            n <> id && v <> expected && not (referenceMatchesPatient v id))

    /// Force search to the token patient; Error if client asked for a different patient.
    let bindSearchQuery
        (patientId: string)
        (resourceType: string)
        (query: Map<string, string list>)
        : Result<Map<string, string list>, string> =
        let id = normalizePatientId patientId
        let expected = patientReference id

        if resourceType.Equals("Patient", StringComparison.OrdinalIgnoreCase) then
            let existing = queryValues query "_id" @ queryValues query "identifier"

            // Prefer _id binding for Patient type search.
            let conflicting =
                queryValues query "_id"
                |> List.exists (fun v -> normalizePatientId v <> id)

            if conflicting then
                Error $"Search patient scope conflict: token patient is {expected}"
            else
                Ok(query |> Map.add "_id" [ id ])
        else
            let patientVals = queryValues query "patient"
            let subjectVals = queryValues query "subject"

            if valuesConflict patientVals id || valuesConflict subjectVals id then
                Error $"Search patient scope conflict: token patient is {expected}"
            else
                let withPatient = query |> Map.add "patient" [ expected ]

                let withSubject =
                    if Map.containsKey "subject" query || resourceType = "Observation" then
                        withPatient |> Map.add "subject" [ expected ]
                    else
                        withPatient

                Ok withSubject

    /// Effective compartment patient id from token context (patient/ scopes or user Patient fhirUser).
    let tryCompartmentPatientId (scope: string) (patientId: string option) (fhirUser: string option) =
        if SmartScopes.hasPatientClinicalScopes scope then
            patientId |> Option.map normalizePatientId
        elif SmartScopes.hasUserClinicalScopes scope then
            match fhirUser with
            | Some fu when fu.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase) ->
                Some(normalizePatientId fu)
            | _ -> None
        else
            None

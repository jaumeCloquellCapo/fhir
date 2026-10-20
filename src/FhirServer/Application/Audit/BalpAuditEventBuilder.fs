namespace FhirServer.Application.Audit

open System
open System.Text
open System.Text.Json.Nodes
open FhirServer.Domain

/// Builds R5 AuditEvent JSON with BALP semantics (profiles + REST actions).
/// R4-style fields map as: type→category, subtype→code, outcome code→outcome.code.
module BalpAuditEventBuilder =

    [<RequireQualifiedAccess>]
    type RestInteraction =
        | Create
        | Read
        | VRead
        | Update
        | Patch
        | Delete
        | Search
        | History
        | Validate
        | Transaction

    type AuditRequest = {
        Interaction: RestInteraction
        ResourceType: string option
        ResourceId: string option
        VersionId: string option
        /// Raw request line for search entity.query (method + path + query).
        QueryRaw: string option
        /// True when search includes a `patient` parameter (PatientQuery).
        HasPatientParam: bool
        AgentDisplay: string
        StatusCode: int
        Recorded: DateTimeOffset
    }

    let private balpBase = "https://profiles.ihe.net/ITI/BALP/StructureDefinition/"

    let private coding (system: string) (code: string) (display: string) =
        FhirJson.mkObject [
            "system", FhirJson.jStr system
            "code", FhirJson.jStr code
            "display", FhirJson.jStr display
        ]

    let private codeableConcept (system: string) (code: string) (display: string) =
        FhirJson.mkObject [ "coding", JsonArray(coding system code display) ]

    let private restfulCode =
        function
        | RestInteraction.Create -> "create", "create"
        | RestInteraction.Read -> "read", "read"
        | RestInteraction.VRead -> "vread", "vread"
        | RestInteraction.Update -> "update", "update"
        | RestInteraction.Patch -> "patch", "patch"
        | RestInteraction.Delete -> "delete", "delete"
        | RestInteraction.Search -> "search", "search"
        | RestInteraction.History -> "history", "history"
        | RestInteraction.Validate -> "operation", "operation"
        | RestInteraction.Transaction -> "transaction", "transaction"

    let private actionOf =
        function
        | RestInteraction.Create -> "C"
        | RestInteraction.Read
        | RestInteraction.VRead
        | RestInteraction.History -> "R"
        | RestInteraction.Update
        | RestInteraction.Patch -> "U"
        | RestInteraction.Delete -> "D"
        | RestInteraction.Search
        | RestInteraction.Validate
        | RestInteraction.Transaction -> "E"

    let private usePatientProfile (req: AuditRequest) =
        match req.Interaction, req.ResourceType with
        | RestInteraction.Search, Some rt when rt <> "Patient" && req.HasPatientParam -> true
        | RestInteraction.Search, _ -> false
        | RestInteraction.Validate, _
        | RestInteraction.Transaction, _
        | RestInteraction.History, _ -> false
        | _, Some "Patient" -> true
        | _ -> false

    let private balpProfile (req: AuditRequest) =
        let patient = usePatientProfile req

        let name =
            match req.Interaction with
            | RestInteraction.Create ->
                if patient then
                    "IHE.BasicAudit.PatientCreate"
                else
                    "IHE.BasicAudit.Create"
            | RestInteraction.Read
            | RestInteraction.VRead ->
                if patient then
                    "IHE.BasicAudit.PatientRead"
                else
                    "IHE.BasicAudit.Read"
            | RestInteraction.Update
            | RestInteraction.Patch ->
                if patient then
                    "IHE.BasicAudit.PatientUpdate"
                else
                    "IHE.BasicAudit.Update"
            | RestInteraction.Delete ->
                if patient then
                    "IHE.BasicAudit.PatientDelete"
                else
                    "IHE.BasicAudit.Delete"
            | RestInteraction.Search ->
                if patient then
                    "IHE.BasicAudit.PatientQuery"
                else
                    "IHE.BasicAudit.Query"
            | RestInteraction.History
            | RestInteraction.Validate
            | RestInteraction.Transaction -> "IHE.BasicAudit.Query"

        balpBase + name

    let private outcomeCoding (statusCode: int) =
        let code, display =
            if statusCode < 400 then "0", "Success"
            elif statusCode < 500 then "4", "Minor failure"
            else "8", "Serious failure"

        coding "http://terminology.hl7.org/CodeSystem/audit-event-outcome" code display

    let private entityReference (req: AuditRequest) =
        match req.ResourceType, req.ResourceId with
        | Some rt, Some id when not (String.IsNullOrWhiteSpace id) ->
            let reference =
                match req.Interaction, req.VersionId with
                | RestInteraction.Delete, Some vid
                | RestInteraction.VRead, Some vid -> $"{rt}/{id}/_history/{vid}"
                | _ -> $"{rt}/{id}"

            Some(FhirJson.mkObject [ "what", FhirJson.mkObject [ "reference", FhirJson.jStr reference ] ])
        | Some rt, _ when
            req.Interaction = RestInteraction.Search
            || req.Interaction = RestInteraction.Create
            ->
            Some(FhirJson.mkObject [ "what", FhirJson.mkObject [ "display", FhirJson.jStr rt ] ])
        | _ -> None

    let private entityQuery (req: AuditRequest) =
        match req.Interaction, req.QueryRaw with
        | RestInteraction.Search, Some raw ->
            let b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            Some(FhirJson.mkObject [ "query", FhirJson.jStr b64 ])
        | _ -> None

    let build (req: AuditRequest) : JsonObject =
        let subtypeCode, subtypeDisplay = restfulCode req.Interaction
        let profile = balpProfile req

        let entities =
            let list = ResizeArray<JsonNode>()

            match entityReference req with
            | Some e -> list.Add(e)
            | None -> ()

            match entityQuery req with
            | Some e -> list.Add(e)
            | None -> ()

            list

        let props: (string * JsonNode) list = [
            "resourceType", FhirJson.jStr "AuditEvent"
            "meta", FhirJson.mkObject [ "profile", JsonArray(JsonValue.Create(profile) :> JsonNode) ]
            "category",
            JsonArray(
                codeableConcept "http://terminology.hl7.org/CodeSystem/audit-event-type" "rest" "Restful Operation"
            )
            "code", codeableConcept "http://hl7.org/fhir/restful-interaction" subtypeCode subtypeDisplay
            "action", FhirJson.jStr (actionOf req.Interaction)
            "recorded", FhirJson.jStr (req.Recorded.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"))
            "outcome", FhirJson.mkObject [ "code", outcomeCoding req.StatusCode ]
            "agent",
            JsonArray(
                FhirJson.mkObject [
                    "who", FhirJson.mkObject [ "display", FhirJson.jStr req.AgentDisplay ]
                    "requestor", FhirJson.jBool true
                ]
            )
            "source", FhirJson.mkObject [ "observer", FhirJson.mkObject [ "display", FhirJson.jStr "FhirServer" ] ]
        ]

        let doc = FhirJson.mkObject props

        if entities.Count > 0 then
            doc["entity"] <- JsonArray(entities.ToArray())

        doc

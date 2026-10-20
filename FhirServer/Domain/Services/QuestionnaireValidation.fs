namespace FhirServer.Domain.Services

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports

/// Validate QuestionnaireResponse against its Questionnaire (linkId, required, answer types).
module QuestionnaireValidation =

    type private QItem = {
        LinkId: string
        Type: string
        Required: bool
        Repeats: bool
        Children: QItem list
    }

    let private issue fatal severity diagnostics : ValidationIssue = {
        Diagnostics = diagnostics
        Fatal = fatal
        Severity = severity
    }

    let private fatal msg = issue true "fatal" msg
    let private warning msg = issue false "warning" msg

    let private propBool (obj: JsonObject) (name: string) =
        match obj[name] with
        | null -> false
        | n when n.GetValueKind() = JsonValueKind.True -> true
        | n when n.GetValueKind() = JsonValueKind.False -> false
        | _ -> false

    let private propString (obj: JsonObject) (name: string) =
        match obj[name] with
        | null -> None
        | n when n.GetValueKind() = JsonValueKind.String -> Some(n.GetValue<string>())
        | _ -> None

    let private itemsArray (obj: JsonObject) =
        match obj["item"] with
        | :? JsonArray as arr ->
            arr
            |> Seq.choose (fun n ->
                match n with
                | :? JsonObject as o -> Some o
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let rec private parseQItem (obj: JsonObject) : QItem option =
        match propString obj "linkId" with
        | None -> None
        | Some linkId ->
            let typ = propString obj "type" |> Option.defaultValue ""

            Some {
                LinkId = linkId
                Type = typ
                Required = propBool obj "required"
                Repeats = propBool obj "repeats"
                Children = itemsArray obj |> List.choose parseQItem
            }

    let private flatten (items: QItem list) =
        let dict = Dictionary<string, QItem>(StringComparer.Ordinal)

        let rec walk (xs: QItem list) =
            for it in xs do
                if not (dict.ContainsKey it.LinkId) then
                    dict[it.LinkId] <- it

                walk it.Children

        walk items
        dict

    let private canonicalQuestionnaireUrl (raw: string) =
        match raw.IndexOf('|') with
        | -1 -> raw.Trim()
        | i -> raw.Substring(0, i).Trim()

    let private tryLoadQuestionnaire (store: IResourceStore) (questionnaireRef: string) : FhirResource option =
        let raw = questionnaireRef.Trim()

        if raw.StartsWith("Questionnaire/", StringComparison.Ordinal) then
            let id = raw.Substring("Questionnaire/".Length)
            store.Get("Questionnaire", id)
        elif
            raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
        then
            let url = canonicalQuestionnaireUrl raw
            store.TryFindByTypeWhere("Questionnaire", [ "url", url ])
        else
            // Bare id
            store.Get("Questionnaire", raw)
            |> Option.orElseWith (fun () ->
                store.TryFindByTypeWhere("Questionnaire", [ "url", canonicalQuestionnaireUrl raw ]))

    /// Expected QuestionnaireResponse.answer value[x] property for a Questionnaire.item.type.
    let private expectedAnswerProperty (itemType: string) =
        match itemType.ToLowerInvariant() with
        | "boolean" -> Some "valueBoolean"
        | "decimal" -> Some "valueDecimal"
        | "integer" -> Some "valueInteger"
        | "date" -> Some "valueDate"
        | "datetime" -> Some "valueDateTime"
        | "time" -> Some "valueTime"
        | "string"
        | "text" -> Some "valueString"
        | "url"
        | "uri" -> Some "valueUri"
        | "attachment" -> Some "valueAttachment"
        | "coding"
        | "choice"
        | "open-choice" -> Some "valueCoding"
        | "quantity" -> Some "valueQuantity"
        | "reference" -> Some "valueReference"
        | "group"
        | "display" -> None
        | _ -> None

    let private answerHasExpectedValue (answer: JsonObject) (propName: string) =
        match answer[propName] with
        | null -> false
        | n when n.GetValueKind() = JsonValueKind.Null -> false
        | _ -> true

    let private collectResponseItems (obj: JsonObject) : (string * JsonObject) list =
        let acc = ResizeArray<string * JsonObject>()

        let rec walk (nodes: JsonObject list) =
            for node in nodes do
                match propString node "linkId" with
                | Some lid ->
                    acc.Add(lid, node)
                    walk (itemsArray node)

                    match node["answer"] with
                    | :? JsonArray as answers ->
                        for a in answers do
                            match a with
                            | :? JsonObject as ao -> walk (itemsArray ao)
                            | _ -> ()
                    | _ -> ()
                | None -> ()

        walk (itemsArray obj)
        acc |> Seq.toList

    let private enforceRequired
        (strict: bool)
        (qItems: IReadOnlyDictionary<string, QItem>)
        (responseByLink: Map<string, JsonObject list>)
        =
        [
            for KeyValue(linkId, qItem) in qItems do
                if qItem.Required && qItem.Type <> "display" then
                    match responseByLink |> Map.tryFind linkId with
                    | None
                    | Some [] ->
                        let msg = $"Questionnaire item '{linkId}' is required but missing in QuestionnaireResponse."

                        if strict then yield fatal msg else yield warning msg
                    | Some nodes when qItem.Type = "group" ->
                        // Group required: at least one node present (already true).
                        ()
                    | Some nodes ->
                        let hasAnswer =
                            nodes
                            |> List.exists (fun n ->
                                match n["answer"] with
                                | :? JsonArray as arr when arr.Count > 0 -> true
                                | _ -> false)

                        if not hasAnswer then
                            let msg =
                                $"Questionnaire item '{linkId}' is required but has no answer in QuestionnaireResponse."

                            if strict then yield fatal msg else yield warning msg
        ]

    let private enforceTypes
        (qItems: IReadOnlyDictionary<string, QItem>)
        (responseItems: (string * JsonObject) list)
        =
        [
            for linkId, node in responseItems do
                match qItems.TryGetValue linkId with
                | false, _ ->
                    yield fatal $"QuestionnaireResponse item linkId '{linkId}' is not defined in the Questionnaire."
                | true, qItem ->
                    match expectedAnswerProperty qItem.Type with
                    | None ->
                        match node["answer"] with
                        | :? JsonArray as arr when arr.Count > 0 && qItem.Type = "display" ->
                            yield warning $"Display item '{linkId}' should not have answers."
                        | _ -> ()
                    | Some expectedProp ->
                        match node["answer"] with
                        | :? JsonArray as arr ->
                            for i = 0 to arr.Count - 1 do
                                match arr[i] with
                                | :? JsonObject as answer ->
                                    if not (answerHasExpectedValue answer expectedProp) then
                                        // Allow empty answer objects for incomplete drafts only checked when present
                                        let anyValue =
                                            answer
                                            |> Seq.exists (fun (kv: KeyValuePair<string, JsonNode>) ->
                                                kv.Key.StartsWith("value", StringComparison.Ordinal)
                                                && not (isNull kv.Value)
                                                && kv.Value.GetValueKind() <> JsonValueKind.Null)

                                        if anyValue then
                                            yield
                                                fatal
                                                    $"QuestionnaireResponse item '{linkId}' answer[{i}] expected {expectedProp} for type '{qItem.Type}'."
                                | _ -> ()
                        | _ -> ()
        ]

    /// Returns issues for QuestionnaireResponse vs Questionnaire. Empty if not a QR or no questionnaire ref.
    let collectIssues (store: IResourceStore) (resource: FhirResource) : ValidationIssue list =
        if isNull (resource :> obj) then
            []
        elif FhirJson.resourceType resource <> "QuestionnaireResponse" then
            []
        else
            match propString resource "questionnaire" with
            | None ->
                [ warning "QuestionnaireResponse.questionnaire is missing; Questionnaire-linked checks skipped." ]
            | Some qRef ->
                match tryLoadQuestionnaire store qRef with
                | None ->
                    [ warning $"Questionnaire '{qRef}' was not found; Questionnaire-linked checks skipped." ]
                | Some questionnaire ->
                    let qRootItems = itemsArray questionnaire |> List.choose parseQItem
                    let qIndex = flatten qRootItems :> IReadOnlyDictionary<_, _>
                    let responseFlat = collectResponseItems resource

                    let byLink =
                        responseFlat
                        |> List.groupBy fst
                        |> List.map (fun (k, pairs) -> k, pairs |> List.map snd)
                        |> Map.ofList

                    let status = propString resource "status" |> Option.defaultValue ""
                    let strict = status = "completed" || status = "amended"

                    enforceTypes qIndex responseFlat
                    @ enforceRequired strict qIndex byLink

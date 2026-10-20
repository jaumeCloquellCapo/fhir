namespace FhirServer.Domain.Services

open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports

/// Validate QuestionnaireResponse against its Questionnaire
/// (linkId, required, answer types, enableWhen, answerValueSet / answerOption).
module QuestionnaireValidation =

    type private EnableOp =
        | Exists
        | Eq
        | Ne
        | Gt
        | Lt
        | Ge
        | Le
        | Unknown of string

    type private EnableWhen = {
        Question: string
        Operator: EnableOp
        /// Expected answer[x] node from Questionnaire.enableWhen (null for missing).
        Answer: JsonNode
    }

    type private AnswerOption = { ValueProp: string; Value: JsonNode }

    type private QItem = {
        LinkId: string
        Type: string
        Required: bool
        Repeats: bool
        EnableBehaviorAll: bool
        EnableWhen: EnableWhen list
        AnswerValueSet: string option
        AnswerOptions: AnswerOption list
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

    let private parseOperator (raw: string) =
        match raw with
        | "exists" -> Exists
        | "=" -> Eq
        | "!=" -> Ne
        | ">" -> Gt
        | "<" -> Lt
        | ">=" -> Ge
        | "<=" -> Le
        | other -> Unknown other

    let private answerValueNode (obj: JsonObject) =
        obj
        |> Seq.tryFind (fun (kv: KeyValuePair<string, JsonNode>) ->
            kv.Key.StartsWith("answer", StringComparison.Ordinal)
            && kv.Key <> "answerOption"
            && not (isNull kv.Value)
            && kv.Value.GetValueKind() <> JsonValueKind.Null)
        |> Option.map (fun kv -> kv.Value)

    let private parseEnableWhen (obj: JsonObject) : EnableWhen option =
        match propString obj "question", propString obj "operator" with
        | Some q, Some op ->
            Some {
                Question = q
                Operator = parseOperator op
                Answer = answerValueNode obj |> Option.defaultValue null
            }
        | _ -> None

    let private parseAnswerOption (obj: JsonObject) : AnswerOption option =
        obj
        |> Seq.tryFind (fun (kv: KeyValuePair<string, JsonNode>) ->
            kv.Key.StartsWith("value", StringComparison.Ordinal)
            && not (isNull kv.Value)
            && kv.Value.GetValueKind() <> JsonValueKind.Null)
        |> Option.map (fun kv -> { ValueProp = kv.Key; Value = kv.Value })

    let rec private parseQItem (obj: JsonObject) : QItem option =
        match propString obj "linkId" with
        | None -> None
        | Some linkId ->
            let typ = propString obj "type" |> Option.defaultValue ""

            let enableWhen =
                match obj["enableWhen"] with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.choose (fun n ->
                        match n with
                        | :? JsonObject as o -> parseEnableWhen o
                        | _ -> None)
                    |> Seq.toList
                | _ -> []

            let enableBehaviorAll =
                match propString obj "enableBehavior" with
                | Some "all" -> true
                | _ -> false // FHIR default when unspecified with 0–1 conditions is effectively "any"

            let answerOptions =
                match obj["answerOption"] with
                | :? JsonArray as arr ->
                    arr
                    |> Seq.choose (fun n ->
                        match n with
                        | :? JsonObject as o -> parseAnswerOption o
                        | _ -> None)
                    |> Seq.toList
                | _ -> []

            Some {
                LinkId = linkId
                Type = typ
                Required = propBool obj "required"
                Repeats = propBool obj "repeats"
                EnableBehaviorAll = enableBehaviorAll
                EnableWhen = enableWhen
                AnswerValueSet = propString obj "answerValueSet" |> Option.map (fun u -> u.Split('|').[0].Trim())
                AnswerOptions = answerOptions
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
            store.Get("Questionnaire", raw)
            |> Option.orElseWith (fun () ->
                store.TryFindByTypeWhere("Questionnaire", [ "url", canonicalQuestionnaireUrl raw ]))

    let private tryLoadValueSet (store: IResourceStore) (canonical: string) : FhirResource option =
        let url = canonical.Split('|').[0].Trim()

        if url.StartsWith("ValueSet/", StringComparison.Ordinal) then
            store.Get("ValueSet", url.Substring("ValueSet/".Length))
        else
            store.TryFindByTypeWhere("ValueSet", [ "url", url ])
            |> Option.orElseWith (fun () ->
                if url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) then
                    None
                else
                    store.Get("ValueSet", url))

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

    let private responseAnswers (node: JsonObject) : JsonObject list =
        match node["answer"] with
        | :? JsonArray as arr ->
            arr
            |> Seq.choose (fun n ->
                match n with
                | :? JsonObject as o -> Some o
                | _ -> None)
            |> Seq.toList
        | _ -> []

    let private answersForLink (byLink: Map<string, JsonObject list>) (linkId: string) =
        byLink
        |> Map.tryFind linkId
        |> Option.defaultValue []
        |> List.collect responseAnswers

    let private nodeHasValue (n: JsonNode) =
        not (isNull n) && n.GetValueKind() <> JsonValueKind.Null

    let private asString (n: JsonNode) =
        if isNull n then None
        elif n.GetValueKind() = JsonValueKind.String then Some(n.GetValue<string>())
        elif n.GetValueKind() = JsonValueKind.Number then Some(n.ToString())
        elif n.GetValueKind() = JsonValueKind.True then Some "true"
        elif n.GetValueKind() = JsonValueKind.False then Some "false"
        else Some(n.ToJsonString())

    let private asDecimal (n: JsonNode) =
        if isNull n then None
        elif n.GetValueKind() = JsonValueKind.Number then
            Some(n.GetValue<decimal>())
        elif n.GetValueKind() = JsonValueKind.String then
            match Decimal.TryParse(n.GetValue<string>(), Globalization.NumberStyles.Any, Globalization.CultureInfo.InvariantCulture) with
            | true, d -> Some d
            | _ -> None
        else
            None

    let private valuesEqual (expected: JsonNode) (actual: JsonNode) =
        if isNull expected || isNull actual then
            false
        else
            match expected, actual with
            | (:? JsonObject as e), (:? JsonObject as a) ->
                // Coding / Quantity / Reference: compare by code or reference string when present
                match propString e "code", propString a "code" with
                | Some ec, Some ac ->
                    let sysOk =
                        match propString e "system", propString a "system" with
                        | Some es, Some asys -> es = asys
                        | _ -> true

                    ec = ac && sysOk
                | _ ->
                    match propString e "reference", propString a "reference" with
                    | Some er, Some ar -> er = ar
                    | _ ->
                        match asDecimal expected, asDecimal actual with
                        | Some ed, Some ad -> ed = ad
                        | _ -> asString expected = asString actual
            | _ ->
                match asDecimal expected, asDecimal actual with
                | Some ed, Some ad -> ed = ad
                | _ -> asString expected = asString actual

    let private compareOrdered (op: EnableOp) (expected: JsonNode) (actual: JsonNode) =
        match asDecimal expected, asDecimal actual with
        | Some ed, Some ad ->
            match op with
            | Gt -> ad > ed
            | Lt -> ad < ed
            | Ge -> ad >= ed
            | Le -> ad <= ed
            | _ -> false
        | _ ->
            match asString expected, asString actual with
            | Some es, Some as_ ->
                let cmp = String.CompareOrdinal(as_, es)

                match op with
                | Gt -> cmp > 0
                | Lt -> cmp < 0
                | Ge -> cmp >= 0
                | Le -> cmp <= 0
                | _ -> false
            | _ -> false

    let private answerValueFromResponse (answer: JsonObject) =
        answer
        |> Seq.tryFind (fun (kv: KeyValuePair<string, JsonNode>) ->
            kv.Key.StartsWith("value", StringComparison.Ordinal)
            && nodeHasValue kv.Value)
        |> Option.map (fun kv -> kv.Value)

    let private evaluateEnableWhen (cond: EnableWhen) (byLink: Map<string, JsonObject list>) =
        let answers = answersForLink byLink cond.Question
        let hasAnswer = answers |> List.exists (fun a -> answerValueFromResponse a |> Option.isSome)

        match cond.Operator with
        | Exists ->
            let want =
                match cond.Answer with
                | null -> true
                | n when n.GetValueKind() = JsonValueKind.True -> true
                | n when n.GetValueKind() = JsonValueKind.False -> false
                | _ -> true

            hasAnswer = want
        | Unknown _ -> true // can't evaluate → treat as enabled (don't block)
        | Eq ->
            match cond.Answer with
            | null -> false
            | expected -> answers |> List.exists (fun a -> match answerValueFromResponse a with Some v -> valuesEqual expected v | None -> false)
        | Ne ->
            match cond.Answer with
            | null -> hasAnswer
            | expected ->
                if not hasAnswer then true
                else answers |> List.exists (fun a -> match answerValueFromResponse a with Some v -> not (valuesEqual expected v) | None -> false)
        | Gt
        | Lt
        | Ge
        | Le as op ->
            match cond.Answer with
            | null -> false
            | expected ->
                answers
                |> List.exists (fun a ->
                    match answerValueFromResponse a with
                    | Some v -> compareOrdered op expected v
                    | None -> false)

    let private itemEnabled (qItem: QItem) (byLink: Map<string, JsonObject list>) =
        match qItem.EnableWhen with
        | [] -> true
        | conditions when qItem.EnableBehaviorAll -> conditions |> List.forall (fun c -> evaluateEnableWhen c byLink)
        | conditions -> conditions |> List.exists (fun c -> evaluateEnableWhen c byLink)

    /// linkId → enabled, cascading parent disable to descendants.
    let private computeEnabled (roots: QItem list) (byLink: Map<string, JsonObject list>) =
        let enabled = Dictionary<string, bool>(StringComparer.Ordinal)

        let rec walk (parentEnabled: bool) (items: QItem list) =
            for it in items do
                let self = parentEnabled && itemEnabled it byLink
                enabled[it.LinkId] <- self
                walk self it.Children

        walk true roots
        enabled :> IReadOnlyDictionary<_, _>

    let private enforceRequired
        (strict: bool)
        (qItems: IReadOnlyDictionary<string, QItem>)
        (enabled: IReadOnlyDictionary<string, bool>)
        (responseByLink: Map<string, JsonObject list>)
        =
        [
            for KeyValue(linkId, qItem) in qItems do
                let isOn =
                    match enabled.TryGetValue linkId with
                    | true, v -> v
                    | _ -> true

                if isOn && qItem.Required && qItem.Type <> "display" then
                    match responseByLink |> Map.tryFind linkId with
                    | None
                    | Some [] ->
                        let msg = $"Questionnaire item '{linkId}' is required but missing in QuestionnaireResponse."

                        if strict then yield fatal msg else yield warning msg
                    | Some nodes when qItem.Type = "group" -> ()
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

    let private enforceDisabledAnswers
        (strict: bool)
        (enabled: IReadOnlyDictionary<string, bool>)
        (responseItems: (string * JsonObject) list)
        =
        [
            for linkId, node in responseItems do
                let isOn =
                    match enabled.TryGetValue linkId with
                    | true, v -> v
                    | _ -> true

                if not isOn then
                    let hasAnswer =
                        match node["answer"] with
                        | :? JsonArray as arr when arr.Count > 0 -> true
                        | _ -> false

                    if hasAnswer then
                        let msg =
                            $"QuestionnaireResponse item '{linkId}' has answers but is disabled by enableWhen."

                        if strict then yield fatal msg else yield warning msg
        ]

    let private enforceTypes
        (qItems: IReadOnlyDictionary<string, QItem>)
        (enabled: IReadOnlyDictionary<string, bool>)
        (responseItems: (string * JsonObject) list)
        =
        [
            for linkId, node in responseItems do
                let isOn =
                    match enabled.TryGetValue linkId with
                    | true, v -> v
                    | _ -> true

                if isOn then
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

    let private expandValueSetCodes (store: IResourceStore) (valueSet: FhirResource) : Set<string * string> =
        let fromContains (arr: JsonArray) =
            arr
            |> Seq.choose (fun n ->
                match n with
                | :? JsonObject as o ->
                    match propString o "code" with
                    | Some code -> Some(propString o "system" |> Option.defaultValue "", code)
                    | None -> None
                | _ -> None)
            |> Set.ofSeq

        let rec conceptCodes (system: string) (node: JsonNode) =
            match node with
            | :? JsonObject as c ->
                let self =
                    match propString c "code" with
                    | Some code -> [ system, code ]
                    | None -> []

                let kids =
                    match c["concept"] with
                    | :? JsonArray as arr -> arr |> Seq.collect (conceptCodes system) |> Seq.toList
                    | _ -> []

                self @ kids
            | _ -> []

        match valueSet["expansion"] with
        | :? JsonObject as expansion ->
            match expansion["contains"] with
            | :? JsonArray as contains when contains.Count > 0 -> fromContains contains
            | _ -> Set.empty
        | _ ->
            match valueSet["compose"] with
            | :? JsonObject as compose ->
                match compose["include"] with
                | :? JsonArray as includes ->
                    includes
                    |> Seq.collect (fun n ->
                        match n with
                        | :? JsonObject as inc ->
                            let system = propString inc "system" |> Option.defaultValue ""

                            match inc["concept"] with
                            | :? JsonArray as concepts when concepts.Count > 0 ->
                                concepts
                                |> Seq.choose (fun c ->
                                    match c with
                                    | :? JsonObject as co ->
                                        propString co "code" |> Option.map (fun code -> system, code)
                                    | _ -> None)
                            | _ when system <> "" ->
                                match store.TryFindByTypeWhere("CodeSystem", [ "url", system ]) with
                                | Some cs ->
                                    match cs["concept"] with
                                    | :? JsonArray as arr -> arr |> Seq.collect (conceptCodes system)
                                    | _ -> Seq.empty
                                | None -> Seq.empty
                            | _ -> Seq.empty
                        | _ -> Seq.empty)
                    |> Set.ofSeq
                | _ -> Set.empty
            | _ -> Set.empty

    let private codingInSet (codes: Set<string * string>) (coding: JsonObject) =
        match propString coding "code" with
        | None -> false
        | Some code ->
            match propString coding "system" with
            | Some system -> codes.Contains(system, code)
            | None -> codes |> Set.exists (fun (_, c) -> c = code)

    let private optionMatches (opt: AnswerOption) (answer: JsonObject) =
        match answer[opt.ValueProp] with
        | null -> false
        | actual -> valuesEqual opt.Value actual

    let private enforceAnswerMembership
        (strict: bool)
        (store: IResourceStore)
        (qItems: IReadOnlyDictionary<string, QItem>)
        (enabled: IReadOnlyDictionary<string, bool>)
        (responseItems: (string * JsonObject) list)
        =
        [
            for linkId, node in responseItems do
                let isOn =
                    match enabled.TryGetValue linkId with
                    | true, v -> v
                    | _ -> true

                if isOn then
                    match qItems.TryGetValue linkId with
                    | false, _ -> ()
                    | true, qItem ->
                        let answers = responseAnswers node

                        if answers <> [] then
                            let isOpenChoice = qItem.Type.Equals("open-choice", StringComparison.OrdinalIgnoreCase)

                            // answerOption membership
                            if qItem.AnswerOptions <> [] then
                                for i, answer in List.indexed answers do
                                    let ok = qItem.AnswerOptions |> List.exists (fun o -> optionMatches o answer)

                                    let freeText =
                                        isOpenChoice
                                        && answerHasExpectedValue answer "valueString"

                                    if not ok && not freeText then
                                        let msg =
                                            $"QuestionnaireResponse item '{linkId}' answer[{i}] is not in answerOption."

                                        if strict then yield fatal msg else yield warning msg

                            // answerValueSet membership (choice / open-choice / coding)
                            match qItem.AnswerValueSet with
                            | None -> ()
                            | Some vsUrl ->
                                match tryLoadValueSet store vsUrl with
                                | None ->
                                    yield
                                        warning
                                            $"ValueSet '{vsUrl}' for item '{linkId}' was not found; answerValueSet check skipped."
                                | Some vs ->
                                    let codes = expandValueSetCodes store vs

                                    if codes.IsEmpty then
                                        yield
                                            warning
                                                $"ValueSet '{vsUrl}' for item '{linkId}' expanded to no codes; answerValueSet check skipped."
                                    else
                                        for i, answer in List.indexed answers do
                                            match answer["valueCoding"] with
                                            | :? JsonObject as coding ->
                                                if not (codingInSet codes coding) then
                                                    let shown =
                                                        propString coding "code" |> Option.defaultValue "?"

                                                    let msg =
                                                        $"QuestionnaireResponse item '{linkId}' answer[{i}] coding '{shown}' is not in answerValueSet '{vsUrl}'."

                                                    if strict then yield fatal msg else yield warning msg
                                            | _ when isOpenChoice && answerHasExpectedValue answer "valueString" ->
                                                () // open-choice free text allowed
                                            | _ when answerHasExpectedValue answer "valueString" && qItem.AnswerOptions = [] ->
                                                // choice typed as string answer → not in VS
                                                let msg =
                                                    $"QuestionnaireResponse item '{linkId}' answer[{i}] must be valueCoding from answerValueSet '{vsUrl}'."

                                                if strict then yield fatal msg else yield warning msg
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

                    let enabled = computeEnabled qRootItems byLink
                    let status = propString resource "status" |> Option.defaultValue ""
                    let strict = status = "completed" || status = "amended"

                    enforceTypes qIndex enabled responseFlat
                    @ enforceRequired strict qIndex enabled byLink
                    @ enforceDisabledAnswers strict enabled responseFlat
                    @ enforceAnswerMembership strict store qIndex enabled responseFlat

namespace FhirServer.Domain

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes

/// Helpers for Firely-free FHIR JSON documents (`Types.FhirResource` = `JsonObject`).
module FhirJson =

    let mkObject (props: (string * JsonNode) list) : JsonObject =
        let o = JsonObject()

        for name, value in props do
            o[name] <- value

        o

    let jStr (s: string) : JsonNode = JsonValue.Create(s)
    let jInt (n: int) : JsonNode = JsonValue.Create(n)
    let jBool (b: bool) : JsonNode = JsonValue.Create(b)

    let parse (json: string) : Result<JsonObject, string> =
        if String.IsNullOrWhiteSpace json then
            Error "Request body is required."
        else
            try
                match JsonNode.Parse(json) with
                | :? JsonObject as o -> Ok o
                | _ -> Error "Request body must be a JSON object."
            with ex ->
                Error $"Unable to parse FHIR payload: {ex.Message}"

    let toJson (r: JsonObject) =
        r.ToJsonString(JsonSerializerOptions(WriteIndented = false))

    let deepClone (r: JsonObject) : JsonObject = r.DeepClone() :?> JsonObject

    let private strProp (r: JsonObject) (name: string) =
        match r[name] with
        | null -> None
        | n when n.GetValueKind() = JsonValueKind.Null -> None
        | n when n.GetValueKind() = JsonValueKind.String -> Some(n.GetValue<string>())
        | _ -> None

    let resourceType (r: JsonObject) =
        strProp r "resourceType" |> Option.defaultValue ""

    let id (r: JsonObject) =
        strProp r "id" |> Option.defaultValue ""

    let setId (r: JsonObject) (value: string) =
        r["id"] <- value
        r

    let private metaObj (r: JsonObject) =
        match r["meta"] with
        | :? JsonObject as m -> m
        | _ ->
            let m = JsonObject()
            r["meta"] <- m
            m

    let versionId (r: JsonObject) =
        match r["meta"] with
        | :? JsonObject as m -> strProp m "versionId" |> Option.defaultValue ""
        | _ -> ""

    let lastUpdated (r: JsonObject) : DateTimeOffset option =
        match r["meta"] with
        | :? JsonObject as m ->
            match strProp m "lastUpdated" with
            | None -> None
            | Some s ->
                match DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
                | true, dto -> Some dto
                | _ -> None
        | _ -> None

    let stampMeta (r: JsonObject) (txid: int64) (ts: DateTimeOffset) =
        let meta = metaObj r
        meta["versionId"] <- string txid
        meta["lastUpdated"] <- ts.ToUniversalTime().ToString("o")
        r

    let propString (r: JsonObject) (name: string) = strProp r name

    let propArray (r: JsonObject) (name: string) =
        match r[name] with
        | :? JsonArray as arr -> Some arr
        | _ -> None

    let asObject (node: JsonNode) : JsonObject option =
        match node with
        | :? JsonObject as o -> Some o
        | _ -> None

    let arrayObjects (arr: JsonArray) : JsonObject list =
        arr
        |> Seq.choose asObject
        |> Seq.toList

    let propObjects (r: JsonObject) (name: string) : JsonObject list =
        match propArray r name with
        | None -> []
        | Some arr -> arrayObjects arr

    let operationOutcome (issues: (string * string * string) list) : JsonObject =
        let issueArr = JsonArray()

        for severity, code, diagnostics in issues do
            issueArr.Add(
                mkObject [
                    "severity", jStr severity
                    "code", jStr code
                    "diagnostics", jStr diagnostics
                ]
            )

        mkObject [ "resourceType", jStr "OperationOutcome"; "issue", issueArr ]

    let emptyBundle (bundleType: string) (total: int) : JsonObject =
        mkObject [
            "resourceType", jStr "Bundle"
            "type", jStr bundleType
            "total", jInt total
            "entry", JsonArray()
        ]

    let addBundleEntry (bundle: JsonObject) (entry: JsonObject) =
        match bundle["entry"] with
        | :? JsonArray as arr -> arr.Add(entry)
        | _ ->
            let arr = JsonArray()
            arr.Add(entry)
            bundle["entry"] <- arr

        bundle

    /// Append a searchset/history-style entry with `search.mode`.
    let addSearchEntry (bundle: JsonObject) (mode: string) (resource: JsonObject) =
        let rt = resourceType resource

        addBundleEntry
            bundle
            (mkObject [
                "fullUrl", jStr $"{rt}/{id resource}"
                "resource", deepClone resource
                "search", mkObject [ "mode", jStr mode ]
            ])

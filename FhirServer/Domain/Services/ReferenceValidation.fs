namespace FhirServer.Domain.Services

open System
open System.Text.Json
open System.Text.Json.Nodes
open FhirServer.Domain
open FhirServer.Domain.Types
open FhirServer.Domain.Ports

/// Business-rule checks: resolve relative `Reference.reference` values against the store.
module ReferenceValidation =

    let private tryParseTypeId (reference: string) : (string * string) option =
        if String.IsNullOrWhiteSpace reference then
            None
        elif reference.StartsWith("#", StringComparison.Ordinal) then
            None
        elif
            reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
        then
            None
        else
            let parts = reference.Split('/', 2, StringSplitOptions.RemoveEmptyEntries)

            if parts.Length = 2 && ResourceCatalog.isSupported parts.[0] then
                Some(parts.[0], parts.[1])
            else
                None

    let rec private walk (node: JsonNode) (path: string) (acc: ResizeArray<string * string * string>) =
        match node with
        | :? JsonObject as o ->
            match o["reference"] with
            | null -> ()
            | n when n.GetValueKind() = JsonValueKind.String ->
                match tryParseTypeId (n.GetValue<string>()) with
                | Some(rt, id) ->
                    let refPath = if path = "" then "reference" else path + ".reference"
                    acc.Add(refPath, rt, id)
                | None -> ()
            | _ -> ()

            for KeyValue(key, child) in o do
                if not (isNull child) then
                    let childPath = if path = "" then key else path + "." + key
                    walk child childPath acc
        | :? JsonArray as arr ->
            for i = 0 to arr.Count - 1 do
                let child = arr[i]

                if not (isNull child) then
                    walk child $"{path}[{i}]" acc
        | _ -> ()

    /// Returns non-fatal issues for dangling Type/id references.
    let collectIssues (store: IResourceStore) (resource: FhirResource) : ValidationIssue list =
        if isNull (resource :> obj) then
            []
        else
            let refs = ResizeArray<string * string * string>()
            walk resource "" refs

            [
                for path, rt, id in refs do
                    match store.Get(rt, id) with
                    | Some _ -> ()
                    | None ->
                        yield {
                            Diagnostics = $"{path}: referenced resource {rt}/{id} was not found."
                            Fatal = false
                            Severity = "warning"
                        }
            ]

    let collectIssuesFromJson (store: IResourceStore) (body: string) : ValidationIssue list =
        if String.IsNullOrWhiteSpace body then
            []
        else
            try
                match JsonNode.Parse(body) with
                | :? JsonObject as obj -> collectIssues store obj
                | _ -> []
            with _ ->
                []

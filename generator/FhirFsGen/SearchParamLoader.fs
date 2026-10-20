namespace FhirFsGen

open System
open System.IO
open System.Text.Json

/// Load HL7 SearchParameter instances from a FHIR package directory.
module SearchParamLoader =

    type ComponentDef =
        { DefinitionUrl: string
          Expression: string }

    type SearchParamRaw =
        { Id: string
          Url: string
          Code: string
          Status: string
          TypeName: string
          Bases: string list
          Expression: string
          Description: string
          Components: ComponentDef list }

    let private prop (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v -> Some v
        | _ -> None

    let private str (el: JsonElement) (name: string) =
        prop el name
        |> Option.bind (fun v ->
            if v.ValueKind = JsonValueKind.String then
                Some(v.GetString())
            else
                None)

    let private strList (el: JsonElement) (name: string) =
        match prop el name with
        | Some arr when arr.ValueKind = JsonValueKind.Array ->
            arr.EnumerateArray()
            |> Seq.choose (fun x ->
                if x.ValueKind = JsonValueKind.String then
                    Some(x.GetString())
                else
                    None)
            |> Seq.toList
        | _ -> []

    let private loadOne (path: string) : SearchParamRaw option =
        try
            use doc = JsonDocument.Parse(File.ReadAllText path)
            let root = doc.RootElement

            match str root "resourceType", str root "code", str root "type" with
            | Some "SearchParameter", Some code, Some typeName ->
                let status = str root "status" |> Option.defaultValue "unknown"

                if status = "retired" then
                    None
                else
                    let components =
                        match prop root "component" with
                        | Some arr when arr.ValueKind = JsonValueKind.Array ->
                            arr.EnumerateArray()
                            |> Seq.choose (fun c ->
                                match str c "definition", str c "expression" with
                                | Some def, Some expr ->
                                    Some {
                                        DefinitionUrl = def
                                        Expression = expr
                                    }
                                | _ -> None)
                            |> Seq.toList
                        | _ -> []

                    Some {
                        Id = str root "id" |> Option.defaultValue (Path.GetFileNameWithoutExtension path)
                        Url = str root "url" |> Option.defaultValue ""
                        Code = code
                        Status = status
                        TypeName = typeName
                        Bases = strList root "base"
                        Expression = str root "expression" |> Option.defaultValue ""
                        Description = str root "description" |> Option.defaultValue code
                        Components = components
                    }
            | _ -> None
        with _ ->
            None

    let loadAll (packageDir: string) : SearchParamRaw list =
        Directory.EnumerateFiles(packageDir, "SearchParameter-*.json")
        |> Seq.choose loadOne
        |> Seq.toList

    /// Split a FHIRPath union expression on top-level `|`.
    let splitUnion (expression: string) =
        let parts = ResizeArray<string>()
        let mutable depth = 0
        let cur = System.Text.StringBuilder()

        for ch in expression do
            match ch with
            | '(' ->
                depth <- depth + 1
                cur.Append ch |> ignore
            | ')' ->
                depth <- depth - 1
                cur.Append ch |> ignore
            | '|' when depth = 0 ->
                let s = cur.ToString().Trim()

                if s <> "" then
                    parts.Add s

                cur.Clear() |> ignore
            | _ -> cur.Append ch |> ignore

        let last = cur.ToString().Trim()

        if last <> "" then
            parts.Add last

        parts |> Seq.toList

    let clauseForResource (resourceType: string) (expression: string) =
        splitUnion expression
        |> List.tryFind (fun c ->
            c.StartsWith(resourceType + ".", StringComparison.Ordinal)
            || c.Equals(resourceType, StringComparison.Ordinal))

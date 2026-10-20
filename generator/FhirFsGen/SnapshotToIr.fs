namespace FhirFsGen

open System
open System.Text.Json
open FhirFsGen.Ir

/// Builds nested ElementSchema trees from StructureDefinition.snapshot JSON.
module SnapshotToIr =

    let private prop (el: JsonElement) (name: string) =
        match el.TryGetProperty name with
        | true, v -> Some v
        | _ -> None

    let private str (el: JsonElement) (name: string) =
        prop el name
        |> Option.bind (fun v ->
            if v.ValueKind = JsonValueKind.String then Some(v.GetString())
            elif v.ValueKind = JsonValueKind.Number then Some(v.GetRawText())
            else None)

    let private intOr (el: JsonElement) (name: string) (fallback: int) =
        prop el name
        |> Option.bind (fun v ->
            match v.ValueKind with
            | JsonValueKind.Number -> Some(v.GetInt32())
            | JsonValueKind.String ->
                match Int32.TryParse(v.GetString()) with
                | true, n -> Some n
                | _ -> None
            | _ -> None)
        |> Option.defaultValue fallback

    let private parseMax (max: string option) =
        match max with
        | None
        | Some "*"
        | Some "" -> None
        | Some s ->
            match Int32.TryParse s with
            | true, n -> Some n
            | _ -> None

    let private bindingStrengthOf (s: string option) =
        match s |> Option.map (fun x -> x.ToLowerInvariant()) with
        | Some "required" -> Some Required
        | Some "extensible" -> Some Extensible
        | Some "preferred" -> Some Preferred
        | Some "example" -> Some Example
        | _ -> None

    let private elementBinding (e: JsonElement) =
        match prop e "binding" with
        | None -> None
        | Some b ->
            match bindingStrengthOf (str b "strength"), str b "valueSet" with
            | Some strength, Some valueSet -> Some { Strength = strength; ValueSet = valueSet }
            | _ -> None

    let private typesOf (e: JsonElement) =
        match prop e "type" with
        | Some t when t.ValueKind = JsonValueKind.Array ->
            t.EnumerateArray()
            |> Seq.choose (fun item -> str item "code")
            |> Seq.distinct
            |> Seq.toList
        | _ -> []

    type private ElementRow =
        { Path: string
          Min: int
          Max: int option
          Types: string list
          Binding: ElementBinding option
          ContentReference: string option
          Raw: JsonElement }

    let private relativeName (resourceType: string) (path: string) =
        let prefix = resourceType + "."

        if path.StartsWith(prefix, StringComparison.Ordinal) then
            path.Substring(prefix.Length)
        else
            path

    let private segmentDepth (relative: string) =
        relative.Split('.') |> Array.length

    /// FHIR JSON choice field: value[x] + Quantity → valueQuantity.
    let choiceFieldName (baseName: string) (typeCode: string) =
        if String.IsNullOrEmpty typeCode then
            baseName
        else
            let suffix =
                string (Char.ToUpperInvariant typeCode.[0]) + typeCode.Substring(1)

            baseName + suffix

    let private choiceFields (baseName: string) (types: string list) =
        types
        |> List.map (choiceFieldName baseName)
        |> List.distinct

    let private rowsOf (sd: StructureLoader.StructureDefinitionDoc) =
        match prop sd.Json "snapshot" |> Option.bind (fun s -> prop s "element") with
        | Some els when els.ValueKind = JsonValueKind.Array ->
            els.EnumerateArray()
            |> Seq.choose (fun e ->
                match str e "path" with
                | None -> None
                | Some path ->
                    Some
                        { Path = path
                          Min = intOr e "min" 0
                          Max = parseMax (str e "max")
                          Types = typesOf e
                          Binding = elementBinding e
                          ContentReference = str e "contentReference"
                          Raw = e })
            |> Seq.toList
        | _ -> failwith $"StructureDefinition '{sd.TypeName}' has no snapshot.element."

    let rec private buildChildren
        (resourceType: string)
        (all: ElementRow list)
        (parentRelative: string)
        : Map<string, ElementSchema> =
        let parentDepth = if parentRelative = "" then 0 else segmentDepth parentRelative

        let direct =
            all
            |> List.choose (fun e ->
                if e.Path = resourceType then
                    None
                else
                    let rel = relativeName resourceType e.Path

                    if rel = "" || rel = resourceType then
                        None
                    else
                        let lastSeg = rel.Split('.') |> Array.last
                        let isChoice = lastSeg.EndsWith("[x]", StringComparison.Ordinal)

                        let segmentName =
                            if isChoice then lastSeg.Substring(0, lastSeg.Length - 3) else lastSeg

                        let relForDepth =
                            if isChoice then
                                let prefix =
                                    if rel.Contains('.') then
                                        rel.Substring(0, rel.LastIndexOf '.') + "."
                                    else
                                        ""

                                prefix + segmentName
                            else
                                rel

                        if parentRelative = "" then
                            if segmentDepth relForDepth = 1 then
                                Some(segmentName, e, relForDepth, isChoice)
                            else
                                None
                        elif
                            relForDepth.StartsWith(parentRelative + ".", StringComparison.Ordinal)
                            && segmentDepth relForDepth = parentDepth + 1
                        then
                            Some(segmentName, e, relForDepth, isChoice)
                        else
                            None)

        direct
        |> List.map (fun (name, e, relForDepth, isChoice) ->
            let isArray = e.Max.IsNone || e.Max.Value > 1

            let choices =
                if isChoice then Some(choiceFields name e.Types) else None

            // Choice fields live as siblings on the parent JSON object — no nested walk under [x].
            // contentReference (incl. recursive item.item → #item): do not inline — validator resolves.
            let contentRef = e.ContentReference |> Option.map (fun c -> c.Trim())

            let nested =
                if isChoice || contentRef.IsSome then
                    Map.empty
                else
                    buildChildren resourceType all relForDepth

            let child =
                { Name = name
                  Path = e.Path
                  Min = e.Min
                  Max = e.Max
                  Types = e.Types
                  Array = isArray
                  Binding = e.Binding
                  Choices = choices
                  ContentReference = contentRef
                  Elements = nested }

            name, child)
        |> Map.ofList

    let fromStructureDefinition (fhirVersion: string) (sd: StructureLoader.StructureDefinitionDoc) : ResourceSchema =
        let rows = rowsOf sd
        let tree = buildChildren sd.TypeName rows ""

        // Choice [x] bases are not JSON properties — cardinality is enforced by validateChoice.
        let required =
            tree
            |> Map.toList
            |> List.choose (fun (name, el) ->
                match el.Choices with
                | Some _ -> None
                | None when el.Min >= 1 -> Some name
                | None -> None)
            |> List.sort

        { FhirVersion = fhirVersion
          TypeName = sd.TypeName
          Url = sd.Url
          Kind = sd.Kind
          Elements = tree
          Required = required }

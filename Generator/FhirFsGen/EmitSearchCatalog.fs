namespace FhirFsGen

open System
open System.IO
open System.Text
open SearchParamLoader

/// Compile SearchParameter expressions into a Supported extractor subset and emit F# catalog.
module EmitSearchCatalog =

    [<RequireQualifiedAccess>]
    type IndexKind =
        | String
        | Token
        | Ref
        | Date
        | Quantity
        | Composite

    [<RequireQualifiedAccess>]
    type StringMatch =
        | Contains
        | Exact

    /// Runtime extractor kind — mirrored in GeneratedSearchCatalog.fs.
    [<RequireQualifiedAccess>]
    type Extractor =
        | HumanName of part: string // "family" | "given" | "any"
        | StringField of path: string list
        | Identifier of path: string list
        | CodeableConcept of path: string list
        | Reference of fields: string list * indexParams: string list
        | DateField of path: string list
        | ObservationEffective
        | QuantityValue
        | Composite of components: (string * string) list // (paramCode * relativeExpr)
        | Unsupported of reason: string

    type EmittedParam =
        { ResourceType: string
          Code: string
          Url: string
          Kind: IndexKind
          Documentation: string
          IndexParam: string
          StringMatch: StringMatch
          Extractor: Extractor }

    let private indexedResources =
        set [
            "Patient"
            "Organization"
            "Observation"
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

    let private stripWhere (clause: string) =
        match clause.IndexOf(".where(", StringComparison.Ordinal) with
        | -1 -> clause.Trim()
        | i -> clause.Substring(0, i).Trim()

    let private pathAfterType (resourceType: string) (clause: string) =
        let cleaned = stripWhere clause

        if cleaned.Equals(resourceType, StringComparison.Ordinal) then
            []
        elif cleaned.StartsWith(resourceType + ".", StringComparison.Ordinal) then
            cleaned.Substring(resourceType.Length + 1).Split('.')
            |> Array.map (fun p ->
                // Drop ofType(...) segments → keep field name before ofType
                match p.IndexOf("ofType(", StringComparison.Ordinal) with
                | -1 -> p
                | _ ->
                    // e.g. effective.ofType(dateTime) already split — ofType is own segment rarely
                    p)
            |> Array.filter (fun p -> not (p.StartsWith("ofType(", StringComparison.Ordinal)))
            |> Array.toList
        else
            []

    let private fhirTypeToKind (typeName: string) =
        match typeName with
        | "string" -> Some IndexKind.String
        | "token" -> Some IndexKind.Token
        | "reference" -> Some IndexKind.Ref
        | "date" -> Some IndexKind.Date
        | "quantity" -> Some IndexKind.Quantity
        | "composite" -> Some IndexKind.Composite
        | _ -> None

    /// Map a resource-local clause + FHIR search type into an extractor.
    let private compileClause
        (resourceType: string)
        (code: string)
        (typeName: string)
        (clause: string)
        : Extractor option =
        let path = pathAfterType resourceType clause

        let humanNameBases =
            set [ "Patient"; "Practitioner"; "Person"; "RelatedPerson" ]

        match typeName, path, code with
        | "string", [ "name" ], _ when humanNameBases.Contains resourceType ->
            Some(Extractor.HumanName "any")
        | "string", [ "name"; "family" ], _ when humanNameBases.Contains resourceType ->
            Some(Extractor.HumanName "family")
        | "string", [ "name"; "given" ], _ when humanNameBases.Contains resourceType ->
            Some(Extractor.HumanName "given")
        | "string", fields, _ when fields <> [] && not (fields |> List.exists (fun f -> f.Contains("("))) ->
            // Organization.name | Organization.alias → first simple field
            Some(Extractor.StringField [ List.head fields ])
        | "token", [ "gender" ], _ -> Some(Extractor.StringField [ "gender" ])
        | "token", [ "status" ], _ -> Some(Extractor.StringField [ "status" ])
        | "token", [ "identifier" ], _ -> Some(Extractor.Identifier [ "identifier" ])
        | "token", [ "code" ], _ -> Some(Extractor.CodeableConcept [ "code" ])
        | "token", fields, _ when fields <> [] && List.last fields = "code" ->
            Some(Extractor.CodeableConcept fields)
        | "date", [ "birthDate" ], _ -> Some(Extractor.DateField [ "birthDate" ])
        | "date", fields, _ when
            resourceType = "Observation"
            && fields <> []
            && List.head fields = "effective"
            ->
            Some Extractor.ObservationEffective
        | "date", fields, _ when fields <> [] -> Some(Extractor.DateField fields)
        | "reference", [ "managingOrganization" ], _ ->
            Some(Extractor.Reference([ "managingOrganization" ], [ "organization"; "managingOrganization" ]))
        | "reference", [ "subject" ], "patient" ->
            Some(Extractor.Reference([ "subject"; "patient" ], [ "patient"; "subject" ]))
        | "reference", [ "subject" ], _ ->
            Some(Extractor.Reference([ "subject"; "patient" ], [ "subject"; "patient" ]))
        | "reference", [ "patient" ], _ ->
            Some(Extractor.Reference([ "patient"; "subject" ], [ "patient"; "subject" ]))
        | "reference", [ "encounter" ], _ -> Some(Extractor.Reference([ "encounter" ], [ "encounter" ]))
        | "reference", fields, _ when fields <> [] ->
            Some(Extractor.Reference(fields, [ code ]))
        | "quantity", fields, _ when
            resourceType = "Observation"
            && (fields = [] || List.head fields = "value")
            ->
            Some Extractor.QuantityValue
        | "composite", _, _ -> None // handled separately
        | _ -> None

    let private indexParamFor (code: string) (extractor: Extractor) =
        match extractor with
        | Extractor.Reference(_, indexParams) when indexParams <> [] ->
            // Canonical index key: prefer subject for Observation patient alias
            if List.contains "subject" indexParams then
                "subject"
            elif List.contains "patient" indexParams then
                "patient"
            else
                List.head indexParams
        | _ -> code

    let private stringMatchFor (code: string) (kind: IndexKind) =
        match kind, code with
        | IndexKind.String, ("gender" | "status") -> StringMatch.Exact
        | IndexKind.String, _ -> StringMatch.Contains
        | _ -> StringMatch.Contains

    /// Prefer token→string for primitive code fields we historically stored in search_string.
    let private runtimeKind (typeName: string) (extractor: Extractor) =
        match typeName, extractor with
        | "token", Extractor.StringField _ -> IndexKind.String
        | "token", Extractor.Identifier _ -> IndexKind.Token
        | "token", Extractor.CodeableConcept _ -> IndexKind.Token
        | _ ->
            match fhirTypeToKind typeName with
            | Some k -> k
            | None -> IndexKind.String

    let private tryEmit
        (byUrl: Map<string, SearchParamRaw>)
        (sp: SearchParamRaw)
        (resourceType: string)
        : EmittedParam list =
        if not (indexedResources.Contains resourceType) then
            []
        else
            match sp.TypeName with
            | "composite" when sp.Components <> [] ->
                let components =
                    sp.Components
                    |> List.choose (fun c ->
                        match byUrl |> Map.tryFind c.DefinitionUrl with
                        | Some def -> Some(def.Code, c.Expression)
                        | None ->
                            // Fallback: last path segment of definition URL
                            let code =
                                match c.DefinitionUrl.LastIndexOf('/') with
                                | -1 -> c.DefinitionUrl
                                | i ->
                                    let tail = c.DefinitionUrl.Substring(i + 1)

                                    match tail.LastIndexOf('-') with
                                    | -1 -> tail
                                    | j ->
                                        // Observation-value-quantity → value-quantity; clinical-code → code
                                        let maybe = tail.Substring(j + 1)

                                        if
                                            maybe = "quantity"
                                            || maybe = "concept"
                                            || maybe = "string"
                                            || maybe = "date"
                                        then
                                            // Observation-value-quantity
                                            let prev = tail.Substring(0, j)

                                            match prev.LastIndexOf('-') with
                                            | -1 -> maybe
                                            | k -> prev.Substring(k + 1) + "-" + maybe
                                        else
                                            maybe

                            Some(code, c.Expression))

                if components.Length = sp.Components.Length then
                    [
                        {
                            ResourceType = resourceType
                            Code = sp.Code
                            Url = sp.Url
                            Kind = IndexKind.Composite
                            Documentation = sp.Description
                            IndexParam = sp.Code
                            StringMatch = StringMatch.Exact
                            Extractor = Extractor.Composite components
                        }
                    ]
                else
                    []
            | "uri"
            | "number"
            | "special" -> []
            | typeName ->
                let clause =
                    match clauseForResource resourceType sp.Expression with
                    | Some c -> c
                    | None when sp.Bases.Length = 1 && sp.Bases.Head = resourceType -> sp.Expression
                    | None -> ""

                if clause = "" then
                    []
                else
                    match compileClause resourceType sp.Code typeName clause with
                    | None -> []
                    | Some extractor ->
                        let kind = runtimeKind typeName extractor
                        let indexParam = indexParamFor sp.Code extractor

                        let primary =
                            {
                                ResourceType = resourceType
                                Code = sp.Code
                                Url = sp.Url
                                Kind = kind
                                Documentation = sp.Description
                                IndexParam = indexParam
                                StringMatch = stringMatchFor sp.Code kind
                                Extractor = extractor
                            }

                        // Observation subject also searchable as patient
                        let aliases =
                            match resourceType, sp.Code, extractor with
                            | "Observation", "subject", Extractor.Reference(_, idxs) when
                                List.contains "patient" idxs
                                ->
                                [
                                    {
                                        primary with
                                            Code = "patient"
                                            Documentation = "Subject patient (alias of subject)"
                                    }
                                ]
                            | rt, "subject", Extractor.Reference(_, idxs) when
                                List.contains "patient" idxs
                                && rt <> "Observation"
                                ->
                                [
                                    {
                                        primary with
                                            Code = "patient"
                                            IndexParam = "patient"
                                            Documentation = "Subject patient (alias of subject)"
                                    }
                                ]
                            | _ -> []

                        primary :: aliases

    let build (packageDir: string) : EmittedParam list =
        let all = loadAll packageDir
        let byUrl = all |> List.map (fun s -> s.Url, s) |> Map.ofList

        all
        |> List.collect (fun sp ->
            sp.Bases
            |> List.collect (fun baseType -> tryEmit byUrl sp baseType))
        |> List.distinctBy (fun p -> p.ResourceType, p.Code)
        |> List.sortBy (fun p -> p.ResourceType, p.Code)

    let private esc (s: string) =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ")

    let private emitExtractor (ex: Extractor) =
        match ex with
        | Extractor.HumanName part -> $"HumanName \"{esc part}\""
        | Extractor.StringField path ->
            let fields = path |> List.map (fun p -> $"\"{esc p}\"") |> String.concat "; "
            $"StringField [ {fields} ]"
        | Extractor.Identifier path ->
            let fields = path |> List.map (fun p -> $"\"{esc p}\"") |> String.concat "; "
            $"Identifier [ {fields} ]"
        | Extractor.CodeableConcept path ->
            let fields = path |> List.map (fun p -> $"\"{esc p}\"") |> String.concat "; "
            $"CodeableConcept [ {fields} ]"
        | Extractor.Reference(fields, idxs) ->
            let f = fields |> List.map (fun p -> $"\"{esc p}\"") |> String.concat "; "
            let i = idxs |> List.map (fun p -> $"\"{esc p}\"") |> String.concat "; "
            $"Reference([ {f} ], [ {i} ])"
        | Extractor.DateField path ->
            let fields = path |> List.map (fun p -> $"\"{esc p}\"") |> String.concat "; "
            $"DateField [ {fields} ]"
        | Extractor.ObservationEffective -> "ObservationEffective"
        | Extractor.QuantityValue -> "QuantityValue"
        | Extractor.Composite components ->
            let inner =
                components
                |> List.map (fun (c, e) -> $"\"{esc c}\", \"{esc e}\"")
                |> String.concat "; "

            $"Composite [ {inner} ]"
        | Extractor.Unsupported reason -> $"Unsupported \"{esc reason}\""

    let private emitKind =
        function
        | IndexKind.String -> "String"
        | IndexKind.Token -> "Token"
        | IndexKind.Ref -> "Ref"
        | IndexKind.Date -> "Date"
        | IndexKind.Quantity -> "Quantity"
        | IndexKind.Composite -> "Composite"

    let private emitMatch =
        function
        | StringMatch.Contains -> "Contains"
        | StringMatch.Exact -> "Exact"

    let write (params': EmittedParam list) (path: string) =
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        let sb = StringBuilder()

        sb.AppendLine("// <auto-generated />") |> ignore
        sb.AppendLine("// FHIR R5 SearchParameter catalog — Supported extractor subset.")
        |> ignore

        sb.AppendLine($"// Generated {DateTime.UtcNow:o} UTC · {params'.Length} param(s)")
        |> ignore

        sb.AppendLine("namespace FhirServer.Schemas") |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine("/// Search parameter catalog produced by Generator/FhirFsGen.")
        |> ignore

        sb.AppendLine("module GeneratedSearchCatalog =") |> ignore
        sb.AppendLine() |> ignore

        sb.AppendLine("    [<RequireQualifiedAccess>]") |> ignore
        sb.AppendLine("    type IndexKind =") |> ignore
        sb.AppendLine("        | String") |> ignore
        sb.AppendLine("        | Token") |> ignore
        sb.AppendLine("        | Ref") |> ignore
        sb.AppendLine("        | Date") |> ignore
        sb.AppendLine("        | Quantity") |> ignore
        sb.AppendLine("        | Composite") |> ignore
        sb.AppendLine() |> ignore

        sb.AppendLine("    [<RequireQualifiedAccess>]") |> ignore
        sb.AppendLine("    type StringMatch =") |> ignore
        sb.AppendLine("        | Contains") |> ignore
        sb.AppendLine("        | Exact") |> ignore
        sb.AppendLine() |> ignore

        sb.AppendLine("    [<RequireQualifiedAccess>]") |> ignore
        sb.AppendLine("    type Extractor =") |> ignore
        sb.AppendLine("        | HumanName of part: string") |> ignore
        sb.AppendLine("        | StringField of path: string list") |> ignore
        sb.AppendLine("        | Identifier of path: string list") |> ignore
        sb.AppendLine("        | CodeableConcept of path: string list") |> ignore
        sb.AppendLine("        | Reference of fields: string list * indexParams: string list")
        |> ignore

        sb.AppendLine("        | DateField of path: string list") |> ignore
        sb.AppendLine("        | ObservationEffective") |> ignore
        sb.AppendLine("        | QuantityValue") |> ignore
        sb.AppendLine("        | Composite of components: (string * string) list") |> ignore
        sb.AppendLine("        | Unsupported of reason: string") |> ignore
        sb.AppendLine() |> ignore

        sb.AppendLine("    type ParamDef =") |> ignore
        sb.AppendLine("        { ResourceType: string") |> ignore
        sb.AppendLine("          Code: string") |> ignore
        sb.AppendLine("          Url: string") |> ignore
        sb.AppendLine("          Kind: IndexKind") |> ignore
        sb.AppendLine("          Documentation: string") |> ignore
        sb.AppendLine("          IndexParam: string") |> ignore
        sb.AppendLine("          StringMatch: StringMatch") |> ignore
        sb.AppendLine("          Extractor: Extractor }") |> ignore
        sb.AppendLine() |> ignore

        sb.AppendLine("    let all: ParamDef list =") |> ignore
        sb.AppendLine("        [") |> ignore

        for p in params' do
            let doc =
                let d = esc p.Documentation

                if d.Length > 120 then
                    d.Substring(0, 117) + "..."
                else
                    d

            sb.AppendLine("          {") |> ignore
            sb.AppendLine($"            ResourceType = \"{esc p.ResourceType}\"") |> ignore
            sb.AppendLine($"            Code = \"{esc p.Code}\"") |> ignore
            sb.AppendLine($"            Url = \"{esc p.Url}\"") |> ignore
            sb.AppendLine($"            Kind = IndexKind.{emitKind p.Kind}") |> ignore
            sb.AppendLine($"            Documentation = \"{doc}\"") |> ignore
            sb.AppendLine($"            IndexParam = \"{esc p.IndexParam}\"") |> ignore
            sb.AppendLine($"            StringMatch = StringMatch.{emitMatch p.StringMatch}")
            |> ignore

            sb.AppendLine($"            Extractor = Extractor.{emitExtractor p.Extractor}")
            |> ignore

            sb.AppendLine("          }") |> ignore

        sb.AppendLine("        ]") |> ignore
        sb.AppendLine() |> ignore

        sb.AppendLine("    let forResource (resourceType: string) =") |> ignore
        sb.AppendLine("        all |> List.filter (fun p -> p.ResourceType = resourceType)")
        |> ignore

        sb.AppendLine() |> ignore
        sb.AppendLine("    let tryFind (resourceType: string) (code: string) =") |> ignore

        sb.AppendLine(
            "        all |> List.tryFind (fun p -> p.ResourceType = resourceType && p.Code = code)"
        )
        |> ignore

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8)
        printfn "Search catalog: %d supported param(s) → %s" params'.Length path

namespace FhirServer.Domain.Services

open System
open System.Text

/// MVP subset of FHIR `_filter` grammar.
module SearchFilter =

    [<RequireQualifiedAccess>]
    type CompareOp =
        | Eq
        | Ne
        | Gt
        | Lt
        | Ge
        | Le
        | Co
        | Sw

    [<RequireQualifiedAccess>]
    type Expr =
        | Compare of param: string * op: CompareOp * value: string
        | And of Expr * Expr
        | Or of Expr * Expr
        | Not of Expr

    [<RequireQualifiedAccess>]
    type Expansion =
        /// No `_filter` key — use query as-is.
        | Unchanged of Map<string, string list>
        /// Single conjunctive query (AND / simple compare).
        | Single of Map<string, string list>
        /// OR alternatives: union of search results by `_id`.
        | Alternatives of Map<string, string list> list
        | Invalid of reason: string

    type private Tok =
        | Ident of string
        | Str of string
        | LParen
        | RParen
        | KwAnd
        | KwOr
        | KwNot
        | Op of CompareOp
        | Eof

    let private isNameChar (c: char) =
        Char.IsLetterOrDigit c || c = '_' || c = '-'

    let private tokenize (input: string) : Tok list =
        let s = input.Trim()
        let toks = ResizeArray<Tok>()
        let mutable i = 0

        let advance () = i <- i + 1

        while i < s.Length do
            match s.[i] with
            | c when Char.IsWhiteSpace c -> advance ()
            | '(' ->
                toks.Add LParen
                advance ()
            | ')' ->
                toks.Add RParen
                advance ()
            | '"' ->
                advance ()
                let sb = StringBuilder()

                while i < s.Length && s.[i] <> '"' do
                    if s.[i] = '\\' && i + 1 < s.Length then
                        advance ()
                        sb.Append s.[i] |> ignore
                        advance ()
                    else
                        sb.Append s.[i] |> ignore
                        advance ()

                if i < s.Length && s.[i] = '"' then
                    advance ()

                toks.Add(Str(sb.ToString()))
            | c when Char.IsLetter c || c = '_' || Char.IsDigit c ->
                let start = i

                while i < s.Length
                      && (isNameChar s.[i]
                          || s.[i] = '|'
                          || s.[i] = '.'
                          || s.[i] = ':'
                          || s.[i] = '/') do
                    advance ()

                let word = s.Substring(start, i - start)

                match word.ToLowerInvariant() with
                | "and" -> toks.Add KwAnd
                | "or" -> toks.Add KwOr
                | "not" -> toks.Add KwNot
                | "eq" -> toks.Add(Op CompareOp.Eq)
                | "ne" -> toks.Add(Op CompareOp.Ne)
                | "gt" -> toks.Add(Op CompareOp.Gt)
                | "lt" -> toks.Add(Op CompareOp.Lt)
                | "ge" -> toks.Add(Op CompareOp.Ge)
                | "le" -> toks.Add(Op CompareOp.Le)
                | "co" -> toks.Add(Op CompareOp.Co)
                | "sw" -> toks.Add(Op CompareOp.Sw)
                | _ -> toks.Add(Ident word)
            | _ -> advance ()

        toks.Add Eof
        toks |> Seq.toList

    type private Parser(tokens: Tok list) =
        let mutable pos = 0

        let current () =
            if pos < tokens.Length then tokens.[pos] else Eof

        let advance () = pos <- pos + 1

        let accept (pred: Tok -> bool) =
            let t = current ()

            if pred t then
                advance ()
                Some t
            else
                None

        member this.ParseFilter() : Expr option =
            this.ParseOr()

        member private this.ParseOr() =
            match this.ParseAnd() with
            | None -> None
            | Some left ->
                let rec loop acc =
                    match accept (function
                        | KwOr -> true
                        | _ -> false) with
                    | None -> Some acc
                    | Some _ ->
                        match this.ParseAnd() with
                        | None -> Some acc
                        | Some right -> loop (Expr.Or(acc, right))

                loop left

        member private this.ParseAnd() =
            match this.ParseUnary() with
            | None -> None
            | Some left ->
                let rec loop acc =
                    match accept (function
                        | KwAnd -> true
                        | _ -> false) with
                    | None -> Some acc
                    | Some _ ->
                        match this.ParseUnary() with
                        | None -> Some acc
                        | Some right -> loop (Expr.And(acc, right))

                loop left

        member private this.ParseUnary() =
            match accept (function
                | KwNot -> true
                | _ -> false) with
            | Some _ ->
                match accept (function
                    | LParen -> true
                    | _ -> false) with
                | Some _ ->
                    match this.ParseFilter() with
                    | None -> None
                    | Some inner ->
                        ignore (
                            accept (function
                                | RParen -> true
                                | _ -> false)
                        )

                        Some(Expr.Not inner)
                | None ->
                    match this.ParseUnary() with
                    | None -> None
                    | Some inner -> Some(Expr.Not inner)
            | None ->
                match accept (function
                    | LParen -> true
                    | _ -> false) with
                | Some _ ->
                    match this.ParseFilter() with
                    | None -> None
                    | Some inner ->
                        ignore (
                            accept (function
                                | RParen -> true
                                | _ -> false)
                        )

                        Some inner
                | None -> this.ParseCompare()

        member private this.ParseCompare() =
            match accept (function
                | Ident _ -> true
                | _ -> false) with
            | Some(Ident name) ->
                match accept (function
                    | Op _ -> true
                    | _ -> false) with
                | Some(Op op) ->
                    match accept (function
                        | Ident _
                        | Str _ -> true
                        | _ -> false) with
                    | Some(Ident v)
                    | Some(Str v) -> Some(Expr.Compare(name, op, v))
                    | _ -> None
                | _ -> None
            | _ -> None

    let tryParse (raw: string) : Expr option =
        if String.IsNullOrWhiteSpace raw then
            None
        else
            try
                Parser(tokenize raw).ParseFilter()
            with _ ->
                None

    let private compareToEntries (param: string) (op: CompareOp) (value: string) : (string * string) list =
        match op with
        | CompareOp.Co -> [ $"{param}:contains", value ]
        | CompareOp.Sw -> [ param, value ]
        | CompareOp.Ne ->
            if
                value.Contains('|', StringComparison.Ordinal)
                || param = "code"
                || param = "identifier"
            then
                [ $"{param}:not", value ]
            else
                [ param, "ne" + value ]
        | CompareOp.Eq -> [ param, value ]
        | CompareOp.Gt -> [ param, "gt" + value ]
        | CompareOp.Lt -> [ param, "lt" + value ]
        | CompareOp.Ge -> [ param, "ge" + value ]
        | CompareOp.Le -> [ param, "le" + value ]

    let private mergeMaps (a: Map<string, string list>) (b: Map<string, string list>) =
        Map.fold
            (fun acc k vs ->
                let existing = acc |> Map.tryFind k |> Option.defaultValue []
                acc |> Map.add k (existing @ vs))
            a
            b

    let private toAlternatives (expr: Expr) : Map<string, string list> list =
        let rec compile e =
            match e with
            | Expr.Compare(p, op, v) ->
                let m =
                    compareToEntries p op v
                    |> List.fold
                        (fun (acc: Map<string, string list>) (k, value) ->
                            let existing = acc |> Map.tryFind k |> Option.defaultValue []
                            acc |> Map.add k (existing @ [ value ]))
                        Map.empty

                [ m ]
            | Expr.And(a, b) ->
                [
                    for ma in compile a do
                        for mb in compile b do
                            yield mergeMaps ma mb
                ]
            | Expr.Or(a, b) -> compile a @ compile b
            | Expr.Not(Expr.Compare(p, CompareOp.Eq, v)) -> compile (Expr.Compare(p, CompareOp.Ne, v))
            | Expr.Not _ -> [ Map.ofList [ "_id", [ "__filter_nomatch__" ] ] ]

        compile expr

    /// Expand `_filter` (if present) into search query alternative(s).
    let expand (query: Map<string, string list>) : Expansion =
        match query |> Map.tryFind "_filter" with
        | None
        | Some [] -> Expansion.Unchanged query
        | Some values ->
            let raw = String.concat " and " values

            match tryParse raw with
            | None -> Expansion.Invalid "unparsable _filter expression"
            | Some expr ->
                let without = query |> Map.remove "_filter"
                let alts = toAlternatives expr |> List.map (mergeMaps without)

                match alts with
                | [] -> Expansion.Single(without |> Map.add "_id" [ "__filter_nomatch__" ])
                | [ one ] -> Expansion.Single one
                | many -> Expansion.Alternatives many

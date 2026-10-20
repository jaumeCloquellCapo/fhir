namespace FhirServer.Infrastructure.Persistence

open System
open System.Text
open FhirServer.Domain.Services

/// SQL fragment builder for search_index queries (`@p` placeholders).
module SearchIndexQuery =

    type BuiltQuery =
        { WhereClause: string
          OrderBy: string
          Parameters: (string * obj) list }

    let private ph (name: string) = "@" + name

    let private orderBy (plan: SearchEngine.SearchPlan) =
        match plan.Sort with
        | SearchEngine.SortField.TsDesc -> "ORDER BY t.ts DESC, t.id DESC"
        | SearchEngine.SortField.TsAsc -> "ORDER BY t.ts ASC, t.id ASC"
        | SearchEngine.SortField.IdDesc -> "ORDER BY t.id DESC"
        | SearchEngine.SortField.IdAsc -> "ORDER BY t.id ASC"

    let private indexTable =
        function
        | SearchEngine.IndexKind.String -> "search_string"
        | SearchEngine.IndexKind.Token -> "search_token"
        | SearchEngine.IndexKind.Ref -> "search_ref"
        | SearchEngine.IndexKind.Date -> "search_date"
        | SearchEngine.IndexKind.Quantity -> "search_quantity"

    let buildWhere (resourceType: string) (plan: SearchEngine.SearchPlan) : BuiltQuery =
        let sb = StringBuilder(" WHERE 1=1")
        let mutable parameters: (string * obj) list = []
        let mutable n = 0

        let addParam (value: obj) =
            n <- n + 1
            let name = $"p{n}"
            parameters <- (name, value) :: parameters
            ph name

        let rtPh = addParam (resourceType :> obj)

        if plan.Ids <> [] then
            let idPhs =
                plan.Ids
                |> List.map (fun id -> addParam (id :> obj))
                |> String.concat ", "

            sb.Append($" AND t.id IN ({idPhs})") |> ignore

        match plan.LastUpdatedGe with
        | Some ge ->
            let p = addParam (ge :> obj)
            sb.Append($" AND t.ts >= {p}") |> ignore
        | None -> ()

        match plan.LastUpdatedLe with
        | Some le ->
            let p = addParam (le :> obj)
            sb.Append($" AND t.ts <= {p}") |> ignore
        | None -> ()

        for filter in plan.Strings do
            let paramPh = addParam (filter.Param :> obj)

            let valueClauses =
                filter.Values
                |> List.map (fun value ->
                    let vPh = addParam (value :> obj)

                    match filter.Match with
                    | SearchEngine.StringMatch.Contains ->
                        "EXISTS (SELECT 1 FROM search_string s WHERE s.resource_type = "
                        + rtPh
                        + " AND s.id = t.id AND s.param = "
                        + paramPh
                        + " AND s.value LIKE '%' || "
                        + vPh
                        + " || '%')"
                    | SearchEngine.StringMatch.Exact ->
                        $"EXISTS (SELECT 1 FROM search_string s WHERE s.resource_type = {rtPh} AND s.id = t.id AND s.param = {paramPh} AND s.value = {vPh})"
                    | SearchEngine.StringMatch.Prefix ->
                        "EXISTS (SELECT 1 FROM search_string s WHERE s.resource_type = "
                        + rtPh
                        + " AND s.id = t.id AND s.param = "
                        + paramPh
                        + " AND s.value LIKE "
                        + vPh
                        + " || '%')")
                |> String.concat " OR "

            if valueClauses <> "" then
                sb.Append($" AND ({valueClauses})") |> ignore

        for filter in plan.Tokens do
            let paramPh = addParam (filter.Param :> obj)

            let valueClauses =
                filter.Values
                |> List.map (fun (system, code) ->
                    let codePh = addParam (code :> obj)

                    let codePred =
                        match filter.Match with
                        | SearchEngine.TokenMatch.Exact -> $"tk.code = {codePh}"
                        // Ancestor-or-self: indexed code is a prefix of (or equal to) the query code.
                        | SearchEngine.TokenMatch.Above ->
                            codePh
                            + " LIKE tk.code || '%' AND LENGTH(tk.code) > 0 AND LENGTH(tk.code) <= LENGTH("
                            + codePh
                            + ")"
                        // Descendant-or-self: indexed code starts with the query code.
                        | SearchEngine.TokenMatch.Below -> "tk.code LIKE " + codePh + " || '%'"

                    match system with
                    | None ->
                        $"EXISTS (SELECT 1 FROM search_token tk WHERE tk.resource_type = {rtPh} AND tk.id = t.id AND tk.param = {paramPh} AND {codePred})"
                    | Some sys ->
                        let sysPh = addParam (sys :> obj)

                        $"EXISTS (SELECT 1 FROM search_token tk WHERE tk.resource_type = {rtPh} AND tk.id = t.id AND tk.param = {paramPh} AND {codePred} AND tk.system = {sysPh})")
                |> String.concat " OR "

            if valueClauses <> "" then
                if filter.Negated then
                    sb.Append($" AND NOT ({valueClauses})") |> ignore
                else
                    sb.Append($" AND ({valueClauses})") |> ignore

        for filter in plan.Refs do
            let paramPh = addParam (filter.Param :> obj)

            let valueClauses =
                filter.Values
                |> List.map (fun target ->
                    let tPh = addParam (target :> obj)

                    $"EXISTS (SELECT 1 FROM search_ref r WHERE r.resource_type = {rtPh} AND r.id = t.id AND r.param = {paramPh} AND r.target = {tPh})")
                |> String.concat " OR "

            if valueClauses <> "" then
                sb.Append($" AND ({valueClauses})") |> ignore

        for filter in plan.Missing do
            let paramPh = addParam (filter.Param :> obj)
            let table = indexTable filter.Kind
            let exists =
                $"EXISTS (SELECT 1 FROM {table} x WHERE x.resource_type = {rtPh} AND x.id = t.id AND x.param = {paramPh})"

            if filter.Missing then
                sb.Append($" AND NOT {exists}") |> ignore
            else
                sb.Append($" AND {exists}") |> ignore

        for filter in plan.Dates do
            let paramPh = addParam (filter.Param :> obj)
            let instantPh = addParam (filter.Instant :> obj)

            let clause =
                match filter.Comparator with
                | SearchEngine.DateComparator.Eq ->
                    $"EXISTS (SELECT 1 FROM search_date d WHERE d.resource_type = {rtPh} AND d.id = t.id AND d.param = {paramPh} AND d.start_ts <= {instantPh} AND d.end_ts > {instantPh})"
                | SearchEngine.DateComparator.Ne ->
                    $"NOT EXISTS (SELECT 1 FROM search_date d WHERE d.resource_type = {rtPh} AND d.id = t.id AND d.param = {paramPh} AND d.start_ts <= {instantPh} AND d.end_ts > {instantPh})"
                | SearchEngine.DateComparator.Gt
                | SearchEngine.DateComparator.Sa ->
                    $"EXISTS (SELECT 1 FROM search_date d WHERE d.resource_type = {rtPh} AND d.id = t.id AND d.param = {paramPh} AND d.start_ts > {instantPh})"
                | SearchEngine.DateComparator.Lt
                | SearchEngine.DateComparator.Eb ->
                    $"EXISTS (SELECT 1 FROM search_date d WHERE d.resource_type = {rtPh} AND d.id = t.id AND d.param = {paramPh} AND d.end_ts <= {instantPh})"
                | SearchEngine.DateComparator.Ge ->
                    $"EXISTS (SELECT 1 FROM search_date d WHERE d.resource_type = {rtPh} AND d.id = t.id AND d.param = {paramPh} AND d.end_ts > {instantPh})"
                | SearchEngine.DateComparator.Le ->
                    $"EXISTS (SELECT 1 FROM search_date d WHERE d.resource_type = {rtPh} AND d.id = t.id AND d.param = {paramPh} AND d.start_ts <= {instantPh})"
                | SearchEngine.DateComparator.Ap ->
                    let hiPh =
                        match filter.InstantHi with
                        | Some hi -> addParam (hi :> obj)
                        | None -> instantPh

                    $"EXISTS (SELECT 1 FROM search_date d WHERE d.resource_type = {rtPh} AND d.id = t.id AND d.param = {paramPh} AND d.start_ts < {hiPh} AND d.end_ts > {instantPh})"

            sb.Append($" AND {clause}") |> ignore

        for filter in plan.Quantities do
            let paramPh = addParam (filter.Param :> obj)
            let valuePh = addParam (filter.Value :> obj)

            let systemClause =
                match filter.System with
                | None -> ""
                | Some sys ->
                    let sysPh = addParam (sys :> obj)
                    $" AND q.system = {sysPh}"

            let codeClause =
                match filter.Code with
                | None -> ""
                | Some code ->
                    let codePh = addParam (code :> obj)
                    $" AND q.code = {codePh}"

            let clause =
                match filter.Comparator with
                | SearchEngine.QuantityComparator.Ap ->
                    let hiPh =
                        match filter.ValueHi with
                        | Some hi -> addParam (hi :> obj)
                        | None -> valuePh

                    $"EXISTS (SELECT 1 FROM search_quantity q WHERE q.resource_type = {rtPh} AND q.id = t.id AND q.param = {paramPh} AND q.value >= {valuePh} AND q.value <= {hiPh}{systemClause}{codeClause})"
                | other ->
                    let op =
                        match other with
                        | SearchEngine.QuantityComparator.Eq -> "="
                        | SearchEngine.QuantityComparator.Ne -> "<>"
                        | SearchEngine.QuantityComparator.Gt -> ">"
                        | SearchEngine.QuantityComparator.Lt -> "<"
                        | SearchEngine.QuantityComparator.Ge -> ">="
                        | SearchEngine.QuantityComparator.Le -> "<="
                        | SearchEngine.QuantityComparator.Ap -> "="

                    $"EXISTS (SELECT 1 FROM search_quantity q WHERE q.resource_type = {rtPh} AND q.id = t.id AND q.param = {paramPh} AND q.value {op} {valuePh}{systemClause}{codeClause})"

            sb.Append($" AND {clause}") |> ignore
        { WhereClause = sb.ToString()
          OrderBy = orderBy plan
          Parameters = List.rev parameters }

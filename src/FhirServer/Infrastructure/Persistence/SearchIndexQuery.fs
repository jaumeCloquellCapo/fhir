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

            sb.Append($" AND ({valueClauses})") |> ignore

        for filter in plan.Tokens do
            let paramPh = addParam (filter.Param :> obj)

            let valueClauses =
                filter.Values
                |> List.map (fun (system, code) ->
                    let codePh = addParam (code :> obj)

                    match system with
                    | None ->
                        $"EXISTS (SELECT 1 FROM search_token tk WHERE tk.resource_type = {rtPh} AND tk.id = t.id AND tk.param = {paramPh} AND tk.code = {codePh})"
                    | Some sys ->
                        let sysPh = addParam (sys :> obj)

                        $"EXISTS (SELECT 1 FROM search_token tk WHERE tk.resource_type = {rtPh} AND tk.id = t.id AND tk.param = {paramPh} AND tk.code = {codePh} AND tk.system = {sysPh})")
                |> String.concat " OR "

            sb.Append($" AND ({valueClauses})") |> ignore

        for filter in plan.Refs do
            let paramPh = addParam (filter.Param :> obj)

            let valueClauses =
                filter.Values
                |> List.map (fun target ->
                    let tPh = addParam (target :> obj)

                    $"EXISTS (SELECT 1 FROM search_ref r WHERE r.resource_type = {rtPh} AND r.id = t.id AND r.param = {paramPh} AND r.target = {tPh})")
                |> String.concat " OR "

            sb.Append($" AND ({valueClauses})") |> ignore

        { WhereClause = sb.ToString()
          OrderBy = orderBy plan
          Parameters = List.rev parameters }

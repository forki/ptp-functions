﻿module Ptp.Database

open Chessie.ErrorHandling
open Dapper
open System.Collections.Generic
open System.Data.SqlClient
open System.Dynamic
open Ptp.Core

type DateSelectArgs = {Date:string}
type IdSelect = {Id:int}
type LinkSelect = {Link:string}
type IdListSelect = {Ids:int[]}
type LinksListSelect = {Links:string[]}

let sqlConnection() = 
    new SqlConnection((env "SqlServer.ConnectionString"))

let expand (param : Map<string,_>) =
    let expando = ExpandoObject()
    let expandoDictionary = expando :> IDictionary<string,obj>
    for paramValue in param do
        expandoDictionary.Add(paramValue.Key, paramValue.Value :> obj)
    expando

let dapperQuery<'Result> (query:string) (connection:SqlConnection) =
    connection.Query<'Result>(query)
    
let dapperParameterizedQuery<'Result> (query:string) (param:obj) (connection:SqlConnection) : 'Result seq =
    connection.Query<'Result>(query, param)
    
let dapperMapParameterizedQuery<'Result> (query:string) (param : Map<string,_>) (connection:SqlConnection) : 'Result seq =
    let expando = ExpandoObject()
    let expandoDictionary = expando :> IDictionary<string,obj>
    for paramValue in param do
        expandoDictionary.Add(paramValue.Key, paramValue.Value :> obj)
    connection |> dapperParameterizedQuery query (expand param)
    
let dapperParameterizedCommand (query:string) (param:obj) (connection:SqlConnection) =
    connection.Execute(query, param) |> ignore

let toSqlValuesList items =
    items
    |> Seq.map (sprintf "('%s')")
    |> String.concat ", "

// ROP
let dbQuery<'Result> (queryText:string) =
    let op() =
        use sqlCon = sqlConnection()
        sqlCon
        |> dapperQuery<'Result> queryText
        |> Seq.cast<'Result>
    tryFail op (fun err -> DatabaseQueryError(QueryText(queryText), err))

let dbParameterizedQuery<'Result> (queryText:string) (param:obj)=
    let op() =
        use sqlCon = sqlConnection()
        sqlCon
        |> dapperParameterizedQuery<'Result> queryText param
        |> Seq.cast<'Result>
    tryFail op (fun err -> DatabaseQueryError(QueryText(queryText), err))

let expectOne query results = 
    match Seq.tryHead results with
    | None -> 
        let msg = "Expected query to return one value but got none."
        fail (DatabaseQueryError(QueryText(query), msg))
    | Some value -> 
        ok value

let dbQueryOne<'Result> (queryText:string) = trial {
    let! results = dbQuery<'Result> queryText
    let! ret = results |> expectOne queryText
    return ret
    }

let dbParameterizedQueryOne<'Result> (queryText:string) (param:obj) = trial {
    let! results = dbParameterizedQuery<'Result> queryText param
    let! ret = results |> expectOne queryText
    return ret
    }

let dbCommand (commandText:string) items = 
    let op() =
        use sqlCon = sqlConnection()
        sqlCon
        |> dapperParameterizedCommand commandText items
        items
    tryFail op (fun e -> DatabaseCommandError (CommandText(commandText),e))

let queryCurrentSessionYear () =
    dbQueryOne<string> "SELECT TOP 1 Name FROM Session WHERE Active = 1"

let dbQueryById<'Result> (queryText:string) ids = trial { 
    let idList = {Ids=(Seq.toArray ids)}
    let! res = dbParameterizedQuery<'Result> queryText idList
    return ids
 }

let dbQueryByLinks<'Result> (queryText:string) links = trial {
    let linksList = {Links=(Seq.toArray links)}
    let! res = dbParameterizedQuery<'Result> queryText linksList
    return links
}

let dbCommandById<'Result> (queryText:string) ids = trial { 
    let idList = {Ids=(Seq.toArray ids)}
    let! res = dbCommand queryText idList
    return ids
}

let queryAndFilterKnownLinks table links =
    match links with
    | EmptySeq -> links |> ok
    | _ ->
        let values = links |> toSqlValuesList
        let query = 
            (sprintf """
    SELECT a.Link FROM 
    ( VALUES %s ) AS a(Link)
    EXCEPT SELECT Link FROM %s;
    """ values table)
        dbQuery<string> query

let rollbackInsert table link (errs:WorkFlowFailure list) = 
    let queryText = sprintf "DELETE FROM %s WHERE Link=@Link" table
    let res = dbCommand queryText {Link=link}
    match res with
    | Ok _ -> errs |> Next.FailWith
    | Bad err -> err @ errs |> Next.FailWith

[<Literal>]
let SessionIdSubQuery = """
(SELECT TOP 1 Id FROM [Session] WHERE Active = 1)"""

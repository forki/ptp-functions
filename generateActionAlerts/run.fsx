// Configure Database 

#r "System.Data"
#r "../packages/Dapper/lib/net45/Dapper.dll"
#r "../packages/FSharp.Data/lib/portable-net45+sl50+netcore45/FSharp.Data.dll"
#r "../packages/Newtonsoft.Json/lib/net45/Newtonsoft.Json.dll"

#load "../shared/model.fs"
#load "../shared/queries.fs"
#load "../shared/http.fsx"
#load "../shared/db.fsx"

open System
open System.Data.SqlClient
open System.Dynamic
open System.Collections.Generic
open Dapper
open FSharp.Data
open FSharp.Data.JsonExtensions
open IgaTracker.Model
open IgaTracker.Queries
open IgaTracker.Http
open IgaTracker.Db
open Newtonsoft.Json

let locateUserToAlert users userBill =
    users |> Seq.find (fun u -> u.Id = userBill.UserId)

let prettyPrint actionType =
    match actionType with
    | ActionType.AssignedToCommittee -> "was assigned to the"
    | ActionType.CommitteeReading -> "was read in committee. The vote was"
    | ActionType.SecondReading -> "had a second reading. The vote was"
    | ActionType.ThirdReading -> "had a third reading. The vote was"
    | _ -> "(some other event type?)"

// Format a nice message body
let body (bill:Bill) (action:Action) =
    sprintf "In the %A at %s %s %s %s." action.Chamber (action.Date.ToString()) bill.Name (prettyPrint action.ActionType) action.Description   
let subject (bill:Bill) =
    sprintf "Update on %s" bill.Name
let generateEmailMessages (bill:Bill) action users userBills =
    userBills 
    |> Seq.map (fun ub -> 
        locateUserToAlert users ub 
        |> (fun u -> {MessageType=MessageType.Email; Recipient=u.Email; Subject=(subject bill); Body=(body bill action)}))
let generateSmsMessages (bill:Bill) action users userBills = 
    userBills 
    |> Seq.map (fun ub -> 
        locateUserToAlert users ub 
        |> (fun u -> {MessageType=MessageType.SMS; Recipient=u.Mobile; Subject=(subject bill); Body=(body bill action)}))

let fetchUserBills (cn:SqlConnection) id =
    cn.Open()
    let action = cn |> dapperMapParametrizedQuery<Action> "SELECT * FROM Action WHERE Id = @Id" (Map["Id", id :> obj] ) |> Seq.head
    let bill = cn |> dapperMapParametrizedQuery<Bill> "SELECT * FROM Bill WHERE Id = @Id" (Map["Id", action.BillId :> obj] ) |> Seq.head
    let userBills = cn |> dapperMapParametrizedQuery<UserBill> "SELECT * FROM UserBill WHERE BillId = @Id" (Map["Id", action.BillId :> obj] )
    let userIds = userBills |> Seq.map (fun ub -> ub.UserId)
    let users = cn |> dapperMapParametrizedQuery<User> "SELECT * FROM [User] WHERE Id IN @Ids" (Map["Ids", userIds :> obj] )
    cn.Close()
    (bill, action, users, userBills)

let generateAlerts (cn:SqlConnection) id =
    let (bill, action, users, userBills) = id |> fetchUserBills cn
    let emailMessages = userBills |> Seq.filter(fun ub -> ub.ReceiveAlertEmail) |> generateEmailMessages bill action users
    let smsMessages = userBills |> Seq.filter(fun ub -> ub.ReceiveAlertSms) |>  generateSmsMessages bill action users
    (emailMessages, smsMessages)

#r "../packages/Microsoft.Azure.WebJobs/lib/net45/Microsoft.Azure.WebJobs.Host.dll"

open Microsoft.Azure.WebJobs.Host

let Run(actionId: string, notifications: ICollector<string>, log: TraceWriter) =
    log.Info(sprintf "F# function 'generateActionAlerts' executed for action %s at %s" actionId (DateTime.Now.ToString()))
    let cn = new SqlConnection(System.Environment.GetEnvironmentVariable("SqlServer.ConnectionString"))
    let (emailMessages, smsMessages) = (Int32.Parse(actionId)) |> generateAlerts cn
    emailMessages |> Seq.iter (fun m -> log.Info(sprintf "Enqueuing email to '%s' re: '%s'" m.Recipient m.Subject))
    emailMessages |> Seq.iter (fun m -> notifications.Add(JsonConvert.SerializeObject(m)))
    smsMessages |> Seq.iter (fun m -> log.Info(sprintf "Enqueuing SMS to '%s' re: '%s'" m.Recipient m.Subject))
    smsMessages |> Seq.iter (fun m -> notifications.Add(JsonConvert.SerializeObject(m)))
// Configure Database 

#r "System.Data"
#r "../packages/Dapper/lib/net45/Dapper.dll"
#r "../packages/Newtonsoft.Json/lib/net45/Newtonsoft.Json.dll"
#r "../packages/Microsoft.Azure.WebJobs/lib/net45/Microsoft.Azure.WebJobs.Host.dll"

#load "../shared/queries.fs"
#load "../shared/model.fs"
#load "../shared/db.fsx"
#load "../shared/csv.fsx"

open System
open System.Data.SqlClient
open System.Dynamic
open System.Collections.Generic
open IgaTracker.Model
open IgaTracker.Queries
open IgaTracker.Db
open IgaTracker.Csv
open Newtonsoft.Json
open Microsoft.Azure.WebJobs.Host

[<CLIMutable>]
type DigestAction = {
    SessionName:string;
    BillName:string;
    Name:string;
    Title:string;
    BillChamber:Chamber;
    ActionChamber:Chamber;
    ActionType:ActionType;
    Description:string;
}

[<CLIMutable>]
type DigestScheduledAction = {
    SessionName:string;
    BillName:string;
    Title:string;
    BillChamber:Chamber;
    ActionChamber:Chamber;
    ActionType:ActionType;
    Date:DateTime;
    Start:string;
    End:string;
    Location:string;
}

type FetchArgs = { Today:DateTime }
type FetchWithIdsArgs = { Today:DateTime; Ids:seq<int> }
let printSectionTitle actionType = 
    match actionType with 
    | ActionType.CommitteeReading -> "Committee Hearings"
    | ActionType.SecondReading -> "Second Readings"
    | ActionType.ThirdReading -> "Third Readings"
    | _ -> ""

// ACTIONS
let listAction (a:DigestAction) = 
    sprintf "* [%s](https://iga.in.gov/legislative/%s/bills/%A/%s) ('%s'): %s" a.BillName a.SessionName a.BillChamber a.BillName a.Title a.Description

let listActions (actions:DigestAction seq) =
    match actions with 
    | seq when Seq.isEmpty seq -> "(None)"
    | seq -> 
        seq
        |> Seq.sortBy (fun action -> action.BillName)
        |> Seq.map listAction
        |> String.concat "\n"

let describeActions (chamber, actionType) (actions:DigestAction seq) = 
    let sectionTitle = sprintf "###%s  " (printSectionTitle actionType)
    let section = 
        actions 
        |> Seq.filter (fun action -> action.ActionChamber = chamber && action.ActionType = actionType) 
        |> listActions
    [sectionTitle; section]

let describeActionsForChamber chamber (actions:DigestAction seq) = 
    let header = sprintf "##Today's %A Activity  " chamber
    let committeReports = actions |> describeActions (chamber, ActionType.CommitteeReading)
    let secondReadings = actions |> describeActions (chamber, ActionType.SecondReading)
    let thirdReadings = actions |> describeActions (chamber, ActionType.ThirdReading)
    [header] @ committeReports @ secondReadings @ thirdReadings

// SCHEDULED ACTIONS
let listScheduledAction sa =
    let item = sprintf "* [%s](https://iga.in.gov/legislative/%s/bills/%A/%s) ('%s'); [%s](https://iga.in.gov/information/location_maps)" sa.BillName sa.SessionName sa.BillChamber sa.BillName sa.Title sa.Location
    match sa.Start with
    | "" -> item
    | timed -> sprintf "%s, %s-%s" item (DateTime.Parse(sa.Start).ToString("t")) (DateTime.Parse(sa.End).ToString("t"))
    
let listScheduledActions (scheduledActions:DigestScheduledAction seq) =
    scheduledActions 
    |> Seq.sortBy (fun action -> action.BillName)
    |> Seq.map listScheduledAction
    |> String.concat "\n"

let describeScheduledActions actionType (actions:DigestScheduledAction seq) = 
    let actionsOfType = actions |> Seq.filter (fun action -> action.ActionType = actionType)
    match actionsOfType with
    | sequence when Seq.isEmpty sequence -> []
    | sequence ->
        let sectionTitle = sprintf "###%s  " (printSectionTitle actionType)
        let section = sequence |> listScheduledActions
        [sectionTitle; section]

let describeScheduledActionsForDay (date:DateTime,scheduledActions) = 
    let header = sprintf "##Calendar for %s  " (date.ToString("D"))
    let committeReadings = scheduledActions |> describeScheduledActions ActionType.CommitteeReading
    let secondReadings = scheduledActions |> describeScheduledActions ActionType.SecondReading
    let thirdReadings = scheduledActions |> describeScheduledActions ActionType.ThirdReading
    [header] @ committeReadings @ secondReadings @ thirdReadings

let generateDigestMessage digestUser (actions,scheduledActions) filename =
    let salutation = "Hello! Please find attached today's legislative update.  "
    let houseActions = actions |> describeActionsForChamber Chamber.House
    let senateActions = actions |> describeActionsForChamber Chamber.Senate
    let upcomingActions = 
        scheduledActions 
        |> Seq.groupBy (fun scheduledAction -> scheduledAction.Date)
        |> Seq.collect describeScheduledActionsForDay
        |> Seq.toList
    let body = [salutation] @ houseActions @ senateActions @ upcomingActions |> String.concat "\n\n"
    let subject = sprintf "Legislative Update for %s" (DateTime.Now.ToString("D")) 
    {Message.Recipient=digestUser.Email; Subject = subject; Body=body; MessageType=MessageType.Email; Attachment=filename}

let generateDigestMessageForAllBills (digestUser,today) filename cn =
    let actions = cn |> dapperParametrizedQuery<DigestAction> FetchAllActions {FetchArgs.Today=today}
    let scheduledActions = cn |> dapperParametrizedQuery<DigestScheduledAction> FetchAllScheduledActions {FetchArgs.Today=today}
    generateDigestMessage digestUser (actions,scheduledActions) filename

let generateDigestMessageForBills (digestUser:User,today) filename billIds cn = 
    let actions = cn |> dapperParametrizedQuery<DigestAction> FetchActionsForBills {Today=today; Ids=billIds}
    let scheduledActions = cn |> dapperParametrizedQuery<DigestScheduledAction> FetchScheduledActionsForBills {Today=today; Ids=billIds}
    generateDigestMessage digestUser (actions,scheduledActions) filename

let generateSpreadsheetForBills (digestUser:User,today) storageConnStr billIds cn = 
    let userBillsSpreadsheetFilename = generateUserBillsSpreadsheetFilename today digestUser.Id
    cn
    |> dapperMapParametrizedQuery<BillStatus> FetchBillStatusForBills (Map["Ids", billIds:>obj])
    |> postSpreadsheet storageConnStr userBillsSpreadsheetFilename
    userBillsSpreadsheetFilename

let Run(user: string, notifications: ICollector<string>, log: TraceWriter) =
    log.Info(sprintf "F# function executed for '%s' at %s" user (DateTime.Now.ToString()))
    try
        //let digestUser = JsonConvert.DeserializeObject<User>(user)
        let digestUser = {User.Id=1;Name="John HOerr";Email="jhoerr@gmail.com";Mobile=null;DigestType=DigestType.MyBills}
        log.Info(sprintf "[%s] Generating %A digest for %s ..." (DateTime.Now.ToString("HH:mm:ss.fff")) digestUser.DigestType digestUser.Email)
        
        let today = DateTime.Now.Date
        let storageConnStr = System.Environment.GetEnvironmentVariable("AzureStorage.ConnectionString")
        let cn = new SqlConnection(System.Environment.GetEnvironmentVariable("SqlServer.ConnectionString"))
        let billIds = cn |> dapperMapParametrizedQuery<int> "SELECT BillId from UserBill WHERE UserId = @UserId" (Map["UserId", digestUser.Id:>obj])
        
        match digestUser.DigestType with 
        // nop: user has opted for a digest of 'my bills' but has not flagged any bills for tracking
        | DigestType.MyBills when Seq.isEmpty billIds -> printfn "User has not selected any bills "
        //  user has opted for a digest of 'my bills'
        | DigestType.MyBills -> 
            // generate a spreadsheet for the user and upload it to azure. save the filename.
            let filename = cn |> generateSpreadsheetForBills (digestUser,today) storageConnStr billIds
            // generate digest email message with attachment filename and queue for delivery
            cn |> generateDigestMessageForBills (digestUser,today) filename billIds |> JsonConvert.SerializeObject |> notifications.Add
        | DigestType.AllBills -> 
            // resolve the name of the pre-existing 'all bills' spreadsheet
            let filename = generateAllBillsSpreadsheetFilename today
            // generate digest email message with attachment filename and queue for delivery
            cn |> generateDigestMessageForAllBills (digestUser,today) filename |> JsonConvert.SerializeObject |> notifications.Add
        | _ -> raise (ArgumentException("Unrecognized digest type"))

        log.Info(sprintf "[%s] Generating action alerts [OK]" (DateTime.Now.ToString("HH:mm:ss.fff")))
    with
    | ex -> 
        log.Error(sprintf "Encountered error: %s" (ex.ToString())) 
        reraise()
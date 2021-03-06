﻿module Ptp.Workflow.Committee

open Chessie.ErrorHandling
open FSharp.Data
open FSharp.Data.JsonExtensions
open Ptp.Core
open Ptp.Cache
open Ptp.Model
open Ptp.Database
open Ptp.Http
open Ptp.Workflow.Common
open System

// COMMITTEES
let committeeModel c =

    let name = c?name.AsString()

    let chamber = 
        match c.TryGetProperty("chamber") with
        | Some prop -> 
            let name = prop?name.AsString()
            Enum.Parse(typedefof<Chamber>, name) :?> Chamber
        |None -> 
            Chamber.None

    let link = c?link.AsString()
    let committeeType = 
        match link  with
        | Contains("committee_i_") s    
            -> CommitteeType.Interim
        | Contains("committee_conference_") s 
            -> CommitteeType.Conference
        | _ -> CommitteeType.Standing
    
    { Committee.Id=0; 
      SessionId=0; 
      Chamber=chamber; 
      CommitteeType=committeeType;
      Name=name; 
      Link=link }

// COMMITTEE MEMBERSHIPS

/// Determine the makeup of a given committee.
/// Standing committees meet througout the session.
/// Conference committees meet to work out the difference between house/senate versions of a bill.
let determineComposition legislators (committee,json) =
    let committeeMembership (cid,lid,pos) = { Id=0; CommitteeId=cid; LegislatorId=lid; Position= pos; }
    let resolve strategy = strategy committee legislators json committeeMembership
    let one = resolve single
    let any = resolve multiple
    
    let standingCommittee () = 
        let chair =     one "chair" CommitteePosition.Chair
        let viceChair = one "viceChair" CommitteePosition.ViceChair
        let ranking =   one "rankingMinMember" CommitteePosition.RankingMinority
        let leadership = chair @ viceChair @ ranking
        let rankAndFileMembers =
            any "members" CommitteePosition.Member
            |> except' leadership (fun a -> a.LegislatorId)
            |> Seq.toList
        leadership @ rankAndFileMembers

    let conferenceCommittee () =
        let chair =     one "conferee_chair" CommitteePosition.Chair
        let conferees = any "conferees" CommitteePosition.Conferee
        let opp_confs = any "opp_conferees" CommitteePosition.Conferee
        let advisors =  any "advisors" CommitteePosition.Advisor
        let opp_advis = any "opp_advisors" CommitteePosition.Advisor
        chair @ conferees @ opp_confs @ advisors @ opp_advis
    
    if committee.Link.Contains("conference")
    then conferenceCommittee()
    else standingCommittee()

let getSessionLegislatorsQuery = (sprintf """
SELECT Id, Link 
FROM Legislator 
WHERE SessionId = %s""" SessionIdSubQuery)

let getKnownCommMembersQuery = """
SELECT Id, LegislatorId, CommitteeId, Position 
FROM LegislatorCommittee
WHERE CommitteeId = @Id"""

let insertCommMemberCommand = """
IF NOT EXISTS 
    ( SELECT Id from LegislatorCommittee 
      WHERE CommitteeId=@CommitteeId 
       AND LegislatorId=@LegislatorId 
       AND Position=@Position)
BEGIN
    INSERT INTO LegislatorCommittee 
    (CommitteeId, LegislatorId, Position) 
    VALUES (@CommitteeId, @LegislatorId, @Position)
END"""

let deleteQuery = """
DELETE FROM LegislatorCommittee 
WHERE Id IN @Ids"""

let getKnownMemberships (comm:Committee) = 
    dbParameterizedQuery<CommitteeMember> getKnownCommMembersQuery comm
    
let addNewMemberships (allMemberships, knownMemberships) = trial {
    let s = 
        allMemberships
        |> except' knownMemberships (fun x -> (x.CommitteeId, x.LegislatorId, x.Position))
        |> Seq.toList
    let! added = s |> Seq.map (dbCommand insertCommMemberCommand) |> collect
    return added
    }

let deleteOldMemberships (allMemberships, knownMemberships) = trial {
    let toDelete =
        knownMemberships
        |> except' allMemberships (fun x -> (x.CommitteeId, x.LegislatorId, x.Position))
    let! deleted = 
        toDelete
        |> Seq.map (fun m -> m.Id)
        |> dbCommandById deleteQuery
    return toDelete
    }

let updateMemberships (allMemberships, knownMemberships) = trial {
    let! added = (allMemberships, knownMemberships) |> addNewMemberships 
    let! deleted = (allMemberships, knownMemberships) |> deleteOldMemberships
    return added @ deleted
    }

let fetchCommitteeMetadata link =
    fetch link

let deserializeCommitteeModel json = trial {
    let! comm = json |> deserializeOneAs committeeModel
    return (comm,json)
    }

let insertCommitteeQuery = (sprintf """
IF NOT EXISTS 
    ( SELECT Id FROM Committee 
      WHERE Link=@Link )
BEGIN
    INSERT INTO Committee
    (Name,Link,Chamber,CommitteeType,SessionId)
    VALUES (@Name,@Link,@Chamber,@CommitteeType,%s)
END
SELECT * FROM Committee WHERE Link = @Link""" SessionIdSubQuery)

let addOrGetCommittee (comm,json) = trial {
    let! c = dbParameterizedQueryOne<Committee> insertCommitteeQuery comm
    return (c, json)
    }

let reconcileCommitteeMembers (comm:Committee,json) = trial {
    let! legislators = dbQuery<LinkAndId> getSessionLegislatorsQuery
    let linkAndId = {Id=comm.Id; Link=comm.Link}
    let allMemberships = determineComposition legislators (linkAndId,json)
    let! knownMemberships = getKnownMemberships comm
    let! updated = updateMemberships (allMemberships, knownMemberships)
    return updated
    }

/// Invalidate the Redis cache key for committees
let invalidateCommitteeCache =
    tryInvalidateCacheIfAny CommitteesKey

/// Invalidate the Redis cache key for committees
let invalidateMembershipCache =
    tryInvalidateCacheIfAny MembershipsKey

/// Find, add, and log new committees
let workflow link =
    fun () ->
        fetchCommitteeMetadata link
        >>= deserializeCommitteeModel
        >>= addOrGetCommittee
        >>= reconcileCommitteeMembers
        >>= invalidateCommitteeCache
        >>= invalidateMembershipCache
        |>  workflowTerminates
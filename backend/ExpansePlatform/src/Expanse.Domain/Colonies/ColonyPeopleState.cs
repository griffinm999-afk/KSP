using System;
using System.Collections.Generic;

namespace Expanse.Domain.Colonies
{
    // A named KSP roster record is reserved; population counters never create crew.
    public sealed class ColonyPeopleOperation
    {
        public string Id { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string RosterId { get; set; } = "";
        public string Kind { get; set; } = "arrival";
        public string State { get; set; } = "reserved";
        public string HomeFacilityId { get; set; } = "";
        public uint HomePartId { get; set; }
        public string RouteId { get; set; } = "";
        public string RouteHash { get; set; } = "";
        public long Funds { get; set; }
        public bool FundsPaid { get; set; }
        public double TravelSeconds { get; set; }
        public double DepartUt { get; set; }
        public double ArrivalUt { get; set; }
        public string ExpectedRosterType { get; set; } = "Crew";
        public string Provider { get; set; } = "";
        public string BeforeWitness { get; set; } = "";
        public string AfterWitness { get; set; } = "";
        public string Reason { get; set; } = "Awaiting verified fare debit.";
        public string SourceVesselId { get; set; } = "";
        public uint SourcePartId { get; set; }
        public string JobFacilityId { get; set; } = "";
        public string DestinationVesselId { get; set; } = "";
        public uint WorkPartId { get; set; }
        public string WorkerQuoteId { get; set; } = "";
        public bool ExplicitMissionCrewAssignment { get; set; }
        public List<string> SourceOccupantsBefore { get; set; } = new List<string>();
        public List<string> DestinationOccupantsBefore { get; set; } = new List<string>();
    }

    public sealed class ColonyPassengerRoute
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Body { get; set; } = "";
        public string Hash { get; set; } = "";
        public long Fare { get; set; }
        public long RecruitmentFee { get; set; }
        public double TravelSeconds { get; set; }
        public int ConcurrentSeats { get; set; }
        public bool Qualified { get; set; }
        public bool DevelopmentOnly { get; set; }
        public string Evidence { get; set; } = "";
    }

    public sealed class ColonyRosterWitness
    {
        public string RosterId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Trait { get; set; } = "";
        public string Type { get; set; } = "";
        public string Status { get; set; } = "";
        public string VesselId { get; set; } = "";
        public uint PartId { get; set; }
        public string ContextKey { get; set; } = "";
        public double ObservedUt { get; set; }
        public bool Current { get; set; }
        public bool ProtectedMissionCrew { get; set; }
    }

    public sealed class ColonySeatWitness
    {
        public string FacilityId { get; set; } = "";
        public string VesselId { get; set; } = "";
        public uint PartId { get; set; }
        public int Capacity { get; set; }
        public List<string> Occupants { get; set; } = new List<string>();
        public bool Current { get; set; }
        public bool LoadedUnpacked { get; set; }
        public bool CrewMutationSupported { get; set; }
        public bool HousingCertified { get; set; }
        public bool UtilitiesQualified { get; set; }
        public bool WorkSupported { get; set; }
        public string ContextKey { get; set; } = "";
        public string Evidence { get; set; } = "";
        public bool SurfaceTransferSupported { get; set; }
        public string Body { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public sealed class ColonyPeopleEnvironment
    {
        public List<ColonyRosterWitness> Roster { get; set; } = new List<ColonyRosterWitness>();
        public List<ColonySeatWitness> Seats { get; set; } = new List<ColonySeatWitness>();
        public List<ColonyPassengerRoute> Routes { get; set; } = new List<ColonyPassengerRoute>();
        // Site membership is witnessed from actual position, including visiting
        // crew outside adopted buildings. Incomplete observations preserve people.
        public Dictionary<string, List<string>> PresentByColony { get; set; } = new Dictionary<string, List<string>>();
        public bool PresenceComplete { get; set; }
    }
}

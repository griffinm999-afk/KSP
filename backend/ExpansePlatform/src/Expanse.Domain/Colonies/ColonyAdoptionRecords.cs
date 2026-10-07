using System.Collections.Generic;

namespace Expanse.Domain.Colonies
{
    // Review output only; adoption introduces no new saved authority records.
    public sealed class ColonyAdoptionQuote
    {
        public bool CanApprove { get; set; }
        public string Reason { get; set; } = "";
        public string Id { get; set; } = "";
        public string AdoptionWitnessHash { get; set; } = "";
        public string ColonyId { get; set; } = "";
        public string FacilityId { get; set; } = "";
        public string Name { get; set; } = "";
        public string VesselId { get; set; } = "";
        public List<uint> PartIds { get; set; } = new List<uint>();
        public ColonySite Site { get; set; } = new ColonySite();
        public List<string> PresentPeople { get; set; } = new List<string>();
        public int VisitorCount { get; set; }
        public int NewVisitorCount { get; set; }
    }
}

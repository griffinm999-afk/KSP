using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Expanse.Domain.Colonies
{
    public static class ColonyLimits
    {
        public const int MaxBytes = 4 * 1024 * 1024;
        public const int Colonies = 16, Facilities = 256, Residents = 512, Plots = 512;
        public const int Orders = 256, Shipments = 256, Effects = 512, Receipts = 512, Journal = 2048;
        public const int Resources = 128, Suppliers = 128, EventsPerTick = 100;
        public const long Units = 1_000_000;
        public const long MaxQuantity = 1_000_000_000_000_000;
        public const long MaxFunds = 1_000_000_000_000;
        public const double KerbinDay = 21_600;
    }

    public static partial class ColonyStateCodec
    {
        public static byte[] Serialize(ColonyState state)
        {
            Validate(state);
            return ColonyJson.Serialize(state, ColonyLimits.MaxBytes);
        }

        public static ColonyState Deserialize(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > ColonyLimits.MaxBytes) throw new InvalidDataException("Invalid colony payload length.");
            var result = ColonyJson.Deserialize<ColonyState>(bytes, ColonyLimits.MaxBytes);
            MigratePeople(result);
            MigrateServices(result);
            MigrateEconomy(result);
            MigrateWolf(result);
            MigratePlanning(result);
            MigratePhysicalProcurement(result);
            Validate(result);
            return result;
        }

        public static ColonyState Copy(ColonyState state)
        {
            Validate(state);
            return CopyAfterValidation(state);
        }

        // Internal only: call immediately after validating the same unchanged graph.
        // Keep detached ownership, additive migrations and output validation.
        internal static ColonyState CopyAfterValidation(ColonyState state)
        {
            var result = ColonyStateDetachedCopy.Copy(state);
            // Preserve the same additive migrations and validation as a save
            // round trip; only the temporary JSON tree is omitted.
            MigratePeople(result);
            MigrateServices(result);
            MigrateEconomy(result);
            MigrateWolf(result);
            MigratePlanning(result);
            MigratePhysicalProcurement(result);
            Validate(result);
            return result;
        }
        // ConfigNode treats // as a comment even inside values. The URL-safe
        // alphabet prevents a valid payload from being truncated during save/load.
        public static string EncodeSaveValue(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > ColonyLimits.MaxBytes) throw new InvalidDataException("Invalid colony payload length.");
            return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_');
        }
        public static byte[] DecodeSaveValue(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > (ColonyLimits.MaxBytes + 2) / 3 * 4 ||
                value.Any(c => !(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '=')))
                throw new InvalidDataException("Invalid colony save alphabet or length.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/')); }
            catch (FormatException ex) { throw new InvalidDataException("Malformed colony save encoding.", ex); }
            if (EncodeSaveValue(bytes) != value) throw new InvalidDataException("Non-canonical colony save encoding.");
            return bytes;
        }
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static string CommandHash(ColonyCommand command)
        {
            if (command == null || command.Fields == null || command.Fields.Count > 64) throw new InvalidDataException("Invalid command fields.");
            Text(command.Kind, 64); Text(command.ContextKey, 256); Text(command.ColonyId, 64); Text(command.TargetId, 128); Text(command.QuoteId, 128);
            if(command.FoundingIntent!=null)
            {
                if(command.Kind!="approveFoundingPlan"&&command.Kind!="surveyFoundingPlan")throw new InvalidDataException("Founding intent is not valid for this command.");
                ValidateFoundingIntent(command.FoundingIntent);
            }
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(command.ContextKey); writer.Write(command.ExpectedRevision); writer.Write(command.Kind);
                writer.Write(command.ColonyId); writer.Write(command.TargetId); writer.Write(command.QuoteId);
                foreach (var field in command.Fields.OrderBy(x => x.Key, StringComparer.Ordinal))
                { Text(field.Key, 64); Text(field.Value, 4096); writer.Write(field.Key); writer.Write(field.Value); }
                // Null additive intents preserve pre-existing operation hashes.
                if(command.FoundingIntent!=null){byte[] intent=ColonyJson.Serialize(FoundingIntentTerms(command.FoundingIntent),32768);writer.Write(-1);writer.Write("FoundingIntent.v1");writer.Write(intent.Length);writer.Write(intent);}
                writer.Flush(); return Hash(stream.ToArray());
            }
        }

        public static void Validate(ColonyState state)
        {
            if (state == null || state.SchemaVersion != 1) throw new InvalidDataException("Unsupported colony schema; preserve the original node for recovery.");
            Id(state.WorldId); Nonnegative(state.Revision); if (state.NextSequence < 1) Fail("Invalid colony sequence.");
            Nonnegative(state.CompactedJournalCount); Text(state.CompactedJournalHash, 64);
            if (state.CompactedJournalCount > 0 && (state.CompactedJournalHash.Length != 64 || !state.CompactedJournalHash.All(Uri.IsHexDigit))) Fail("Invalid audit compaction checkpoint.");
            Time(state.SimulatedUt); Time(state.TargetUt); if (state.TargetUt < state.SimulatedUt) Fail("Target time predates accepted colony simulation.");
            Rows(state.Colonies, ColonyLimits.Colonies); Rows(state.Construction, ColonyLimits.Orders); Rows(state.Shipments, ColonyLimits.Shipments);
            Rows(state.Effects, ColonyLimits.Effects); Rows(state.Receipts, ColonyLimits.Receipts); Rows(state.Journal, ColonyLimits.Journal); Rows(state.Suppliers, ColonyLimits.Suppliers);
            Unique(state.Colonies.Select(x => x.Id)); Unique(state.Construction.Select(x => x.Id)); Unique(state.Shipments.Select(x => x.Id));
            Unique(state.Effects.Select(x => x.Id)); Unique(state.Receipts.Select(x => x.OperationId)); Unique(state.Suppliers.Select(x => x.Id));
            var colonies = new HashSet<string>(state.Colonies.Select(x => x.Id), StringComparer.Ordinal);
            var facilities = new HashSet<string>(StringComparer.Ordinal);
            var vessels = new HashSet<string>(StringComparer.Ordinal);
            var roster = new HashSet<string>(StringComparer.Ordinal);
            int facilityCount = 0, residentCount = 0, plotCount = 0;
            foreach (var colony in state.Colonies)
            {
                Id(colony.Id); Text(colony.Name, 160, true); Site(colony.Site); Charter(colony.Charter); Time(colony.FoundedUt); Time(colony.SupportAccountedUt);
                if (colony.SupportCommissionedUt.HasValue) Time(colony.SupportCommissionedUt.Value);
                Funds(colony.SpentFunds); Text(colony.Status, 64); Text(colony.SupportPolicy, 128); Text(colony.SupportStatus, 512);
                Text(colony.SupportPolicyHash, 128);
                Quantity(colony.SupportMicroUnitsPerPersonDay);
                Quantity(colony.SupportConsumedMicroUnits); Quantity(colony.SupportPendingJournalMicroUnits);
                if (colony.SupportPendingJournalMicroUnits > colony.SupportConsumedMicroUnits) Fail("Unjournaled support exceeds total consumption.");
                if (colony.SupportMicroUnitsPerPersonDay == 0 || colony.SupportRemainder < 0 || colony.SupportRemainder >= 1) Fail("Invalid support rate or fractional remainder.");
                Rows(colony.Facilities, ColonyLimits.Facilities); Rows(colony.Residents, ColonyLimits.Residents); Rows(colony.Plots, ColonyLimits.Plots);
                Rows(colony.Stock, ColonyLimits.Resources); Rows(colony.VisitorRosterIds, ColonyLimits.Residents); Rows(colony.Proposals, ColonyLimits.Orders);
                Unique(colony.Facilities.Select(x => x.Id)); Unique(colony.Plots.Select(x => x.Id)); Unique(colony.Stock.Select(x => x.Resource));
                Unique(colony.Residents.Select(x => x.Id)); Unique(colony.Proposals.Select(x => x.Id)); Unique(colony.VisitorRosterIds);
                facilityCount += colony.Facilities.Count; residentCount += colony.Residents.Count + colony.VisitorRosterIds.Count; plotCount += colony.Plots.Count;
                foreach (var facility in colony.Facilities)
                {
                    Id(facility.Id); Id(facility.VesselId); if (!facilities.Add(facility.Id) || !vessels.Add(facility.VesselId)) Fail("Facility or physical vessel is assigned twice.");
                    Text(facility.Name, 160); Text(facility.TemplateId, 128); Text(facility.TemplateHash, 128); Text(facility.PlotId, 64);
                    Choice(facility.State, "adopted", "commissioning", "operational", "held", "retired"); Choice(facility.ProductionOwner, "physical", "colony");
                    Count(facility.CertifiedHomes, 512); Count(facility.RequiredWorkers, 512); Text(facility.RequiredTrait, 128); Text(facility.LastReason, 512);
                    Rows(facility.PartIds, 512); if (facility.PartIds.Any(x => x == 0) || facility.PartIds.Distinct().Count() != facility.PartIds.Count) Fail("Invalid part identities.");
                    var q = facility.Qualification ?? throw new InvalidDataException("Missing qualification.");
                    Text(q.Provider, 128); Text(q.Context, 64); Text(q.EvidenceHash, 128); Time(q.ObservedUt);
                    if(q.ReactorContinuation!=null)
                    {ValidateReactorContinuation(q.ReactorContinuation);if(q.ReactorContinuation.WorldId!=state.WorldId||q.ReactorContinuation.VesselId!=facility.VesselId)Fail("Reactor proof belongs to another saved world/vessel.");}
                    if (facility.CertifiedHomes > 0 && !q.HousingCertified) Fail("Homes lack certification.");
                }
                foreach (var plot in colony.Plots)
                {
                    Id(plot.Id); Coordinate(plot.Latitude, plot.Longitude); Range(plot.Heading, 0, 360); Range(plot.WidthMeters, .1, 2000); Range(plot.LengthMeters, .1, 2000);
                    Text(plot.SurveyHash, 128); Text(plot.ReservedBy, 64); Text(plot.OccupiedBy, 64);
                    Text(plot.TemplateId, 128); Text(plot.TemplateHash, 128); Text(plot.EvidenceContext, 256); Text(plot.SurveyProvenance, 512); Time(plot.ObservedUt);
                    if (plot.ReservedBy.Length > 0 && !state.Construction.Any(x => x.Id == plot.ReservedBy && x.ColonyId == colony.Id && x.PlotId == plot.Id && x.State != "cancelled") &&
                        !state.Plans.Any(p => p.Id == plot.ReservedBy && p.ColonyId == colony.Id && p.State != "complete" && p.State != "cancelled" && p.Buildings.Any(b => b.PlotId == plot.Id && b.OrderId.Length == 0))) Fail("Plot reservation has no construction or approved plan owner.");
                    if (plot.OccupiedBy.Length > 0 && !colony.Facilities.Any(x => x.Id == plot.OccupiedBy)) Fail("Occupied plot has no facility.");
                }
                foreach (var stock in colony.Stock)
                {
                    Text(stock.Resource, 128, true); Quantity(stock.Amount); Quantity(stock.Capacity); Quantity(stock.Reserved); Quantity(stock.IncomingReserved);
                    Quantity(stock.SupportFloor); Quantity(stock.ImportedAmount); Quantity(stock.UnitMassMicroTonnes); Quantity(stock.UnitVolumeMilliLiters);
                    if (stock.Amount > stock.Capacity || stock.Reserved > stock.Amount || stock.IncomingReserved > stock.Capacity - stock.Amount || stock.ImportedAmount > stock.Amount) Fail("Stock violates capacity, reservation or provenance conservation.");
                }
                foreach (var resident in colony.Residents)
                {
                    Id(resident.Id); Text(resident.RosterId, 160, true); Text(resident.Name, 160, true); Text(resident.Trait, 128);
                    Text(resident.HomeFacilityId, 64); Text(resident.JobFacilityId, 64); Choice(resident.Status, "resident", "arriving", "departing", "missing");
                    if (!roster.Add(resident.RosterId)) Fail("Roster identity assigned more than once.");
                    if (resident.HomeFacilityId.Length > 0 && !colony.Facilities.Any(x => x.Id == resident.HomeFacilityId && x.CertifiedHomes > 0)) Fail("Resident lacks a certified home.");
                    if (resident.JobFacilityId.Length > 0 && !colony.Facilities.Any(x => x.Id == resident.JobFacilityId)) Fail("Resident job references a missing facility.");
                }
                foreach (string visitor in colony.VisitorRosterIds) { Text(visitor, 160, true); if (!roster.Add(visitor)) Fail("Visitor counted twice or is already a resident."); }
                foreach (var f in colony.Facilities)
                    if (colony.Residents.Count(x => x.HomeFacilityId == f.Id) > f.CertifiedHomes) Fail("Certified housing overbooked.");
            }
            if (facilityCount > ColonyLimits.Facilities || residentCount > ColonyLimits.Residents || plotCount > ColonyLimits.Plots) Fail("Supported colony entity limit exceeded.");
            foreach (var order in state.Construction)
            {
                Id(order.Id); RequireColony(colonies, order.ColonyId); Id(order.PlotId); Text(order.TemplateId, 128, true); Text(order.TemplateHash, 128, true);
                Choice(order.State, "reserved", "building", "awaitingPlacement", "placing", "commissioning", "operational", "cancelled", "held");
                Text(order.Reason, 512); Funds(order.Funds); Time(order.WorkRequired); Time(order.WorkCompleted); Time(order.AccountedUt); Text(order.FacilityId, 64);
                if (order.WorkRequired <= 0 || order.WorkCompleted > order.WorkRequired) Fail("Invalid construction work.");
                Funds(order.FundsConsumed);
                if (order.FundsConsumed > order.Funds || !order.FundsPaid && (order.FundsConsumed > 0 || order.WorkCompleted > 0) || order.WorkCompleted > 0 && !order.MaterialsConsumed) Fail("Construction work or consumed funds lack paid escrow/materials.");
                Materials(order.Materials); Rows(order.Dependencies, ColonyLimits.Orders); Unique(order.Dependencies);
                var owner = state.Colonies.Single(x => x.Id == order.ColonyId);
                if (!owner.Plots.Any(x => x.Id == order.PlotId) || order.Materials.Any(x => !owner.Stock.Any(y => y.Resource == x.Resource))) Fail("Construction lacks a plot or material store.");
                if (order.Dependencies.Any(x => x == order.Id || !state.Construction.Any(y => y.Id == x && y.ColonyId == order.ColonyId))) Fail("Invalid construction dependency.");
            }
            foreach (var order in state.Construction) CheckDependencies(state, order.Id, new HashSet<string>(), new HashSet<string>());
            foreach (var shipment in state.Shipments)
            {
                Id(shipment.Id); RequireColony(colonies, shipment.ColonyId); Text(shipment.SupplierId, 128); Choice(shipment.Kind, "import", "export", "passenger");
                Choice(shipment.State, "reserved", "inTransit", "arrived", "cancelled", "held"); Text(shipment.Resource, 128, true); Text(shipment.Reason, 512);
                Quantity(shipment.Amount); if (shipment.Amount == 0) Fail("Empty shipment."); Funds(shipment.Funds); Time(shipment.DepartUt); Time(shipment.ArrivalUt); Time(shipment.TravelSeconds);
                Quantity(shipment.MassMicroTonnes); Quantity(shipment.VolumeMilliLiters);
                if (shipment.TravelSeconds <= 0 || shipment.ArrivalUt < shipment.DepartUt) Fail("Invalid shipment chronology.");
            }
            foreach (var effect in state.Effects)
            {
                Id(effect.Id); Id(effect.OperationId); RequireColony(colonies, effect.ColonyId); Text(effect.TargetId, 128, true); Text(effect.Kind, 64, true);
                Choice(effect.State, "prepared", "applying", "applied", "held", "cancelled");
                if (effect.FundsDelta < -ColonyLimits.MaxFunds || effect.FundsDelta > ColonyLimits.MaxFunds) Fail("Invalid funds delta.");
                Text(effect.Provider, 128); Text(effect.BeforeWitness, 4096); Text(effect.AfterWitness, 4096); Text(effect.Reason, 512);
            }
            ValidateConstructionActivations(state);
            ValidateConstructionRetries(state);
            foreach (var supplier in state.Suppliers)
            {
                Text(supplier.Id, 128, true); Text(supplier.Resource, 128, true); Quantity(supplier.Available); Quantity(supplier.Reserved);
                Text(supplier.FreightPoolId, 128); Text(supplier.DestinationBody, 128);
                if (supplier.Reserved > supplier.Available) Fail("Supplier stock oversubscribed.");
                Funds(supplier.FundsPerUnit); Funds(supplier.FreightFunds); Quantity(supplier.MassCapacityMicroTonnes); Quantity(supplier.VolumeCapacityMilliLiters);
                Count(supplier.ConcurrentCapacity, 256); Time(supplier.TravelSeconds);
            }
            foreach (var receipt in state.Receipts)
            { Id(receipt.OperationId); Text(receipt.PayloadHash, 64, true); if (receipt.Sequence < 1 || receipt.Sequence >= state.NextSequence || receipt.Revision > state.Revision) Fail("Invalid receipt lineage."); Text(receipt.ResultId, 128); Text(receipt.Outcome, 64); }
            foreach (var entry in state.Journal)
            { Time(entry.Ut); Text(entry.ColonyId, 64); Text(entry.OperationId, 64); Text(entry.Kind, 64); Text(entry.Detail, 512); Text(entry.Resource, 128); }
            ValidateReservations(state);
            ValidatePeople(state);
            ValidateServices(state);
            ValidateEconomy(state);
            ValidateWolf(state);
            ValidateConstruction(state);
            ValidatePlanning(state);
            ValidatePhysicalProcurement(state);
        }

        public static void ValidateReservations(ColonyState state)
        {
            foreach (var colony in state.Colonies) foreach (var stock in colony.Stock)
            {
                long material = state.Construction.Where(x => x.ColonyId == colony.Id && !x.MaterialsConsumed && x.State != "cancelled")
                    .SelectMany(x => x.Materials).Where(x => x.Resource == stock.Resource).Sum(x => x.Amount);
                long inbound = state.Shipments.Where(x => x.ColonyId == colony.Id && x.Kind == "import" && x.Resource == stock.Resource && x.State != "arrived" && x.State != "cancelled").Sum(x => x.Amount);
                long service = ServiceReserved(state,colony.Id,stock.Resource);
                if (stock.Reserved != checked(material+service+ColonyEngine.PlanningMaterialReserved(state,colony.Id,stock.Resource)+ColonyEngine.PhysicalSourceReserved(state,colony.Id,stock.Resource)) || stock.IncomingReserved != checked(inbound+ColonyEngine.PhysicalIncomingReserved(state,colony.Id,stock.Resource))) Fail("Reservation has no matching owner or was counted twice.");
            }
            foreach (var supplier in state.Suppliers)
            {
                long reserved = state.Shipments.Where(x => x.SupplierId == supplier.Id && x.Kind == "import" && (x.State == "reserved" || x.State == "held" && x.DepartUt == 0)).Sum(x => x.Amount);
                if (supplier.Reserved != reserved) Fail("Supplier reservation mismatch.");
            }
        }

        public static void Site(ColonySite site) { if (site == null) Fail("Missing site."); Text(site!.Body, 128, true); Text(site.Biome, 128); Coordinate(site.Latitude, site.Longitude); Range(site.RadiusMeters, 10, 5000); }
        public static void Charter(ColonyCharter charter)
        {
            if (charter == null) Fail("Missing charter."); Text(charter!.Purpose, 160, true); Count(charter.PopulationTarget, 512); Count(charter.ResidentLimit, 512); Count(charter.VisitorLimit, 512);
            if (charter.PopulationTarget > charter.ResidentLimit) Fail("Population target exceeds charter limit.");
            Funds(charter.FoundingBudget); Funds(charter.CashFloor); Funds(charter.SpendingLimit); Range(charter.ReserveDays, 1, 365); Choice(charter.GrowthPolicy, "approval", "automatic", "disabled");
        }
        public static void Materials(List<MaterialRequirement> rows) { Rows(rows, ColonyLimits.Resources); Unique(rows.Select(x => x.Resource)); foreach (var m in rows) { Text(m.Resource, 128, true); Quantity(m.Amount); if (m.Amount == 0) Fail("Zero material requirement."); } }
        static void CheckDependencies(ColonyState state, string id, HashSet<string> active, HashSet<string> done)
        {
            if (done.Contains(id)) return;
            if (!active.Add(id)) Fail("Construction dependency cycle.");
            foreach (string dependency in state.Construction.Single(x => x.Id == id).Dependencies) CheckDependencies(state, dependency, active, done);
            active.Remove(id); done.Add(id);
        }
        static void RequireColony(HashSet<string> colonies, string id) { Id(id); if (!colonies.Contains(id)) Fail("Unknown colony reference."); }
        public static void Coordinate(double lat, double lon) { Range(lat, -90, 90); Range(lon, -180, 180); }
        public static void Quantity(long value) { if (value < 0 || value > ColonyLimits.MaxQuantity) Fail("Invalid resource quantity."); }
        public static void Funds(long value) { if (value < 0 || value > ColonyLimits.MaxFunds) Fail("Invalid funds amount."); }
        public static void Id(string value) { Guid id; if (!Guid.TryParseExact(value, "D", out id) || id == Guid.Empty) Fail("Invalid persistent identifier."); }
        public static void Time(double value) => Range(value, 0, 1e12);
        public static void Range(double value, double min, double max) { if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max) Fail("Numeric value is outside the supported range."); }
        public static void Text(string value, int max, bool required = false) { if (value == null || value.Length > max || required && string.IsNullOrWhiteSpace(value) || value != null && value.Any(c => char.IsControl(c) && c != '\n' && c != '\t')) Fail("Invalid or oversized text."); }
        static void Count(int value, int max) { if (value < 0 || value > max) Fail("Entity count outside supported limits."); }
        static void Nonnegative(long value) { if (value < 0) Fail("Negative sequence."); }
        static void Choice(string value, params string[] options) { if (!options.Contains(value)) Fail("Unsupported state: " + value); }
        static void Rows<T>(ICollection<T> rows, int max) { if (rows == null || rows.Count > max || rows.Any(x => x == null)) Fail("Missing or oversized record collection."); }
        static void Unique(IEnumerable<string> ids) { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var id in ids) { if (string.IsNullOrEmpty(id) || !seen.Add(id)) Fail("Missing or duplicate identity."); } }
        static void Fail(string message) => throw new InvalidDataException(message);
    }
}

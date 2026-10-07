using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed class ColonySiteSurveyResult
    {
        public bool Clear { get; set; }
        public string Reason { get; set; } = "";
        public string TemplateHash { get; set; } = "";
        public string ContextKey { get; set; } = "";
        public string TerrainWitness { get; set; } = "";
        public string SurfaceCollisionWitness { get; set; } = "";
        public ColonyPlot Plot { get; set; }
        public double MaximumSlopeDegrees { get; set; }
        public double MaximumSupportGapMetres { get; set; }
        public double TerrainHeight { get; set; }
        public int SampleCount { get; set; }
    }

    public sealed class ColonySiteLayoutProposal
    {
        public string Reason { get; set; } = "";
        public double StreetClearWidthMetres { get; set; }
        public double AlongRowSpacingMetres { get; set; }
        public List<ColonyPlot> Plots { get; set; } = new List<ColonyPlot>();
    }

    // All methods are read-only: no craft/GameObject creation, vessel movement,
    // state write or save mutation. The caller supplies trusted catalog terms and
    // plots scoped to THIS body; Plot has no separate celestial body field.
    public static class ColonySiteSurvey
    {
        private const string ReservationProvenance = "Loaded terrain preview; symmetric deployment/access reservation; final assembly/deployment/stability required";

        public static ColonySiteSurveyResult Survey(ColonyTemplate template, string bodyName, double latitude, double longitude, double headingDegrees,
            IReadOnlyList<ColonyPlot> registeredPlots, string contextKey, string excludePlotId = null)
        {
            var result = new ColonySiteSurveyResult { TemplateHash = template == null ? "" : template.Hash, ContextKey = contextKey ?? "" };
            try
            {
                ValidateTerms(template, latitude, longitude, headingDegrees);
                Need(ColonyPlacementRequest.Token(contextKey, 512), "Trusted current save/load context is missing");
                Need(HighLogic.LoadedSceneIsFlight && FlightGlobals.ready && !FlightDriver.Pause && Time.timeScale > 0 && TimeWarp.CurrentRate == 1,
                    "Survey requires loaded unpaused flight at 1×; map/PQS estimates alone cannot clear a plot");
                var active = FlightGlobals.ActiveVessel;
                Need(active != null && active.loaded && active.Landed && !active.Splashed && active.mainBody != null && active.mainBody.bodyName == bodyName,
                    "Survey requires a loaded landed reference on the requested celestial body");
                var body = active.mainBody; Need(body.pqsController != null && ColonyPlacementRequest.Range(body.Radius, 1000, 1000000000), "Solid-body PQS terrain/radius is unavailable");
                Need(registeredPlots != null && registeredPlots.Count <= ColonyLimits.Plots, "Registered plot set is absent or exceeds its bound");
                result.TerrainHeight = Height(body, latitude, longitude);
                Vector3d surface = body.GetWorldSurfacePosition(latitude, longitude, result.TerrainHeight);
                Need((active.GetWorldPos3D() - surface).magnitude <= 1000, "Plot is more than 1,000 m from loaded terrain reference");
                Vector3 radial = (Vector3)(surface - body.position).normalized; RaycastHit centerHit;
                string continuousTerrainReason;
                Need(ColonyPlacementParallaxSurface.TerrainRay(body, (Vector3)surface + radial * 50, -radial, 100, out centerHit, out continuousTerrainReason), continuousTerrainReason);
                Vector3 up = centerHit.normal.normalized;
                Need(Vector3.Dot(up, radial) >= 0.9f, "Plot center surface normal is unsafe");
                Vector3 north = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(latitude + 0.001, longitude, result.TerrainHeight) - surface), up).normalized;
                Need(north.sqrMagnitude > 0.9f, "Local north frame is undefined near a pole");
                Quaternion frame = Quaternion.LookRotation(Quaternion.AngleAxis((float)headingDegrees, up) * north, up);
                int nx = Math.Max(2, (int)Math.Ceiling(template.MaxX - template.MinX)), nz = Math.Max(2, (int)Math.Ceiling(template.MaxZ - template.MinZ));
                Need((nx + 1) * (nz + 1) <= 4096, "Deployment terrain grid exceeds the 4,096 sample bound");
                double minimum = double.MaxValue, maximum = double.MinValue, slope = 0;
                var witness = new StringBuilder(); Add(witness, bodyName, template.Hash, template.CraftSha256, contextKey); Add(witness, latitude, longitude, headingDegrees, body.Radius, result.TerrainHeight);
                for (int x = 0; x <= nx; x++) for (int z = 0; z <= nz; z++)
                {
                    double px = template.MinX + (template.MaxX - template.MinX) * x / nx, pz = template.MinZ + (template.MaxZ - template.MinZ) * z / nz;
                    Vector3 origin = centerHit.point + frame * new Vector3((float)px, 0, (float)pz); RaycastHit hit;
                    Need(ColonyPlacementParallaxSurface.TerrainRay(body, origin + up * 20, -up, 40, out hit, out continuousTerrainReason), continuousTerrainReason);
                    double offset = Vector3.Dot(hit.point - centerHit.point, up); Vector3 normal = Quaternion.Inverse(frame) * hit.normal;
                    minimum = Math.Min(minimum, offset); maximum = Math.Max(maximum, offset); slope = Math.Max(slope, Vector3.Angle(hit.normal, (hit.point - (Vector3)body.position).normalized)); result.SampleCount++;
                    Add(witness, px, pz, Math.Round(offset, 3), Math.Round(normal.x, 6), Math.Round(normal.y, 6), Math.Round(normal.z, 6));
                }
                result.MaximumSlopeDegrees = slope; result.MaximumSupportGapMetres = maximum - minimum;
                var surfaceCollision = ColonyPlacementParallaxSurface.Check(body, latitude, longitude, result.TerrainHeight, headingDegrees, template.MinX, template.MaxX, template.MinZ, template.MaxZ, template.MaximumHeight, template.ClearanceMetres);
                result.SurfaceCollisionWitness = surfaceCollision.Witness;
                Add(witness, surfaceCollision.Witness); Need(surfaceCollision.Ready && surfaceCollision.Clear, surfaceCollision.Reason);
                result.TerrainWitness = ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(witness.ToString()));
                Need(slope <= template.MaximumSlopeDegrees, "Measured slope exceeds template limit: " + F(slope) + "°");
                Need(result.MaximumSupportGapMetres <= template.MaximumSupportGapMetres, "Measured terrain support gap exceeds template limit: " + F(result.MaximumSupportGapMetres) + " m");
                var proposal = ReservedPlot(template, latitude, longitude, headingDegrees); proposal.Id = excludePlotId ?? "";
                var inverse = Quaternion.Inverse(frame);
                var plotWitness = new StringBuilder(); var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var plot in registeredPlots.OrderBy(p => p == null ? "" : p.Id, StringComparer.Ordinal))
                {
                    Need(plot != null && ColonyPlacementRequest.Token(plot.Id, 128) && seen.Add(plot.Id) && ValidLocation(plot.Latitude, plot.Longitude, plot.Heading) &&
                        ColonyPlacementRequest.Range(plot.WidthMeters, 0.25, 1000) && ColonyPlacementRequest.Range(plot.LengthMeters, 0.25, 1000), "Registered plot authority contains an invalid or duplicate claim");
                    Add(plotWitness, plot.Id, plot.TemplateHash ?? "", plot.SurveyHash ?? "", plot.ReservedBy ?? "", plot.OccupiedBy ?? "");
                    Add(plotWitness, plot.Latitude, plot.Longitude, plot.Heading, plot.WidthMeters, plot.LengthMeters);
                    if (excludePlotId != null && plot.Id == excludePlotId) continue;
                    Vector3 local = inverse * (Vector3)(body.GetWorldSurfacePosition(plot.Latitude, plot.Longitude, result.TerrainHeight) - surface);
                    // Old/adopted claims have no certified reservation convention.
                    // Conservatively add both access margins instead of treating
                    // an undocumented rectangle as proven free access space.
                    double padding = plot.SurveyProvenance == ReservationProvenance ? 0 : template.ClearanceMetres;
                    Need(!RectanglesOverlap(local.x, local.z, plot.Heading - headingDegrees, proposal.WidthMeters / 2, proposal.LengthMeters / 2,
                        plot.WidthMeters / 2 + padding, plot.LengthMeters / 2 + padding), "Deployment/access reservation intersects registered plot " + plot.Id);
                }
                var half = new Vector3((float)((template.MaxX - template.MinX) / 2 + template.ClearanceMetres), (float)((maximum - minimum + template.MaximumHeight + 0.1) / 2), (float)((template.MaxZ - template.MinZ) / 2 + template.ClearanceMetres));
                var center = centerHit.point + frame * new Vector3((float)((template.MinX + template.MaxX) / 2), (float)((minimum + maximum + template.MaximumHeight) / 2), (float)((template.MinZ + template.MaxZ) / 2));
                var hits = new Collider[4097]; int hitCount = Physics.OverlapBoxNonAlloc(center, half, hits, frame, ~(1 << 15 | 1 << 21), QueryTriggerInteraction.Ignore);
                Need(hitCount < hits.Length, "Hardware clearance query exceeds its 4,096 collider bound");
                if (hitCount > 0)
                {
                    var part = hits[0] == null ? null : hits[0].GetComponentInParent<Part>();
                    throw new InvalidOperationException("Deployment/access envelope intersects existing hardware" + (part == null ? "" : ": " + part.partInfo.title));
                }
                proposal.EvidenceContext = contextKey; proposal.ObservedUt = Planetarium.GetUniversalTime();
                var terms = new StringBuilder(); Add(terms, "colony-site-survey-v1", bodyName, template.Id, template.Hash, template.CraftSha256, contextKey, result.TerrainWitness, plotWitness.ToString(), active.id.ToString("D"));
                Add(terms, latitude, longitude, headingDegrees, proposal.WidthMeters, proposal.LengthMeters, template.MinX, template.MaxX, template.MinZ, template.MaxZ, template.MaximumHeight,
                    template.ClearanceMetres, template.MaximumSlopeDegrees, template.MaximumSupportGapMetres, proposal.ObservedUt);
                proposal.SurveyHash = ColonyPlacementRequest.Hash(Encoding.UTF8.GetBytes(terms.ToString()));
                result.Plot = proposal; result.Clear = true;
                result.Reason = "Loaded footprint/terrain/hardware/registered-plot preview is clear; final real assembly, deployed geometry, contents, stable physics and anchor checks remain required";
            }
            catch (Exception ex) { result.Clear = false; result.Reason = ex.Message; }
            return result;
        }

        // A proposal only: empty SurveyHash deliberately prevents treating a
        // regular street sketch as surveyed terrain. Sparse lamps go beyond row
        // ends and outside the clear street corridor, not between touching homes.
        public static ColonySiteLayoutProposal SuggestStreet(ColonyTemplate housing, ColonyTemplate lamp, string bodyName, double latitude, double longitude,
            double headingDegrees, int buildingsPerSide = 3, double streetClearWidthMetres = 8)
        {
            var result = new ColonySiteLayoutProposal();
            try
            {
                ValidateTerms(housing, latitude, longitude, headingDegrees); if (lamp != null) ValidateTerms(lamp, latitude, longitude, headingDegrees);
                Need(buildingsPerSide >= 1 && buildingsPerSide <= 12 && ColonyPlacementRequest.Range(streetClearWidthMetres, 8, 50), "Street proposal exceeds its bounded layout limits");
                var body = FlightGlobals.Bodies.FirstOrDefault(b => b != null && b.bodyName == bodyName); Need(body != null && ColonyPlacementRequest.Range(body.Radius, 1000, 1000000000), "Celestial body radius unavailable");
                double halfX = Math.Max(Math.Abs(housing.MinX), Math.Abs(housing.MaxX)), halfZ = Math.Max(Math.Abs(housing.MinZ), Math.Abs(housing.MaxZ));
                double across = streetClearWidthMetres / 2 + halfX + housing.ClearanceMetres, spacing = 2 * (halfZ + housing.ClearanceMetres);
                Need(spacing * buildingsPerSide <= 900, "Street proposal exceeds loaded survey reach");
                result.StreetClearWidthMetres = streetClearWidthMetres; result.AlongRowSpacingMetres = spacing;
                foreach (int side in new[] { -1, 1 }) for (int i = 0; i < buildingsPerSide; i++)
                    result.Plots.Add(OffsetPlot(housing, body.Radius, latitude, longitude, headingDegrees, side * across, (i - (buildingsPerSide - 1) / 2d) * spacing));
                if (lamp != null)
                {
                    double lampX = streetClearWidthMetres / 2 + Math.Max(Math.Abs(lamp.MinX), Math.Abs(lamp.MaxX)) + lamp.ClearanceMetres;
                    double end = (buildingsPerSide - 1) * spacing / 2 + halfZ + housing.ClearanceMetres + Math.Max(Math.Abs(lamp.MinZ), Math.Abs(lamp.MaxZ)) + lamp.ClearanceMetres;
                    foreach (int side in new[] { -1, 1 }) foreach (int finish in new[] { -1, 1 }) result.Plots.Add(OffsetPlot(lamp, body.Radius, latitude, longitude, headingDegrees, side * lampX, finish * end));
                }
                result.Reason = "Unsurveyed street proposal; each body-coordinate plot needs a current loaded survey. Full deployment/access edges bound spacing; lamp envelopes remain outside the 8 m or wider street corridor.";
            }
            catch (Exception ex) { result.Plots.Clear(); result.Reason = ex.Message; }
            return result;
        }

        private static ColonyPlot OffsetPlot(ColonyTemplate template, double radius, double latitude, double longitude, double heading, double x, double z)
        {
            double h = heading * Math.PI / 180, north = z * Math.Cos(h) - x * Math.Sin(h), east = z * Math.Sin(h) + x * Math.Cos(h), distance = Math.Sqrt(north * north + east * east);
            double lat = latitude * Math.PI / 180, lon = longitude * Math.PI / 180, bearing = Math.Atan2(east, north), arc = distance / radius;
            double destinationLat = Math.Asin(Math.Sin(lat) * Math.Cos(arc) + Math.Cos(lat) * Math.Sin(arc) * Math.Cos(bearing));
            double destinationLon = lon + Math.Atan2(Math.Sin(bearing) * Math.Sin(arc) * Math.Cos(lat), Math.Cos(arc) - Math.Sin(lat) * Math.Sin(destinationLat));
            double degreesLat = destinationLat * 180 / Math.PI, degreesLon = (destinationLon * 180 / Math.PI + 540) % 360 - 180;
            Need(ValidLocation(degreesLat, degreesLon, heading), "Street proposal crosses unsupported polar bounds");
            var plot = ReservedPlot(template, degreesLat, degreesLon, heading); plot.SurveyProvenance = "Unsurveyed geometric proposal; no terrain or hardware clearance claimed"; return plot;
        }
        private static ColonyPlot ReservedPlot(ColonyTemplate template, double latitude, double longitude, double heading)
        { return new ColonyPlot { Latitude = latitude, Longitude = longitude, Heading = heading, WidthMeters = 2 * (Math.Max(Math.Abs(template.MinX), Math.Abs(template.MaxX)) + template.ClearanceMetres), LengthMeters = 2 * (Math.Max(Math.Abs(template.MinZ), Math.Abs(template.MaxZ)) + template.ClearanceMetres), TemplateId = template.Id, TemplateHash = template.Hash, SurveyProvenance = ReservationProvenance }; }
        private static void ValidateTerms(ColonyTemplate template, double latitude, double longitude, double heading)
        {
            Need(template != null && ColonyPlacementRequest.Sha(template.Hash) && ColonyPlacementRequest.Sha(template.CraftSha256) && ColonyPlacementRequest.Token(template.Id, 80), "Trusted template binding is missing");
            Need(ValidLocation(latitude, longitude, heading), "Site coordinates/heading are invalid or near unsupported poles");
            Need(ColonyPlacementRequest.Range(template.MinX, -50, 50) && ColonyPlacementRequest.Range(template.MaxX, -50, 50) && template.MinX < template.MaxX &&
                ColonyPlacementRequest.Range(template.MinZ, -50, 50) && ColonyPlacementRequest.Range(template.MaxZ, -50, 50) && template.MinZ < template.MaxZ &&
                ColonyPlacementRequest.Range(template.MaximumHeight, 0.01, 50) && ColonyPlacementRequest.Range(template.ClearanceMetres, 0.25, 10) &&
                ColonyPlacementRequest.Range(template.MaximumSlopeDegrees, 0, 15) && ColonyPlacementRequest.Range(template.MaximumSupportGapMetres, 0, 2), "Template full deployment envelope/safety limits are invalid");
        }
        private static bool ValidLocation(double latitude, double longitude, double heading)
        { return ColonyPlacementRequest.Range(latitude, -89.9, 89.9) && ColonyPlacementRequest.Range(longitude, -180, 180) && ColonyPlacementRequest.Range(heading, 0, 359.999999999); }
        private static double Height(CelestialBody body, double latitude, double longitude)
        { var radial = QuaternionD.AngleAxis(longitude, Vector3d.down) * QuaternionD.AngleAxis(latitude, Vector3d.forward) * Vector3d.right; double height = body.pqsController.GetSurfaceHeight(radial) - body.Radius; Need(ColonyPlacementRequest.Finite(height), "PQS height is not finite"); return height; }
        private static bool RectanglesOverlap(double x, double z, double degrees, double ax, double az, double bx, double bz)
        {
            double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r), ac = Math.Abs(c), ass = Math.Abs(s);
            const double epsilon = 0.001; // edge touching is allowed; each reservation already contains its access margin
            if (Math.Abs(x) >= ax + ac * bx + ass * bz - epsilon || Math.Abs(z) >= az + ass * bx + ac * bz - epsilon) return false;
            if (Math.Abs(x * c - z * s) >= bx + ac * ax + ass * az - epsilon || Math.Abs(x * s + z * c) >= bz + ass * ax + ac * az - epsilon) return false;
            return true;
        }
        private static string F(double number) { return number.ToString("F3", CultureInfo.InvariantCulture); }
        private static void Need(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        private static void Add(StringBuilder b, params string[] values) { foreach (var value in values) { string v = value ?? ""; b.Append(v.Length).Append(':').Append(v).Append('|'); } }
        private static void Add(StringBuilder b, params double[] values) { foreach (double value in values) b.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('|'); }
    }
}

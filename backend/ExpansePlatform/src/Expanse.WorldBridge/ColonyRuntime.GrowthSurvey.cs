using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        sealed class GrowthSurveyProgress { public string Key; public int Candidate; }
        string growthSurveyContext = "";
        readonly Dictionary<string,GrowthSurveyProgress> growthSurveys = new Dictionary<string,GrowthSurveyProgress>(StringComparer.Ordinal);
        float nextGrowthSurveyRealtime;

        // One real footprint per pump, with at least two seconds between them.
        // This produces plot evidence only. Spending still goes through the
        // charter, fresh quote, shared commitments and normal approval engine.
        bool PumpAutomaticGrowthSurvey(ColonyEnvironment env)
        {
            if (growthSurveyContext != ContextKey)
            { growthSurveyContext = ContextKey; growthSurveys.Clear(); nextGrowthSurveyRealtime = 0; }
            if (!HighLogic.LoadedSceneIsFlight || !FlightGlobals.ready || FlightDriver.Pause || Time.timeScale <= 0 || TimeWarp.CurrentRate != 1 ||
                Time.realtimeSinceStartup < nextGrowthSurveyRealtime || state.Effects.Any(e => e.State == "held" || e.State == "applying")) return false;
            var reference = FlightGlobals.ActiveVessel;
            if (reference == null || reference.mainBody == null || reference.rootPart == null ||
                reference.packed && !reference.HoldPhysics || ColonyPlacementReference.Stability(reference, .05) != null) return false;
            foreach (var colony in state.Colonies.Where(c => c.Charter.GrowthPolicy == "automatic" && c.Status == "operational" &&
                c.SupportCommissionedUt.HasValue && c.Site.Body == reference.mainBody.bodyName).OrderBy(c => c.Id, StringComparer.Ordinal))
            {
                if (state.Plans.Any(p => p.ColonyId == colony.Id && p.State != "complete" && p.State != "cancelled")) continue;
                var quote = ColonyEngine.QuoteGrowthPlan(state, colony.Id, env);
                // Survey only the otherwise justified and affordable proposal.
                // Any other missing capability, funding or support blocks it.
                if (quote.Blockers.Count != 1 || !quote.Blockers[0].StartsWith("Missing clear surveyed plots.", StringComparison.Ordinal)) continue;
                var missing = quote.Buildings.Where(b => b.PlotId.Length == 0).ToArray();
                if (missing.Length != 1) continue;
                var template = env.Templates.SingleOrDefault(t => t.Id == missing[0].TemplateId && t.Hash == missing[0].TemplateHash);
                if (template == null || !env.BodyRadiiMeters.TryGetValue(colony.Site.Body, out double bodyRadius)) continue;
                double cadence = Math.Max(60, Math.Min(30 * ColonyLimits.KerbinDay, env.Planning.CadenceSeconds));
                string key = colony.Id + "|" + template.Hash + "|" + colony.Plots.Count + "|" + Math.Floor(env.Ut / cadence).ToString("R", CultureInfo.InvariantCulture);
                if (!growthSurveys.TryGetValue(colony.Id,out GrowthSurveyProgress progress) || progress.Key != key)
                { progress = new GrowthSurveyProgress {Key=key}; growthSurveys[colony.Id]=progress; }
                if (progress.Candidate >= 32) continue;
                nextGrowthSurveyRealtime = Time.realtimeSinceStartup + 2;
                int candidate = progress.Candidate++;
                double width = Math.Max(template.WidthMeters, 2 * (Math.Max(Math.Abs(template.MinX), Math.Abs(template.MaxX)) + template.ClearanceMetres));
                double length = Math.Max(template.LengthMeters, 2 * (Math.Max(Math.Abs(template.MinZ), Math.Abs(template.MaxZ)) + template.ClearanceMetres));
                double east = (candidate % 2 == 0 ? -1 : 1) * (4 + width / 2), north = (candidate / 2 + 1) * (length + 2);
                double cosine = Math.Cos(colony.Site.Latitude * Math.PI / 180);
                if (Math.Abs(cosine) < .01) return false;
                double latitude = colony.Site.Latitude + north / bodyRadius * 180 / Math.PI;
                double longitude = (colony.Site.Longitude + east / (bodyRadius * cosine) * 180 / Math.PI + 540) % 360 - 180;
                if (latitude < -90 || latitude > 90 || ColonyEngine.SurfaceDistance(colony.Site, new ColonySite { Latitude = latitude, Longitude = longitude }, bodyRadius) > colony.Site.RadiusMeters) return false;
                // Surveying beyond actual physics reach cannot produce a usable
                // automatic plot while preserving the player's active vessel.
                var range = PhysicsGlobals.Instance?.VesselRangesDefault?.landed;
                double envelopeRadius = Math.Sqrt(width * width + length * length) / 2;
                var point = reference.mainBody.GetWorldSurfacePosition(latitude, longitude, reference.altitude);
                if (range == null || !Finite(range.unpack) || range.unpack <= envelopeRadius + 5 ||
                    (point - (Vector3d)reference.rootPart.transform.position).magnitude >= Math.Min(1000, range.unpack - envelopeRadius - 5)) return false;
                var plots = state.Colonies.Where(c => c.Site.Body == colony.Site.Body).SelectMany(c => c.Plots).ToList();
                var survey = ColonySiteSurvey.Survey(template, colony.Site.Body, latitude, longitude, 0, plots, ContextKey);
                if (!survey.Clear || survey.ContextKey != ContextKey) return false;
                string operation = ColonyEngine.PlanningChildId(colony.Id, "automatic-growth-survey:" + key + ":" + candidate);
                survey.Plot.Id = ColonyEngine.PlanningChildId(operation, missing[0].Id);
                env.Planning.SurveyedPlots.Clear(); env.Planning.SurveyedPlots.Add(survey.Plot); env.Planning.SurveyFailure = "";
                var result = ColonyEngine.Execute(state, new ColonyCommand { Kind = "surveyGrowthPlan", ColonyId = colony.Id,
                    OperationId = operation, ContextKey = ContextKey, ExpectedRevision = state.Revision }, env);
                env.Planning.SurveyedPlots.Clear();
                if (result.Outcome != "accepted") return false;
                Accept(result.State);
                return true;
            }
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Expanse.Domain.Colonies;
using UnityEngine;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        ColonyPlanningEnvironment planningPolicy;
        string planningCursor = "";

        // Immutable package-role configuration is cached; state/utility/roster
        // witnesses are rebuilt from the current selected world on every review.
        void PopulatePlanningPolicyEnvironment(ColonyEnvironment env)
        {
            if(planningPolicy==null && GameDatabase.Instance!=null)
            {
                var configs=GameDatabase.Instance.GetConfigNodes("EXPANSE_COLONY_PLANNING");
                var policy=new ColonyPlanningEnvironment();
                if(configs.Length==1)
                {
                    var config=configs[0];
                    policy.CadenceSeconds=Value(config,"cadenceSeconds");policy.ImportDelayFactor=Value(config,"importDelayFactor");
                    foreach(var role in config.GetNodes("ROLE")) policy.Roles.Add(new ColonyPlanningRole {Role=Required(role,"role"),TemplateId=Required(role,"templateId"),MinimumCount=checked((int)Whole(role,"minimumCount"))});
                    ColonyStateCodec.ValidatePlanningEnvironment(policy);
                }
                else policy.SurveyFailure="Exactly one explicit installed planning policy is required.";
                planningPolicy=policy;
            }
            if(planningPolicy==null)return;
            env.Planning=new ColonyPlanningEnvironment {Roles=planningPolicy.Roles.ToList(),CadenceSeconds=planningPolicy.CadenceSeconds,
                ImportDelayFactor=planningPolicy.ImportDelayFactor,SurveyFailure=planningPolicy.SurveyFailure};
            if(state==null || FlightGlobals.Vessels==null)return;
            foreach(var colony in state.Colonies) foreach(var facility in colony.Facilities.Where(f=>f.State!="retired"))
            {
                var vessel=FlightGlobals.Vessels.SingleOrDefault(v=>v!=null&&v.id.ToString("D")==facility.VesselId);
                if(vessel==null || !vessel.loaded || vessel.parts==null)continue;
                var members=vessel.parts.Where(p=>p!=null&&facility.PartIds.Contains(p.persistentId)).ToArray();
                bool membershipCurrent=facility.PartIds.Count>0&&members.Length==facility.PartIds.Count&&members.All(p=>p.partInfo!=null)&&members.Select(p=>p.persistentId).Distinct().Count()==facility.PartIds.Count;
                string identity=ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(facility.VesselId+"|"+string.Join(";",members.OrderBy(p=>p.persistentId)
                    .Select(p=>p.persistentId+":"+(p.partInfo==null?"unavailable":p.partInfo.name)+":"+string.Join(",",p.Modules.Cast<PartModule>().Where(m=>m!=null).Select(m=>m.moduleName).OrderBy(n=>n,StringComparer.Ordinal))))));
                var utility=env.Services.Utilities.SingleOrDefault(u=>u.FacilityId==facility.Id&&u.ContextKey==env.ContextKey&&Math.Abs(env.Ut-u.ObservedUt)<=10);
                bool usable=membershipCurrent&&utility!=null&&utility.InputsAccessible&&ColonyUtilityQualification.PowerSupported(utility)&&ColonyUtilityQualification.HeatSupported(utility)&&utility.BackgroundProviderQualified;
                var modules=members.SelectMany(p=>p.Modules.Cast<PartModule>()).Where(m=>m!=null).Select(m=>m.moduleName).ToArray();
                var roles=new List<string>();
                if(modules.Contains("ModuleAutoRepairer"))roles.Add("workshop");
                if(members.Any(p=>p.Resources.Cast<PartResource>().Any(r=>r.resourceName!="ElectricCharge"&&r.maxAmount>0)))roles.Add("storage");
                if(modules.Contains("ModuleLight")||modules.Contains("ModuleColoredLensLight"))roles.Add("lamp");
                if(utility!=null&&utility.ContinuousSourceQualified&&utility.NominalGenerationEcPerSecond.GetValueOrDefault()>0)roles.Add("power");
                // Actual package provenance can identify service roles after real
                // commissioning; it cannot manufacture quantities or qualify power.
                foreach(var configured in env.Planning.Roles.Where(r=>r.TemplateId==facility.TemplateId))if(!roles.Contains(configured.Role))roles.Add(configured.Role);
                string witness=ColonyStateCodec.Hash(Encoding.UTF8.GetBytes(env.ContextKey+"|"+facility.Id+"|"+facility.VesselId+"|"+
                    string.Join(",",vessel.parts.Where(p=>p!=null).Select(p=>p.persistentId).OrderBy(x=>x))+"|"+string.Join(",",modules.OrderBy(x=>x))+"|"+(utility==null?"":utility.Evidence)));
                foreach(string role in roles.Take(4)) env.Planning.Assets.Add(new ColonyPlanningAsset {ColonyId=colony.Id,FacilityId=facility.Id,Role=role,
                    Name=facility.Name,ContextKey=env.ContextKey,Witness=witness,IdentityHash=identity,Qualified=usable,Homes=role=="housing"?facility.CertifiedHomes:0});
            }
            ColonyStateCodec.ValidatePlanningEnvironment(env.Planning);
        }

        void PreparePlanningPolicyCommand(ColonyCommand command, ColonyEnvironment env)
        {
            if(command==null || command.Kind!="surveyFoundingPlan"&&command.Kind!="surveyGrowthPlan")return;
            try
            {
                if(command.ContextKey!=ContextKey||command.ExpectedRevision!=state.Revision)throw new InvalidDataException("Site survey belongs to an old save/revision.");
                var colony=state.Colonies.Single(c=>c.Id==command.ColonyId);
                var q=command.Kind=="surveyGrowthPlan"?ColonyEngine.QuoteGrowthPlan(state,colony.Id,env):ColonyEngine.QuoteFoundingPlan(state,colony.Id,env,command.FoundingIntent);
                var missing=q.Buildings.Where(b=>b.PlotId.Length==0).ToArray();
                if(missing.Length==0)throw new InvalidDataException("All proposed buildings already have clear surveyed plots.");
                var packages=missing.Select(b=>env.Templates.Single(t=>t.Id==b.TemplateId&&t.Hash==b.TemplateHash)).ToArray();
                if(!env.BodyRadiiMeters.TryGetValue(colony.Site.Body,out var radius))throw new InvalidDataException("Actual body radius is unavailable.");
                double width=packages.Max(t=>2*(Math.Max(Math.Abs(t.MinX),Math.Abs(t.MaxX))+t.ClearanceMetres));
                double length=packages.Max(t=>2*(Math.Max(Math.Abs(t.MinZ),Math.Abs(t.MaxZ))+t.ClearanceMetres));
                width=Math.Max(width,packages.Max(t=>t.WidthMeters));length=Math.Max(length,packages.Max(t=>t.LengthMeters));
                var registered=state.Colonies.Where(c=>c.Site.Body==colony.Site.Body).SelectMany(c=>c.Plots).ToList();
                for(int i=0;i<missing.Length;i++)
                {
                    // Two rows leave an eight metre street plus full deployment
                    // access margins. Additional candidates continue along the row;
                    // loaded terrain and existing hardware decide actual clearance.
                    double east=(i%2==0?-1:1)*(4+width/2), north=(i/2+1)*(length+2);
                    bool clear=false;string reason="";
                    for(int attempt=0;attempt<8&&!clear;attempt++)
                    {
                        double lat=colony.Site.Latitude+(north+attempt*(length+2))/radius*180/Math.PI;
                        double cos=Math.Cos(colony.Site.Latitude*Math.PI/180);
                        if(Math.Abs(cos)<.01)throw new InvalidDataException("Street survey cannot use the polar coordinate frame.");
                        double lon=colony.Site.Longitude+east/(radius*cos)*180/Math.PI;lon=(lon+540)%360-180;
                        if(lat < -90 || lat > 90)continue;
                        var location=new ColonySite {Latitude=lat,Longitude=lon};
                        if(ColonyEngine.SurfaceDistance(colony.Site,location,radius)>colony.Site.RadiusMeters)continue;
                        var survey=ColonySiteSurvey.Survey(packages[i],colony.Site.Body,lat,lon,0,registered,ContextKey);
                        reason=survey.Reason;if(!survey.Clear)continue;
                        survey.Plot.Id=ColonyEngine.PlanningChildId(command.OperationId,missing[i].Id);
                        env.Planning.SurveyedPlots.Add(survey.Plot);registered.Add(survey.Plot);clear=true;
                    }
                    if(!clear)throw new InvalidDataException("Street plot "+missing[i].Role+" could not be cleared: "+reason);
                }
                // Long surveys must obtain a fresh server UT for the <=10 second
                // validation window, while preserving the same loaded context.
                if(command.ContextKey!=ContextKey)throw new InvalidDataException("Save changed during the read-only survey.");
                env.Ut=Planetarium.GetUniversalTime();
            }
            catch(Exception ex) {env.Planning.SurveyedPlots.Clear();env.Planning.SurveyFailure=Bound(ex.Message,512);}
        }

        bool QueuePlanningPolicy(ColonyEnvironment env, ColonyPureTickWork operation)
        {
            if(!Ready||mutating||state==null||env==null||env.ContextKey!=ContextKey||env.WorldId!=state.WorldId||
                !ReferenceEquals(selectedGame,HighLogic.CurrentGame)||env.Ut!=state.SimulatedUt)return false;
            PumpAutomaticGrowthSurvey(env);
            // A survey may enter native code. Recheck the selected world before
            // the synchronous pure phase; its stages have no native callbacks.
            if(!Ready||mutating||state==null||env.ContextKey!=ContextKey||env.WorldId!=state.WorldId||
                !ReferenceEquals(selectedGame,HighLogic.CurrentGame)||env.Ut!=state.SimulatedUt)return false;
            operation.StartPlanning(state,selectedGame,state.WorldId,ContextKey,loadEpoch,acceptedBytes,env,planningCursor);
            pendingPureTick=operation;
            return true;
        }
    }
}

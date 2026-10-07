using Expanse.Domain.Colonies;
using System.Text;
using System.Text.Json;
namespace Expanse.Colony.Acceptance;

// Synthetic rule checks only. They do not manufacture a wire-valid native
// snapshot or prove placement, production, inventory transfer or habitation.
internal static class AcceptanceSelfChecks
{
    internal static void Run()
    {
        int checks=0;void Check(bool value,string reason){if(!value)throw new InvalidDataException(reason);checks++;}
        void Reject(Action action,string reason){try{action();}catch(InvalidDataException){checks++;return;}throw new InvalidDataException(reason);}
        string id=Guid.NewGuid().ToString("D"),planId=Guid.NewGuid().ToString("D");
        var state=ColonyEngine.Create(Guid.NewGuid().ToString("D"),100);state.Colonies.Add(new(){Id=id});
        var a=new ColonyManagementSnapshot{State=state,ContextKey="synthetic/context",ObservedUt=100};
        var b=new ColonyManagementSnapshot{State=ColonyEngine.Create(state.WorldId,100),ContextKey=a.ContextKey,ObservedUt=100};b.State.Colonies.Add(new(){Id=id});
        var recipe=new ColonyProductionRecipe{ConfigurationHash=new string('a',64)};
        b.Production.Recipes.Add(recipe);Check(ReferenceEquals(AcceptanceChecks.Environment(b).Production,b.Production),"Acceptance environment discarded typed production catalog.");
        Reject(()=>AcceptanceChecks.Production(a,b,id),"No investment must not pass production.");
        var plan=new ColonyPlan{Id=planId,ColonyId=id,Quote=new(){ProductionInvestments=[new(){Id="fixture-investment",Recipe=recipe}]}};
        var claim=new ColonyProductionClaim{Id="fixture-investment",State="operational",OutputContextKey=a.ContextKey,OutputObservedUt=100,OutputIntervalSeconds=.02,OutputSuppliesUnits=.00001,OutputPartId=100,OutputModuleId=1100};
        for(int index=0;index<5;index++){string child=Guid.NewGuid().ToString("D");claim.Steps.Add(new(){Id=child,Kind=index%2==0&&index<4?"hopperConnect":"converterStart",State="applied",BeforeWitness="synthetic before",AfterWitness="synthetic after",HopperId=index%2==0&&index<4?"synthetic hopper "+index:""});b.State.Effects.Add(new(){Id=child,Kind="productionNative",State="applied"});}
        plan.Production.Add(claim);claim.OutputWitness=ColonyEngine.ProductionOutputWitness(plan,claim);b.State.Plans.Add(plan);
        Reject(()=>AcceptanceChecks.Production(a,b,id),"Historical receipt alone must not pass current production.");
        var current=new ColonyProductionObservation{PlanId=plan.Id,InvestmentId=claim.Id,ContextKey=b.ContextKey,ObservedUt=100,Active=true,Qualified=true,PartId=100,ModuleId=1100,RecipeHash=recipe.ConfigurationHash};b.Production.Observations.Add(current);
        AcceptanceChecks.Production(a,b,id);checks++;
        current.Qualified=false;Reject(()=>AcceptanceChecks.Production(a,b,id),"Current unsafe observation must not inherit historical success.");current.Qualified=true;
        current.ObservedUt=89;Reject(()=>AcceptanceChecks.Production(a,b,id),"Stale native observation must not pass.");current.ObservedUt=100;
        claim.Steps[0].HopperId="";Reject(()=>AcceptanceChecks.Production(a,b,id),"Missing created native HopperId must not pass.");
        // Parser/DTO round trips use real validators; none of these synthetic
        // values are a ready provider, capability or native acceptance proof.
        var typedIntent=new ColonyFoundingIntent {NewArrivalCount=2,Startup=new(){SuppliesReorderEnabled=true,ServiceEnabled=true,LocalProcurementEnabled=true},Production=new(){Mode="localInvestment",RecipeId="cultivate-substrate-v1",Operations=new(){RegisterCreatedEndpoints=true,AutomaticIntake=true,AutomaticInputRefill=true,Resources=[
            new(){Resource="Fertilizer",InitialReserve=100*ColonyLimits.Units,ReorderEnabled=true,ReorderPoint=100*ColonyLimits.Units,TargetAmount=200*ColonyLimits.Units,NativeTargetFraction=.5},
            new(){Resource="Machinery",InitialReserve=100*ColonyLimits.Units,ReorderEnabled=true,ReorderPoint=100*ColonyLimits.Units,TargetAmount=100*ColonyLimits.Units,NativeTargetFraction=.95},
            new(){Resource="Plutonium-238",InitialReserve=ColonyLimits.Units,ReorderEnabled=true,ReorderPoint=ColonyLimits.Units,TargetAmount=2*ColonyLimits.Units,NativeTargetFraction=1}]}}};
        byte[] intentBytes=JsonSerializer.SerializeToUtf8Bytes(typedIntent);var parsed=AcceptanceInput.ParseIntent(intentBytes);var ops=parsed.Production!.Operations!;
        Check(ops.RegisterCreatedEndpoints&&ops.AutomaticIntake&&ops.AutomaticInputRefill&&ops.Resources.Count==3,"Typed operating consent or resources were dropped.");
        Check(ops.Resources.Single(r=>r.Resource=="Plutonium-238").TargetAmount==2*ColonyLimits.Units&&ops.Resources.Single(r=>r.Resource=="Machinery").NativeTargetFraction==.95,"Exact refill amounts/fractions were changed.");
        var command=new ColonyCommand{OperationId=Guid.NewGuid().ToString("D"),ContextKey=a.ContextKey,ColonyId=id,Kind="approveFoundingPlan",QuoteId=new('c',64),FoundingIntent=parsed};
        var request=ColonyManagementWire.DecodeRequest(ColonyManagementWire.Encode(new ColonyManagementWireRequest{RequestId=Guid.NewGuid().ToString("D"),Kind="submit",Command=command}));
        Check(ColonyStateCodec.CommandHash(command)==ColonyStateCodec.CommandHash(request.Command!),"Exact reviewed command lost nested operating terms.");
        void RejectInput(string input,string reason){try{AcceptanceInput.ParseIntent(Encoding.UTF8.GetBytes(input));}catch(InvalidDataException){checks++;return;}catch(JsonException){checks++;return;}throw new InvalidDataException(reason);}
        string text=Encoding.UTF8.GetString(intentBytes);
        RejectInput(text.Replace("\"AutomaticIntake\":true","\"AutomaticIntake\":true,\"AutomaticIntake\":false"),"Duplicate operating consent was accepted.");
        RejectInput(text.Replace("\"AutomaticIntake\":true","\"InventedProviderReady\":true,\"AutomaticIntake\":true"),"Unknown nested authority was accepted.");
        Reject(()=>AcceptanceInput.ParseIntent(new byte[AcceptanceInput.MaximumIntentBytes+1]),"Oversized intent was accepted.");
        var legacy=AcceptanceInput.ParseIntent(Encoding.UTF8.GetBytes("{}"));Check(legacy.Production==null&&legacy.Startup==null,"Older empty intent silently gained operating authority.");
        var wireSnapshot=new ColonyManagementSnapshot{State=ColonyEngine.Create(Guid.NewGuid().ToString("D"),100),ContextKey="synthetic unavailable provider",ObservedUt=100};
        wireSnapshot.State.Colonies.Add(new(){Id=id,Name="No native authority test",Site=new(){Body="Minmus"},FoundedUt=100,SupportAccountedUt=100});
        wireSnapshot.Production.Registry=new(){Ready=false,WorldId=Guid.NewGuid().ToString("D"),Revision=7,Witness=new('d',64),ColonyEndpoints=4,TotalMembers=20,Reason="Explicitly unavailable registry test"};
        wireSnapshot.Production.Observations.Add(new(){PlanId=Guid.NewGuid().ToString("D"),InvestmentId="unqualified-native-row",FacilityId=Guid.NewGuid().ToString("D"),ContextKey=wireSnapshot.ContextKey,ObservedUt=100,PartId=12,ModuleId=120,RecipeHash=new('e',64),Active=false,Qualified=false,Provider="synthetic unavailable broker",NativeStatus="held",NativeSampleSeconds=1,
            NativeDeliveredInputs=[new(){Resource="Water",UnitsPerSecond=.1}],NativeDeliveredOutputs=[new(){Resource="Supplies",UnitsPerSecond=.001}],PhysicalSampleSeconds=1,PhysicalNetOutputs=[new(){Resource="Supplies",UnitsPerSecond=-.002}]});
        wireSnapshot.Services.Targets.Add(new(){ColonyId=id,FacilityId=Guid.NewGuid().ToString("D"),PartId=14,PartName="Exact local fuel test",DepotId=Guid.NewGuid().ToString("D"),SourceResource="Plutonium-238",DestinationResource="Plutonium-238",Capacity=20*ColonyLimits.Units,Amount=2*ColonyLimits.Units,ObservedUt=100,ContextKey=wireSnapshot.ContextKey,Provider="synthetic unavailable selected BRP",WorkerWitness="No worker qualification",CanApply=false,QualifiedWorker=false,Current=false});
        var detached=AcceptanceInput.ParseSnapshot(ColonyManagementWire.Encode(new ColonyManagementWireResponse{RequestId=Guid.NewGuid().ToString("D"),Snapshot=wireSnapshot}));
        Check(!detached.Production.Registry.Ready&&detached.Production.Registry.Revision==7&&detached.Production.Registry.TotalMembers==20,"Snapshot registry fields were dropped or made ready.");
        var observation=detached.Production.Observations.Single();Check(!observation.Active&&!observation.Qualified&&observation.ModuleId==120&&observation.NativeDeliveredOutputs.Single().UnitsPerSecond==.001&&observation.PhysicalNetOutputs.Single().UnitsPerSecond==-.002,"Native module/output observations were lost or fabricated.");
        var service=detached.Services.Targets.Single();Check(service.SourceResource=="Plutonium-238"&&service.Capacity==20*ColonyLimits.Units&&!service.CanApply&&!service.Current&&!service.QualifiedWorker&&service.WorkerWitness=="No worker qualification","Service readback fields or holds were changed.");
        Check(detached.Capabilities.Count==0&&ReferenceEquals(AcceptanceChecks.Environment(detached).Production,detached.Production),"Offline environment fabricated capabilities or replaced observed production.");
        var unavailableQuote=ColonyEngine.QuoteFoundingPlan(detached.State!,id,AcceptanceChecks.Environment(detached),parsed);
        Check(!unavailableQuote.CanApprove&&unavailableQuote.Blockers.Count>0&&!detached.Production.Registry.Ready&&detached.Capabilities.Count==0,"Exact pure quote invented missing native catalog/registry authority.");
        Check(AcceptanceChecks.CompleteStartupGaps(new ColonyPlanningQuote()).Count>0,"Historical building-only approval passed complete startup coverage.");
        // Complete startup follows frozen names, not the domain's deliberately
        // cleared auto-selection flag. Mutations stay synthetic and offline.
        ColonyPlanningQuote CompletePopulationQuote()=>new(){ColonyId=id,TargetPopulation=4,
            FoundingIntent=new(){FillPopulationTarget=false,NewArrivalCount=2,ExistingResidentRosterIds=["visitor-a","visitor-b"],RecruitRosterIds=["arrival-a","arrival-b"],Production=typedIntent.Production},
            Residents=[new(){RosterId="visitor-a",Name="Visitor A",Kind="existing"},new(){RosterId="visitor-b",Name="Visitor B",Kind="existing"},new(){RosterId="arrival-a",Name="Arrival A",Kind="recruit"},new(){RosterId="arrival-b",Name="Arrival B",Kind="recruit"}],
            StartupPolicies=new(){SuppliesReorderEnabled=true,ServiceEnabled=true,LocalProcurementEnabled=true},
            ProductionInvestments=[new(){Wolf=new(){Id="synthetic shared sequence"},Inventory=new(){Endpoints=[new(),new()]}}]};
        var complete=CompletePopulationQuote();Check(AcceptanceChecks.CompleteStartupGaps(complete).Count==0,"Resolved full named population was rejected because auto-fill is false.");
        complete.FoundingIntent!.FillPopulationTarget=true;complete.Residents.RemoveAt(1);complete.FoundingIntent.ExistingResidentRosterIds.RemoveAt(1);
        Check(AcceptanceChecks.CompleteStartupGaps(complete).Any(g=>g.Contains("population target")),"True auto-fill flag bypassed an underfilled named population.");
        complete=CompletePopulationQuote();complete.TargetPopulation=3;
        Check(AcceptanceChecks.CompleteStartupGaps(complete).Any(g=>g.Contains("population target")),"Named residents exceeding the target were accepted.");
        complete=CompletePopulationQuote();complete.Residents[1].RosterId="visitor-a";complete.FoundingIntent!.ExistingResidentRosterIds[1]="visitor-a";
        Check(AcceptanceChecks.CompleteStartupGaps(complete).Any(g=>g.Contains("population target")),"Duplicate identities counted twice toward the target.");
        complete=CompletePopulationQuote();complete.FoundingIntent!.ExistingResidentRosterIds[1]="substituted-visitor";
        Check(AcceptanceChecks.CompleteStartupGaps(complete).Any(g=>g.Contains("roster identities")),"Intent/bill identity substitution passed complete scope.");
        complete=CompletePopulationQuote();complete.Residents[0].Name="";
        Check(AcceptanceChecks.CompleteStartupGaps(complete).Any(g=>g.Contains("roster identities")),"Unnamed resident passed complete startup.");
        complete=CompletePopulationQuote();complete.Residents.RemoveAt(1);complete.FoundingIntent!.ExistingResidentRosterIds.RemoveAt(1);
        var existingColony=new ColonyRecord{Id=id,Residents=[new(){RosterId="committed-person",Status="resident"}]};
        Check(AcceptanceChecks.CompleteStartupGaps(complete,existingColony).Count==0,"Already committed resident was discarded when evaluating the target.");
        existingColony.Residents[0].Status="missing";
        Check(AcceptanceChecks.CompleteStartupGaps(complete,existingColony).Any(g=>g.Contains("population target")),"Missing resident was counted toward the target.");
        existingColony.Residents[0].Status="resident";existingColony.Residents[0].RosterId="visitor-a";
        Check(AcceptanceChecks.CompleteStartupGaps(complete,existingColony).Any(g=>g.Contains("population target")),"Committed resident was counted again as a new assignment.");
        existingColony.Residents[0].RosterId="committed-person";existingColony.Id=Guid.NewGuid().ToString("D");
        Check(AcceptanceChecks.CompleteStartupGaps(complete,existingColony).Any(g=>g.Contains("population target")),"Another colony's committed residents filled the target.");
        claim.Steps[0].HopperId="synthetic native returned hopper";
        Reject(()=>AcceptanceChecks.ProductionOperations(a,b,id),"Active hardware without reviewed endpoint/intake/refill consent passed complete operations.");
        plan.Quote.FoundingIntent=typedIntent;
        Reject(()=>AcceptanceChecks.ProductionOperations(a,b,id),"Operating consent without real saved endpoint/stock/service receipts passed complete operations.");
        Console.WriteLine("PASS offline production acceptance rule checks="+checks+"; synthetic only, no native acceptance or command submission.");
    }
}

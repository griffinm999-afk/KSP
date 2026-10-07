using System.Text.Json;
using Expanse.Clock.Core;
using Expanse.WorldBridge;

namespace Expanse.Clock.Tests;

public sealed class ColonyProductionTelemetryTests
{
    static readonly object Broker=new(),Part=new();
    static ProductionTelemetryLedger Ledger(){var ledger=new ProductionTelemetryLedger();ledger.Reset("epoch1");return ledger;}
    [Fact] public void AcceptedBrokerReturnsSeparateConsumptionAndCreditedOutput()
    {
        var ledger=Ledger();var frame=ledger.Enter("epoch1",Broker,Part,"water",100,2,true);
        ledger.Record(Broker,Part,"Water",8,3,false);ledger.Record(Broker,Part,"Supplies",6,-2,true);
        Assert.True(ledger.Complete(frame,true,3));Assert.Equal(1.5,frame.Inputs["Water"]/frame.Seconds);Assert.Equal(1,frame.Outputs["Supplies"]/frame.Seconds);
    }
    [Theory][InlineData(false)][InlineData(true)] public void UnobservedNestedConverterNeverCreditsItsParent(bool observedChild)
    {
        var ledger=Ledger();var parent=ledger.Enter("epoch1",Broker,Part,"parent",100,1,true);
        var child=ledger.Enter("epoch1",Broker,Part,"child",100,1,observedChild);
        ledger.Record(Broker,Part,"Water",2,2,false);Assert.Equal(observedChild,ledger.Complete(child,true,1));
        ledger.Record(Broker,Part,"Water",1,1,false);Assert.True(ledger.Complete(parent,true,1));Assert.Equal(1,parent.Inputs["Water"]);
    }
    [Fact] public void FailedCallbackWithholdsReceiptAndRestoresParent()
    {
        var ledger=Ledger();var parent=ledger.Enter("epoch1",Broker,Part,"a",100,1,true);var failed=ledger.Enter("epoch1",Broker,Part,"b",100,1,true);
        Assert.False(ledger.Complete(failed,false,0));ledger.Record(Broker,Part,"Water",1,1,false);Assert.True(ledger.Complete(parent,true,1));Assert.Single(parent.Inputs);
    }
    [Theory][InlineData(4,-1,false)][InlineData(4,1,true)][InlineData(4,5,false)][InlineData(4,-5,true)]
    public void InvalidOrWrongSignedReturnsInvalidateOnlyTheSample(double requested,double returned,bool output)
    {
        var ledger=Ledger();var frame=ledger.Enter("epoch1",Broker,Part,"a",100,1,true);
        ledger.Record(Broker,Part,"Water",requested,returned,output);
        Assert.False(ledger.Complete(frame,true,1));
    }
    [Fact] public void WrongBrokerOrPartCannotCreditTheSelectedModule()
    {
        foreach(bool wrongPart in new[]{false,true}){var ledger=Ledger();var frame=ledger.Enter("epoch1",Broker,Part,"a",100,1,true);ledger.Record(wrongPart?Broker:new object(),wrongPart?new object():Part,"Water",4,2,false);Assert.False(ledger.Complete(frame,true,1));}
    }
    [Fact] public void ObservedInputOrStorageLimitZeroIsDifferentFromNoCallback()
    {
        var ledger=Ledger();var frame=ledger.Enter("epoch1",Broker,Part,"a",100,1,true);
        Assert.True(ledger.Complete(frame,true,0));Assert.True(ProductionTelemetryLedger.Fresh(frame,"epoch1","a",100,true));
        Assert.Empty(frame.Outputs);Assert.False(ProductionTelemetryLedger.Fresh(null!,"epoch1","a",100,true));
    }
    [Fact] public void RecipeSwapDifficultyChangeStopStalenessAndEpochInvalidateReceipt()
    {
        var ledger=Ledger();var frame=ledger.Enter("epoch1",Broker,Part,"machinery-on:bay0",100,1,true);ledger.Complete(frame,true,1);
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"epoch1","machinery-off:bay0",101,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"epoch1","machinery-on:bay1",101,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"epoch2",frame.RecipeKey,101,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"epoch1",frame.RecipeKey,111,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"epoch1",frame.RecipeKey,99,true));
        Assert.False(ProductionTelemetryLedger.Fresh(frame,"epoch1",frame.RecipeKey,101,false));
    }
    [Fact] public void EpochResetDoesNotRestoreOldParentDuringFinalizer()
    {
        var ledger=Ledger();var old=ledger.Enter("epoch1",Broker,Part,"old",100,1,true);ledger.Reset("epoch2");
        var next=ledger.Enter("epoch2",Broker,Part,"new",50,1,true);Assert.False(ledger.Complete(old,false,0));
        ledger.Record(Broker,Part,"Water",1,1,false);Assert.True(ledger.Complete(next,true,1));Assert.Single(next.Inputs);
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(21601)] public void InvalidIntervalsWithholdActual(double seconds)
    {var ledger=Ledger();var frame=ledger.Enter("epoch1",Broker,Part,"a",100,seconds,true);Assert.False(ledger.Complete(frame,true,0));}
    static ColonyProductionRateTelemetry Rate(string resource,double units)=>new(resource,units,"ALL_VESSEL",false);
    static ColonyProductionModuleTelemetry Module(uint partId=1,int index=0)=>new(partId,2,index,"USITools.USI_Converter","Duna Agriculture",0,1,"Cultivate(S)",new string('a',64),true,true,"loaded-broker",new([Rate("Water",.0026)],[Rate("Supplies",.00026)],[]),new(100,1.5,1,new([Rate("Water",.0039)],[Rate("Supplies",.00039)],[])),new(100,1,1.5,[Rate("Water",.003)],[Rate("Supplies",.0003)],1),null,"Running","Returned broker quantities.",null);
    static ColonyVessel Vessel(ColonyProductionTelemetry? production=null)=>new("vessel1","Agriculture","Minmus","Greater Flats",0,0,"loaded",1,[],[],Production:production);
    static ClockSample Sample(ColonyVessel vessel)=>new(1,"clockSample",1,Guid.NewGuid(),Guid.NewGuid(),@"C:\Kerbal Space Program","The Expanse","The Expanse",100,true,"FLIGHT",false,null,1,Colony:new("observed",null,100,[vessel]));
    [Fact] public void LegacyAndExtendedProtocolRoundTripWithoutInventingActual()
    {
        var legacy=ClockProtocol.DecodeClockSample(ClockProtocol.Encode(Sample(Vessel())).AsSpan(4).ToArray());Assert.Null(legacy.Colony!.Vessels[0].Production);
        var row=Module();var frame=ClockProtocol.DecodeClockSample(ClockProtocol.Encode(Sample(Vessel(new("partial","Native callbacks only.",100,[row])))).AsSpan(4).ToArray());
        var actual=frame.Colony!.Vessels[0].Production!.Modules[0];Assert.Equal(.003,actual.Achieved!.Inputs[0].UnitsPerSecond);Assert.Equal(.0039,actual.Prepared!.Rates.Inputs[0].UnitsPerSecond);Assert.Equal(.0026,actual.Configured.Inputs[0].UnitsPerSecond);
    }
    [Fact] public void BackgroundModelCannotMasqueradeAsMeasuredDelivery()
    {
        var row=Module() with {Basis="background-model",Prepared=null,Achieved=null,Background=new(100,"BOUNDARY",[Rate("Water",.001)],[Rate("Supplies",.0001)])};
        var vessel=Vessel(new("partial","BRP model.",100,[row])) with {ObservationBasis="snapshot"};ClockProtocol.Validate(Sample(vessel));
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(vessel with {Production=vessel.Production! with {Modules=[row with {Achieved=Module().Achieved}]}})));
    }
    [Fact] public void MalformedRateIsIsolatedAndClockContextSurvives()
    {
        var json=System.Text.Encoding.UTF8.GetString(ClockProtocol.Encode(Sample(Vessel(new("partial","test",100,[Module()])))).AsSpan(4)).Replace("\"unitsPerSecond\":0.003,","\"unitsPerSecond\":-1,");
        var decoded=ClockProtocol.DecodeClockSample(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal("unavailable",decoded.Colony!.Status);Assert.Equal(100,decoded.UtSeconds);
    }
    [Fact] public void DuplicateBayIdentityAndOversizedVectorsFailClosed()
    {
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(new("partial","test",100,[Module(),Module()])))));
        var row=Module() with {Configured=new(Enumerable.Range(0,17).Select(i=>Rate("R"+i,1)).ToArray(),[],[])};
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(new("partial","test",100,[row])))));
    }
    [Fact] public void DifferentBaysOnOnePartKeepSeparateIdentities()
    {ClockProtocol.Validate(Sample(Vessel(new("partial","test",100,[Module(),Module(1,1) with {ModuleId=3,BayIndex=1,SelectedLoadout=2}]))));}
    [Fact] public void HarvesterCoefficientIsSeparateFromAbundancePreparedAndCreditedRates()
    {
        var row=Module() with {ModuleType="USITools.USI_Harvester",Recipe="Gypsum",SelectedLoadout=1,Harvester=new("Gypsum",26.2,0,0),Configured=new([Rate("ElectricCharge",262)],[],[]),Prepared=new(100,2,1,new([Rate("ElectricCharge",524)],[Rate("Gypsum",.7)],[])),Achieved=new(100,1,.5,[Rate("ElectricCharge",131)],[Rate("Gypsum",.175)],2)};
        var decoded=ClockProtocol.DecodeClockSample(ClockProtocol.Encode(Sample(Vessel(new("partial","Supported native owners only.",100,[row])))).AsSpan(4).ToArray());
        var actual=decoded.Colony!.Vessels[0].Production!.Modules[0];Assert.Equal(26.2,actual.Harvester!.Efficiency);Assert.Empty(actual.Configured.Outputs);Assert.Equal(.7,actual.Prepared!.Rates.Outputs[0].UnitsPerSecond);Assert.Equal(.175,actual.Achieved!.Outputs[0].UnitsPerSecond);
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(new("partial","test",100,[row with {Configured=row.Configured with {Outputs=[Rate("Gypsum",26.2)]}}])))));
    }
    [Fact] public void SavedHarvesterSelectionDoesNotInventDeliveredZero()
    {
        var row=Module() with {ModuleType="USI_Harvester",Recipe="Substrate",Basis="proto-config",SelectedLoadout=3,Harvester=new("Substrate",26.2,0,0),Configured=new([Rate("ElectricCharge",262)],[],[]),Prepared=null,Achieved=null};
        var sample=Sample(Vessel(new("partial","Saved only.",100,[row])) with {ObservationBasis="snapshot"});ClockProtocol.Validate(sample);
        Assert.Null(sample.Colony!.Vessels[0].Production!.Modules[0].Achieved);
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(sample with {Colony=sample.Colony with {Vessels=[sample.Colony.Vessels[0] with {Production=new("partial","test",100,[row with {Harvester=row.Harvester with {Efficiency=double.NaN}}])}]}}));
    }
    [Fact] public void HeartbeatsRetainCaptureIdentityAndNewCallbacksAdvanceIt()
    {
        var ledger=Ledger();var first=ledger.Enter("epoch1",Broker,Part,"a",100,1,true);Assert.True(ledger.Complete(first,true,0));
        long capture=first.CaptureSequence;Assert.True(capture>0);Assert.True(ProductionTelemetryLedger.Fresh(first,"epoch1","a",101,true));Assert.Equal(capture,first.CaptureSequence);
        ledger.Reset("epoch1");var next=ledger.Enter("epoch1",Broker,Part,"a",100,1,true);Assert.True(ledger.Complete(next,true,0));Assert.True(next.CaptureSequence>capture);
        var missing=Module() with {Achieved=Module().Achieved! with {CaptureSequence=0}};Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(new("partial","test",100,[missing])))));
    }
    [Fact] public void InventoryCompletenessOnlyCoversSupportedLoadedModuleInventory()
    {
        var production=new ColonyProductionTelemetry("partial","Other owners outside scope.",100,[],"complete-supported");ClockProtocol.Validate(Sample(Vessel(production)));
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(production) with {ObservationBasis="snapshot"})));
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(production with {Modules=[Module() with {ModuleType="Unknown.Producer"}]}))));
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(production with {Status="truncated"}))));
    }
    [Fact] public void StaleOrImpossibleActualClaimsAreRejected()
    {
        var stale=Module() with {Prepared=Module().Prepared! with {SampleUt=80},Achieved=Module().Achieved! with {SampleUt=80}};
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(new("partial","test",100,[stale])))));
        var impossible=Module() with {Achieved=Module().Achieved! with {Outputs=[Rate("Supplies",1)]}};
        Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(new("partial","test",100,[impossible])))));
    }
    [Fact] public void TruncatedModulesAreExplicitAndOversizedViewRemainsBounded()
    {
        var production=new ColonyProductionTelemetry("truncated","Rows omitted; incomplete totals.",100,[]);ClockProtocol.Validate(Sample(Vessel(production)));
        var large=Enumerable.Range(0,32).Select(i=>Module((uint)i+1,i) with {PartName=new string('p',100),Reason=new string('r',360),Configured=new(Enumerable.Range(0,16).Select(n=>Rate(new string('w',79)+n%10,1)).ToArray(),Enumerable.Range(0,16).Select(n=>Rate(new string('s',79)+n%10,1)).ToArray(),[])}).ToArray();
        var sample=Sample(Vessel(new("partial","test",100,large)));
        var colony=sample.Colony! with {Vessels=Enumerable.Range(0,2).Select(_=>sample.Colony!.Vessels[0] with {VesselId=Guid.NewGuid().ToString()}).ToArray()};
        var view=new ClockView(1,"clockView","live",sample with {Colony=null},0,true,Colony:colony);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(view,ClockProtocol.JsonOptions).Length>ClockProtocol.MaxFrameBytes);
        byte[] bytes=ClockProtocol.EncodeView(view);Assert.InRange(bytes.Length,5,ClockProtocol.MaxFrameBytes+4);var decoded=JsonSerializer.Deserialize<ClockView>(bytes.AsSpan(4),ClockProtocol.JsonOptions)!;Assert.Equal("truncated",decoded.Colony!.Vessels[0].Production!.Status);Assert.Equal("Agriculture",decoded.Colony.Vessels[0].Name);
    }
    [Fact] public void RosterCensusAndProductionSurviveCombinedBoundedView()
    {
        var ids=Enumerable.Range(0,481).Select(_=>Guid.NewGuid().ToString()).ToArray();
        var modules=Enumerable.Range(0,32).Select(i=>Module((uint)i+1,i) with {PartName=new string('p',100),Reason=new string('r',360)}).ToArray();
        var vessels=Enumerable.Range(0,24).Select(i=>Vessel(new("partial","Synthetic combined fixture",100,modules)) with {
            VesselId=ids[i],Crew=1,CrewRoster=[new("Crew "+i,"Engineer")],CrewRosterComplete=true,PhysicalCrewCapacity=4}).ToArray();
        var sample=Sample(vessels[0]);
        var colony=sample.Colony! with {Vessels=vessels,VesselCensus=new("complete",ids,42)};
        ClockProtocol.Validate(sample with {Colony=colony});
        var encoded=ClockProtocol.EncodeView(new ClockView(1,"clockView","live",sample with {Colony=null},0,true,Colony:colony));
        Assert.InRange(encoded.Length,5,ClockProtocol.MaxFrameBytes+4);
        var decoded=JsonSerializer.Deserialize<ClockView>(encoded.AsSpan(4),ClockProtocol.JsonOptions)!;
        ClockProtocol.Validate(sample with {Colony=decoded.Colony});
        Assert.Equal("complete",decoded.Colony!.VesselCensus!.Status);
        Assert.Equal(42,decoded.Colony.VesselCensus.ObservationSequence);
        Assert.Equal(481,decoded.Colony.VesselCensus.VesselIds.Length);
        Assert.True(decoded.Colony.Vessels.Sum(v=>v.Production!.Modules.Length)>0);
        Assert.True(decoded.Colony.Vessels.Sum(v=>JsonSerializer.SerializeToUtf8Bytes(v.Production,ClockProtocol.JsonOptions).Length)>24000,
            "A valid bounded frame retains production beyond the former independent Site threshold");
        Assert.All(decoded.Colony.Vessels,v=>{Assert.Equal("truncated",v.Production!.Status);Assert.True(v.CrewRosterComplete);Assert.Single(v.CrewRoster!);Assert.Equal(4,v.PhysicalCrewCapacity);});
    }
    [Fact] public void CrowdedFrameRotatesFairlyAndPreservesWholeActualObservations()
    {
        var covered=new HashSet<string>();
        var tanks=Enumerable.Range(0,12).Select(i=>new ColonyTank("Tank-"+i+new string('x',24),1,10,false,false,true)).ToArray();
        var vessels=Enumerable.Range(0,18).Select(i=>Vessel(new("partial","Synthetic rates, not live output.",100,
            Enumerable.Range(0,16).Select(j=>Module((uint)(i*16+j+1),j)).ToArray(),"complete-supported")) with {
                VesselId=Guid.NewGuid().ToString(),Name=i==16?"Atlas Harvester 1":i==17?"Ag Module":"Facility "+i,Tanks=tanks,
                CrewRoster=[new("Crew "+i,"Engineer")],CrewRosterComplete=true,PhysicalCrewCapacity=4}).ToArray();
        for(int turn=0;turn<18;turn++)
        {
            var colony=new ColonySnapshot("observed",null,100,vessels,VesselCensus:new("complete",vessels.Select(v=>v.VesselId).ToArray(),turn+1));
            var view=new ClockView(1,"clockView","live",Sample(vessels[0]) with {Colony=null},0,true,Colony:colony);
            var bytes=ClockProtocol.EncodeView(view);
            Assert.InRange(bytes.Length,5,ClockProtocol.MaxFrameBytes+4);
            var decoded=JsonSerializer.Deserialize<ClockView>(bytes.AsSpan(4),ClockProtocol.JsonOptions)!;
            foreach(var vessel in decoded.Colony!.Vessels)
            {
                Assert.True(vessel.CrewRosterComplete);
                if(vessel.Production!.Modules.Length>0)covered.Add(vessel.VesselId);
                if(vessel.Production.Modules.Length<16){Assert.Equal("truncated",vessel.Production.Status);Assert.Equal("partial",vessel.Production.InventoryStatus);Assert.Equal(16-vessel.Production.Modules.Length,vessel.Production.BudgetOmittedModuleCount);Assert.Equal(turn+1,vessel.Production.BudgetSelectionSequence);}
                foreach(var row in vessel.Production.Modules){Assert.NotNull(row.Prepared);Assert.NotNull(row.Achieved);Assert.Equal(.0003,row.Achieved!.Outputs[0].UnitsPerSecond);Assert.Equal(.00039,row.Prepared!.Rates.Outputs[0].UnitsPerSecond);}
            }
        }
        Assert.Equal(18,covered.Count);
        Assert.All(vessels,v=>{Assert.Equal(16,v.Production!.Modules.Length);Assert.Equal("complete-supported",v.Production.InventoryStatus);});
    }
    [Fact] public void DeliveredZeroHasPriorityOverLargeConfigurationOnlyRows()
    {
        var nominal=Enumerable.Range(0,31).Select(i=>Module((uint)i+1,i) with {Activated=false,Basis="proto-config",Prepared=null,Achieved=null,
            Configured=new(Enumerable.Range(0,16).Select(n=>Rate("Input-"+n+new string('x',60),1)).ToArray(),
                Enumerable.Range(0,16).Select(n=>Rate("Output-"+n+new string('x',60),1)).ToArray(),[])}).ToArray();
        var zero=Module(999,100) with {Achieved=Module().Achieved! with {TimeFactor=0,Inputs=[Rate("Water",0)],Outputs=[Rate("Supplies",0)]}};
        var colony=Sample(Vessel(new("partial","Synthetic priority fixture.",100,nominal.Append(zero).ToArray()))).Colony!;
        var view=new ClockView(1,"clockView","live",Sample(colony.Vessels[0]) with {Colony=null},0,true,Colony:colony);
        var bytes=ClockProtocol.EncodeView(view);
        var decoded=JsonSerializer.Deserialize<ClockView>(bytes.AsSpan(4),ClockProtocol.JsonOptions)!;
        ClockProtocol.Validate(decoded);
        var delivered=Assert.Single(decoded.Colony!.Vessels[0].Production!.Modules.Where(m=>m.PartId==999));
        Assert.Equal(0,delivered.Achieved!.Outputs[0].UnitsPerSecond);
        Assert.NotNull(delivered.Prepared);
        Assert.All(decoded.Colony.Vessels[0].Production!.Modules.Where(m=>m.PartId!=999),m=>Assert.Null(m.Achieved));
    }
    [Fact] public void BudgetOmissionMetadataRejectsAmbiguousOrImpossibleInventories()
    {
        var original=new ColonyProductionTelemetry("truncated","Budget fixture",100,[Module()],"partial",1,5);
        ClockProtocol.Validate(Sample(Vessel(original)));
        foreach(var invalid in new[]{original with {BudgetOmittedModuleCount=-1},original with {BudgetOmittedModuleCount=32},
            original with {BudgetSelectionSequence=null},original with {BudgetSelectionSequence=-1},original with {BudgetSelectionSequence=9007199254740992L},
            original with {Status="partial"},original with {InventoryStatus="complete-supported"},original with {BudgetOmittedModuleCount=0}})
            Assert.Throws<InvalidDataException>(()=>ClockProtocol.Validate(Sample(Vessel(invalid))));
    }
    [Fact] public void HostBudgetAccumulatesPublisherOmissionsWithoutPoisoningCallbackIdentity()
    {
        var rows=Enumerable.Range(0,16).Select(i=>Module((uint)i+1,i) with {Configured=new(
            Enumerable.Range(0,16).Select(n=>Rate("In-"+n+new string('x',60),1)).ToArray(),
            Enumerable.Range(0,16).Select(n=>Rate("Out-"+n+new string('x',60),1)).ToArray(),[])}).ToArray();
        var production=new ColonyProductionTelemetry("truncated","Publisher budget fixture",100,rows,"partial",16,5);
        var baseColony=Sample(Vessel(production)).Colony!;
        var colony=baseColony with {Vessels=Enumerable.Range(0,4).Select(_=>baseColony.Vessels[0] with {VesselId=Guid.NewGuid().ToString()}).ToArray()};
        var view=new ClockView(1,"clockView","live",Sample(colony.Vessels[0]) with {Colony=null},0,true,Colony:colony);
        byte[] bytes=ClockProtocol.EncodeView(view);
        var decoded=JsonSerializer.Deserialize<ClockView>(bytes.AsSpan(4),ClockProtocol.JsonOptions)!;
        ClockProtocol.Validate(decoded);
        var kept=decoded.Colony!.Vessels[0].Production!;
        Assert.NotEmpty(kept.Modules);Assert.True(kept.Modules.Length<16);
        Assert.Equal(32-kept.Modules.Length,kept.BudgetOmittedModuleCount);
        Assert.All(kept.Modules,m=>{Assert.Equal(1,m.Achieved!.CaptureSequence);Assert.Equal(100,m.Achieved.SampleUt);});
        Assert.Equal(16,production.BudgetOmittedModuleCount);Assert.Equal(16,production.Modules.Length);
    }
    [Fact] public void UnavailableObservationIsDistinctFromBudgetOmission()
    {
        var production=new ColonyProductionTelemetry("unavailable","No current observation",100,[]);
        var encoded=ClockProtocol.Encode(Sample(Vessel(production)));
        var decoded=ClockProtocol.DecodeClockSample(encoded.AsSpan(4).ToArray()).Colony!.Vessels[0].Production!;
        Assert.Equal(0,decoded.BudgetOmittedModuleCount);Assert.Null(decoded.BudgetSelectionSequence);
    }
}

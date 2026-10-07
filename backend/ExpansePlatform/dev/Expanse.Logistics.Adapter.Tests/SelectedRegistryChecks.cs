using Expanse.Domain;
using Expanse.WorldBridge;
using BackgroundResourceProcessing;

// Production adapters and physical commit code are source linked. Registry and
// KSP objects are explicit doubles; these checks grant no native certificate.
internal static class SelectedRegistryChecks
{
    sealed class F
    {
        internal Vessel Vessel;internal PartResource Ore,Water;internal ProtoPartResourceSnapshot OreProto,WaterProto;
        internal BackgroundResourceProcessor Processor;internal DepotRegistryModule Registry;
    }
    static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    static F Fixture(bool loaded=true,bool packed=false,bool mirrors=true)
    {
        HighLogic.CurrentGame=new object();HighLogic.LoadedSceneIsGame=true;HighLogic.LoadedSceneIsFlight=true;
        var f=new F {Ore=new(){resourceName="Ore",amount=10,maxAmount=100},Water=new(){resourceName="Water",amount=20,maxAmount=100},Processor=new()};
        f.Vessel=new(){loaded=loaded,packed=packed,parts=[new(){persistentId=7,flightID=70,Resources=[f.Ore,f.Water]}],vesselModules=[f.Processor]};
        if(mirrors)
        {
            f.OreProto=new(){resourceName="Ore",amount=10,maxAmount=100};f.OreProto.UpdateConfigNodeAmounts();
            f.WaterProto=new(){resourceName="Water",amount=20,maxAmount=100};f.WaterProto.UpdateConfigNodeAmounts();
            f.Vessel.protoVessel.protoPartSnapshots=[new(){persistentId=7,flightID=70,resources=[f.OreProto,f.WaterProto]}];
            f.Processor.Inventories=[new(){FlightId=70,ResourceName="Ore",Amount=10,OriginalAmount=10,MaxAmount=100,Snapshot=f.OreProto},new(){FlightId=70,ResourceName="Water",Amount=20,OriginalAmount=20,MaxAmount=100,Snapshot=f.WaterProto}];
        }
        f.Registry=new(){Registrations=[new(){DepotId="colony",OwnerKind="colony",OwnerColonyId=Guid.NewGuid().ToString("D"),OwnerFacilityId=Guid.NewGuid().ToString("D"),ApprovedSpecHash=new('b',64),MembershipRevision=1,Anchor=7,MemberIds=[7]}]};
        DepotRegistryModule.Instance=f.Registry;FlightGlobals.Vessels=[f.Vessel];FlightGlobals.ActiveVessel=new();return f;
    }
    static PhysicalInventoryTransferPlan Prepare(bool two=false,bool debit=true)
    {
        var manifest=new List<ResourceAmount>{new(){ResourceName="Ore",AmountMicroUnits=1_000_000}};
        if(two)manifest.Add(new(){ResourceName="Water",AmountMicroUnits=1_000_000});
        Require(PhysicalInventoryTransfers.TryPrepare("independent-selected","colony",manifest.ToArray(),debit,out var plan,out var reason,7),reason);return plan;
    }
    sealed class Ledger
    {
        internal string State="before";internal int Credits,Refunds;
        internal PhysicalInventoryCommitBoundary Boundary()=>new(){IsCurrent=()=>true,CommitDurableHold=()=>State="held",CommitSuccess=()=>{State="complete";Credits++;},RestoreBeforeAfterConfirmedRollback=()=>{State="before";Refunds++;}};
    }
    internal static void Run(Action<string,Action> test)
    {
        test("colony selected endpoint transfers without legacy snapshot/mirror expansion",()=>
        {var f=Fixture();for(uint i=0;i<128;i++)f.Registry.Registrations.Add(new(){DepotId="other"+i,OwnerKind=i<8?"legacy":"colony",Anchor=100+i,MemberIds=[100+i],MembershipRevision=1});
         Require(f.Registry.CreateEffectRegistrySnapshot().Depots.Length==8,"legacy scope");int before=f.Registry.LegacySnapshotCalls;var l=new Ledger();var p=Prepare();var result=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(result.Outcome=="accepted"&&f.Ore.amount==9&&l.Credits==1&&f.Registry.LegacySnapshotCalls==before,"one scoped source and one ledger credit");});
        test("colony anchored packed endpoint stays packed and conserves one debit",()=>
        {var f=Fixture(packed:true);var l=new Ledger();Require(PhysicalInventoryTransfers.Commit(Prepare(),l.Boundary()).Outcome=="accepted"&&f.Vessel.packed&&f.Ore.amount==9&&l.Credits==1,"packed source");});
        test("colony unloaded BRP endpoint synchronizes exact physical source",()=>
        {var f=Fixture(loaded:false);var l=new Ledger();Require(PhysicalInventoryTransfers.Commit(Prepare(),l.Boundary()).Outcome=="accepted"&&f.OreProto.amount==9&&f.Processor.Inventories[0].Amount==9&&f.Processor.Inventories[0].OriginalAmount==9&&l.Credits==1,"remote conserved source");});
        test("remote selected PID duplicate in real loaded parts rejects stale proto authority",()=>
        {var f=Fixture(loaded:false);FlightGlobals.Vessels.Add(new(){loaded=true,parts=[new(){persistentId=7,flightID=700,Resources=[new(){resourceName="Ore",amount=80,maxAmount=100}]}]});
         Require(!PhysicalInventoryTransfers.TryPrepare("actual-loaded-duplicate","colony",[new(){ResourceName="Ore",AmountMicroUnits=1_000_000}],true,out _,out _)&&f.OreProto.amount==10,"real loaded PID counts even when proto has no such member");});
        test("remote global ownership does not count a loaded vessel stale proto twice",()=>
        {var f=Fixture(loaded:false);FlightGlobals.Vessels.Add(new(){loaded=true,parts=[new(){persistentId=8,flightID=800,Resources=[new(){resourceName="Ore",amount=80,maxAmount=100}]}],protoVessel=new(){protoPartSnapshots=[new(){persistentId=7,flightID=700}]}});var l=new Ledger();
         Require(PhysicalInventoryTransfers.Commit(Prepare(),l.Boundary()).Outcome=="accepted"&&f.OreProto.amount==9&&l.Credits==1,"one actual ownership source per vessel");});
        test("same selected fingerprint with changed registry revision refuses before hold",()=>
        {var f=Fixture();var p=Prepare();f.Registry.MutationRevision++;var l=new Ledger();Require(PhysicalInventoryTransfers.Commit(p,l.Boundary()).Outcome=="held"&&l.State=="before"&&f.Ore.amount==10,"transient registry fence");});
        test("same registry/world with changed selected game refuses before hold",()=>
        {var f=Fixture();var p=Prepare();HighLogic.CurrentGame=new object();var l=new Ledger();Require(PhysicalInventoryTransfers.Commit(p,l.Boundary()).Outcome=="held"&&l.State=="before"&&f.Ore.amount==10,"selected game identity");});
        test("changed owner authority with unchanged members cannot apply old plan",()=>
        {var f=Fixture();var p=Prepare();f.Registry.Registrations[0].OwnerFacilityId=Guid.NewGuid().ToString("D");var l=new Ledger();Require(PhysicalInventoryTransfers.Commit(p,l.Boundary()).Outcome=="held"&&f.Ore.amount==10&&l.Credits==0,"owner binds selected hash");});
        test("native unloaded catchup registry mutation invalidates observation",()=>
        {var f=Fixture(loaded:false);f.Processor.OnCatchup=()=>f.Registry.MutationRevision++;
         Require(!PhysicalInventoryTransfers.TryObserve("colony",out var observed,out _,out _,out _)&&observed==null,"no stale observation");
         Require(!PhysicalInventoryTransfers.TryPrepare("stale-catchup","colony",[new(){ResourceName="Ore",AmountMicroUnits=1_000_000}],true,out _,out _)&&f.OreProto.amount==10,"no stale preflight");});
        test("native catchup processor swap refuses selected observation and preparation",()=>
        {foreach(bool prepare in new[]{false,true}){var f=Fixture(loaded:false);var next=new BackgroundResourceProcessor {Inventories=f.Processor.Inventories};f.Processor.OnCatchup=()=>f.Vessel.vesselModules=[next];
          if(prepare)Require(!PhysicalInventoryTransfers.TryPrepare("provider-swapped","colony",[new(){ResourceName="Ore",AmountMicroUnits=1_000_000}],true,out var plan,out _)&&plan==null,"no plan from a detached catch-up provider");
          else Require(!PhysicalInventoryTransfers.TryObserve("colony",out var observed,out _,out _,out _)&&observed==null,"no quote from a detached provider");
          Require(f.OreProto.amount==10&&f.WaterProto.amount==20,"catch-up provider ownership switch creates no transfer");}});
        test("remote observation ConfigNode callback cannot retain old processor authority",()=>
        {var f=Fixture(loaded:false);var next=new BackgroundResourceProcessor {Inventories=f.Processor.Inventories};f.OreProto.resourceValues.OnRead=()=>f.Vessel.vesselModules=[next];
         Require(!PhysicalInventoryTransfers.TryObserve("colony",out var observed,out _,out _,out _)&&observed==null&&f.OreProto.amount==10,"exact latest native owner is checked after observation reads");});
        test("remote observation ConfigNode callback cannot replace captured proto authority",()=>
        {var f=Fixture(loaded:false);var parts=f.Vessel.protoVessel.protoPartSnapshots;f.OreProto.resourceValues.OnRead=()=>f.Vessel.protoVessel=new(){protoPartSnapshots=parts};
         Require(!PhysicalInventoryTransfers.TryObserve("colony",out var observed,out _,out _,out _)&&observed==null&&f.OreProto.amount==10,"same member identities do not substitute a different observed proto");});
        test("known loaded provider change refuses before durable ledger hold",()=>
        {var f=Fixture();var p=Prepare();f.Vessel.packed=true;var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="held"&&!p.Attempted&&p.Mutation.AttemptedCount==0&&f.Ore.amount==10&&l.State=="before"&&l.Credits==0&&l.Refunds==0,"provider stale before commit causes no ledger or physical attempt");});
        test("loaded first-row registry loss holds partial and prevents second row",()=>
        {var f=Fixture();var p=Prepare(two:true);f.OreProto.OnUpdate=()=>f.Registry.MutationRevision++;var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Ore.amount==9&&f.OreProto.amount==9&&f.Water.amount==20&&f.WaterProto.amount==20&&l.State=="held"&&l.Credits==0&&l.Refunds==0&&p.Mutation.AttemptedCount==1,"exact one-row partial preserved");});
        test("remote first-row world switch holds partial and prevents second row",()=>
        {var f=Fixture(loaded:false);var p=Prepare(two:true);f.OreProto.OnUpdate=()=>HighLogic.CurrentGame=new object();var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.OreProto.amount==9&&f.WaterProto.amount==20&&l.State=="held"&&l.Credits==0&&l.Refunds==0&&p.Mutation.AttemptedCount==1,"remote one-row partial preserved");});
        test("loaded first-row packed transition prevents second mutation and rollback",()=>
        {var f=Fixture();var p=Prepare(two:true);f.OreProto.OnUpdate=()=>f.Vessel.packed=true;var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Ore.amount==9&&f.Water.amount==20&&p.Mutation.AttemptedCount==1&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"same-save packed transition stops at first native write");});
        test("loaded first-row part removal prevents stale second mutation and rollback",()=>
        {var f=Fixture();var p=Prepare(two:true);f.OreProto.OnUpdate=()=>f.Vessel.parts.Clear();var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Ore.amount==9&&f.OreProto.amount==9&&f.Water.amount==20&&p.Mutation.AttemptedCount==1&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"removed selected physical owner cannot be written again");});
        test("loaded proto replacement with identical members prevents stale writes",()=>
        {var f=Fixture();var p=Prepare(two:true);var parts=f.Vessel.protoVessel.protoPartSnapshots;f.OreProto.OnUpdate=()=>f.Vessel.protoVessel=new(){protoPartSnapshots=parts};var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Ore.amount==9&&f.Water.amount==20&&p.Mutation.AttemptedCount==1&&l.State=="held"&&l.Refunds==0&&l.Credits==0,"equal member IDs do not authorize a different proto object");});
        test("remote proto replacement with identical members prevents stale writes",()=>
        {var f=Fixture(loaded:false);var p=Prepare(two:true);var parts=f.Vessel.protoVessel.protoPartSnapshots;f.OreProto.OnUpdate=()=>f.Vessel.protoVessel=new(){protoPartSnapshots=parts};var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.OreProto.amount==9&&f.WaterProto.amount==20&&p.Mutation.AttemptedCount==1&&l.State=="held"&&l.Refunds==0&&l.Credits==0,"same-save proto instance switch holds exact one-row partial");});
        test("remote processor replacement cannot mutate old second row or new provider",()=>
        {var f=Fixture(loaded:false);var p=Prepare(two:true);var next=new BackgroundResourceProcessor {Inventories=[new(){FlightId=70,ResourceName="Ore",Amount=80,OriginalAmount=80,MaxAmount=100,Snapshot=f.OreProto},new(){FlightId=70,ResourceName="Water",Amount=90,OriginalAmount=90,MaxAmount=100,Snapshot=f.WaterProto}]};f.OreProto.OnUpdate=()=>f.Vessel.vesselModules=[next];var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Processor.Inventories[0].Amount==9&&f.Processor.Inventories[1].Amount==20&&f.WaterProto.amount==20&&next.Inventories[0].Amount==80&&next.Inventories[1].Amount==90&&p.Mutation.AttemptedCount==1&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"old/new native provider ownership preserved");});
        test("remote inventory owner replacement prevents old detached row writes",()=>
        {var f=Fixture(loaded:false);var p=Prepare(two:true);var original=f.Processor.Inventories;var next=new List<ResourceInventory>{new(){FlightId=70,ResourceName="Ore",Amount=9,OriginalAmount=9,MaxAmount=100,Snapshot=f.OreProto},new(){FlightId=70,ResourceName="Water",Amount=20,OriginalAmount=20,MaxAmount=100,Snapshot=f.WaterProto}};f.OreProto.OnUpdate=()=>f.Processor.Inventories=next;var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&original[0].Amount==9&&original[1].Amount==20&&next[0].Amount==9&&next[1].Amount==20&&f.WaterProto.amount==20&&p.Mutation.AttemptedCount==1&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"same processor cannot replace selected owned inventory silently");});
        test("remote flight mapping replacement with same inventory refs stops after first row",()=>
        {var f=Fixture(loaded:false);var p=Prepare(two:true);f.OreProto.OnUpdate=()=>{f.Vessel.protoVessel.protoPartSnapshots[0].flightID=71;foreach(var i in f.Processor.Inventories)i.FlightId=71;};var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.OreProto.amount==9&&f.WaterProto.amount==20&&p.Mutation.AttemptedCount==1&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"persistent identity alone cannot replace the retained native flight mapping");});
        test("loaded flight mapping replacement with same mirror refs stops after first row",()=>
        {var f=Fixture();var p=Prepare(two:true);f.OreProto.OnUpdate=()=>{f.Vessel.parts[0].flightID=71;foreach(var i in f.Processor.Inventories)i.FlightId=71;};var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Ore.amount==9&&f.Water.amount==20&&p.Mutation.AttemptedCount==1&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"loaded native flight mapping is bound to the prepared provider context");});
        test("loaded baseline read callback loses authority before first mutation",()=>
        {var f=Fixture();var p=Prepare();f.OreProto.resourceValues.OnRead=()=>HighLogic.CurrentGame=new object();var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Ore.amount==10&&f.OreProto.amount==10&&p.Mutation.AttemptedCount==0&&l.State=="held"&&l.Credits==0,"post-read authority rechecked before write");});
        test("remote baseline read callback loses authority before first mutation",()=>
        {var f=Fixture(loaded:false);var p=Prepare();f.OreProto.resourceValues.OnRead=()=>HighLogic.CurrentGame=new object();var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.OreProto.amount==10&&p.Mutation.AttemptedCount==0&&l.State=="held"&&l.Credits==0,"remote post-read authority rechecked before write");});
        test("loaded baseline read topology change prevents first mutation",()=>
        {var f=Fixture();var p=Prepare();f.OreProto.resourceValues.OnRead=()=>f.Vessel.parts.Clear();var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Ore.amount==10&&f.OreProto.amount==10&&p.Mutation.AttemptedCount==0&&l.State=="held"&&l.Credits==0,"provider context is rechecked after baseline callback");});
        test("loaded rollback world switch cannot restore another row or refund ledger",()=>
        {var f=Fixture();var p=Prepare(two:true);f.Processor.OnDirty=()=>throw new Exception("post-write failure");f.WaterProto.resourceValues.OnWrite=()=>{if(f.WaterProto.amount==20)HighLogic.CurrentGame=new object();};var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Water.amount==20&&f.Ore.amount==9&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"reverse first-row restoration held; second restoration prevented");});
        test("remote rollback world switch cannot restore another row or refund ledger",()=>
        {var f=Fixture(loaded:false);var p=Prepare(two:true);f.Processor.OnDirty=()=>throw new Exception("post-write failure");f.WaterProto.OnUpdate=()=>{if(f.WaterProto.amount==20)HighLogic.CurrentGame=new object();};var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.WaterProto.amount==20&&f.OreProto.amount==9&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"remote rollback stops at context change");});
        test("loaded rollback packed transition stops remaining ConfigNode and row writes",()=>
        {var f=Fixture();var p=Prepare(two:true);f.Processor.OnDirty=()=>throw new Exception("post-write failure");int configWrites=0;f.WaterProto.resourceValues.OnWrite=()=>{if(f.WaterProto.amount==20){configWrites++;f.Vessel.packed=true;}};var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.Water.amount==20&&f.Ore.amount==9&&configWrites==1&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"no second ConfigNode call or row restoration after topology switch");});
        test("remote rollback processor switch prevents ConfigNode retries and other rows",()=>
        {var f=Fixture(loaded:false);var p=Prepare(two:true);f.Processor.OnDirty=()=>throw new Exception("post-write failure");var next=new BackgroundResourceProcessor();int restoredWrites=0;f.WaterProto.resourceValues.OnWrite=()=>{if(f.WaterProto.amount==20)restoredWrites++;};f.WaterProto.OnUpdate=()=>{if(f.WaterProto.amount==20)f.Vessel.vesselModules=[next];};var l=new Ledger();var r=PhysicalInventoryTransfers.Commit(p,l.Boundary());
         Require(r.Outcome=="uncertain"&&f.WaterProto.amount==20&&f.OreProto.amount==9&&restoredWrites==2&&!next.Dirty&&l.State=="held"&&l.Credits==0&&l.Refunds==0,"native restore callback changes owner; explicit ConfigNode retry and second row held");});
        test("selected registration absent or ambiguous never reaches physical preflight",()=>
        {var f=Fixture();f.Registry.Registrations.Add(f.Registry.Registrations[0]);Require(!PhysicalInventoryTransfers.TryPrepare("duplicate","colony",[new(){ResourceName="Ore",AmountMicroUnits=1_000_000}],true,out _,out _)&&f.Ore.amount==10,"duplicate authority refused");});
        test("authority callback exception fails closed before provider write",()=>
        {var f=Fixture();var g=new LoadedBrpInventoryGateway();var endpoint=new InventoryEndpoint {DepotId="colony",MembershipRevision=1,AnchorPersistentId=7,MemberPersistentIds=[7],MemberSetHash=OperationIdentity.ComputeMemberSetHash(7,[7])};
         var p=g.Preflight(g.Resolve(endpoint),[new(){MemberPersistentId=7,ResourceName="Ore",DeltaMicroUnits=-1_000_000}]).Plan;p.AuthorityIsCurrent=()=>throw new Exception("authority unavailable");Require(!g.Apply(p).Succeeded&&f.Ore.amount==10,"exception cannot grant authority");});
        test("local fuel credit synchronizes locked installed tank across three contexts",()=>
        {foreach(int mode in new[]{0,1,2}){var f=Fixture(loaded:mode!=2,packed:mode==1);var i=f.Processor.Inventories[0];
          f.Ore.resourceName=f.OreProto.resourceName=i.ResourceName="Plutonium-238";f.Ore.amount=f.OreProto.amount=i.Amount=i.OriginalAmount=2;f.Ore.maxAmount=f.OreProto.maxAmount=i.MaxAmount=20;f.Ore.flowState=f.OreProto.flowState=false;f.OreProto.UpdateConfigNodeAmounts();
          Require(PhysicalInventoryTransfers.TryPrepare("paid-local-fuel","colony",[new(){ResourceName="Plutonium-238",AmountMicroUnits=1_000_000}],false,out var p,out var why,7),why);
          var l=new Ledger();Require(PhysicalInventoryTransfers.Commit(p,l.Boundary()).Outcome=="accepted"&&f.OreProto.amount==3&&i.Amount==3&&i.OriginalAmount==3&&f.Ore.flowState==false&&f.OreProto.flowState==false&&f.WaterProto.amount==20&&l.Credits==1,"exact local fuel credit; no flow/visitor change");}});
    }
}

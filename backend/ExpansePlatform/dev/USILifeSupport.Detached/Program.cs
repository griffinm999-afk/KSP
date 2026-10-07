using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using HarmonyLib;
using Expanse.WorldBridge;

// Detached synthetic test: executes an IL copy of the installed native ProcessRecipe.
// Never attaches to KSP. No Unity objects are constructed or lifecycle methods run.
// UI, cheat-option reads, and output notifications are replaced in this copy only.
public static class Program
{
    delegate ConverterResults NativeProcess(ResourceConverter converter,double seconds,ConversionRecipe recipe,Part part,PartModule module,float efficiency);
    static readonly ProductionTelemetryLedger Ledger=new();
    static readonly object PartIdentity=new();
    static double EventRequested;
    public static bool NotEqual(UnityEngine.Object a,UnityEngine.Object b)=>!ReferenceEquals(a,b);
    public static void OutputEvent(PartModule module,string resource,double amount)=>EventRequested+=amount;
    public static PartResourceDefinition DetachedDefinition(PartResourceLibrary library,string resource)=>null;
    static NativeProcess Build(out int requestCalls,out int storeCalls,out int detachedReplacements)
    {
        var native=typeof(ResourceConverter).GetMethod("ProcessRecipe");
        if(native.GetMethodBody().ExceptionHandlingClauses.Count!=0)throw new Exception("Review changed exception body.");
        var dm=new DynamicMethod("DetachedInstalledProcessRecipe",typeof(ConverterResults),new[]{typeof(ResourceConverter),typeof(double),typeof(ConversionRecipe),typeof(Part),typeof(PartModule),typeof(float)},typeof(Program).Module,true);
        var il=dm.GetILGenerator();
        foreach(var local in native.GetMethodBody().LocalVariables)il.DeclareLocal(local.LocalType,local.IsPinned);
        var rows=Read(native,il);
        requestCalls=storeCalls=detachedReplacements=0;
        foreach(var row in rows)
        {
            if(row.operand is MethodInfo m && m.DeclaringType==typeof(IResourceBroker))
            {
                if(m.Name=="RequestResource")requestCalls++;
                if(m.Name=="StoreResource")storeCalls++;
            }
            if(row.operand is MethodInfo ui && ui.DeclaringType==typeof(UIPartActionController)&&ui.Name=="get_Instance")
            {row.opcode=OpCodes.Ldnull;row.operand=null;detachedReplacements++;}
            else if(row.operand is MethodInfo op && op.DeclaringType==typeof(UnityEngine.Object)&&op.Name=="op_Inequality")
            {row.opcode=OpCodes.Call;row.operand=typeof(Program).GetMethod(nameof(NotEqual));detachedReplacements++;}
            else if(row.operand is MethodInfo library && library.DeclaringType==typeof(PartResourceLibrary)&&library.Name=="get_Instance")
            {row.opcode=OpCodes.Ldnull;row.operand=null;detachedReplacements++;}
            else if(row.operand is MethodInfo definition && definition.DeclaringType==typeof(PartResourceLibrary)&&definition.Name=="GetDefinition"&&definition.GetParameters()[0].ParameterType==typeof(string))
            {row.opcode=OpCodes.Call;row.operand=typeof(Program).GetMethod(nameof(DetachedDefinition));detachedReplacements++;}
            else if(row.operand is FieldInfo cheat && cheat.DeclaringType==typeof(CheatOptions))
            {row.opcode=OpCodes.Ldc_I4_0;row.operand=null;detachedReplacements++;}
            else if(row.operand is FieldInfo ev && ev.DeclaringType==typeof(GameEvents)&&ev.Name=="OnResourceConverterOutput")
            {row.opcode=OpCodes.Nop;row.operand=null;detachedReplacements++;}
            else if(row.operand is MethodInfo fire && fire.Name=="Fire"&&fire.DeclaringType.IsGenericType&&fire.DeclaringType.GetGenericTypeDefinition()==typeof(EventData<,,>))
            {row.opcode=OpCodes.Call;row.operand=typeof(Program).GetMethod(nameof(OutputEvent));detachedReplacements++;}
            foreach(var label in row.labels)il.MarkLabel(label);
            if(row.blocks.Count!=0)throw new Exception("Unexpected EH block");
            Emit(il,row);
        }
        if(requestCalls!=1||storeCalls!=1)throw new Exception("Native broker shape changed");
        return (NativeProcess)dm.CreateDelegate(typeof(NativeProcess));
    }
    static List<CodeInstruction> Read(MethodInfo method,ILGenerator il)
    {
        var codes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)).ToDictionary(c=>c.Value);
        var bytes=method.GetMethodBody().GetILAsByteArray();int pos=0;
        var rows=new List<(int offset,CodeInstruction instruction)>();var targets=new Dictionary<int,Label>();
        Label Target(int offset){if(!targets.TryGetValue(offset,out var label))targets[offset]=label=il.DefineLabel();return label;}
        while(pos<bytes.Length)
        {
            int offset=pos;short value=bytes[pos++];if(value==0xfe)value=(short)(0xfe00|bytes[pos++]);var op=codes[value];object operand=null;
            switch(op.OperandType)
            {
                case OperandType.InlineNone:break;
                case OperandType.InlineMethod:case OperandType.InlineField:case OperandType.InlineType:case OperandType.InlineTok:
                    operand=method.Module.ResolveMember(BitConverter.ToInt32(bytes,pos));pos+=4;break;
                case OperandType.InlineString:operand=method.Module.ResolveString(BitConverter.ToInt32(bytes,pos));pos+=4;break;
                case OperandType.InlineBrTarget:operand=Target(pos+4+BitConverter.ToInt32(bytes,pos));pos+=4;break;
                case OperandType.ShortInlineBrTarget:operand=Target(pos+1+(sbyte)bytes[pos]);pos++;break;
                case OperandType.InlineI:operand=BitConverter.ToInt32(bytes,pos);pos+=4;break;
                case OperandType.InlineI8:operand=BitConverter.ToInt64(bytes,pos);pos+=8;break;
                case OperandType.ShortInlineI:operand=(sbyte)bytes[pos++];break;
                case OperandType.InlineR:operand=BitConverter.ToDouble(bytes,pos);pos+=8;break;
                case OperandType.ShortInlineR:operand=BitConverter.ToSingle(bytes,pos);pos+=4;break;
                case OperandType.InlineVar:operand=(short)BitConverter.ToUInt16(bytes,pos);pos+=2;break;
                case OperandType.ShortInlineVar:operand=bytes[pos++];break;
                default:throw new Exception("Unsupported IL operand: "+op.OperandType);
            }
            rows.Add((offset,new CodeInstruction(op,operand)));
        }
        foreach(var row in rows)if(targets.TryGetValue(row.offset,out var label))row.instruction.labels.Add(label);
        return rows.Select(r=>r.instruction).ToList();
    }
    static void Emit(ILGenerator il,CodeInstruction c)
    {
        switch(c.operand)
        {
            case null:il.Emit(c.opcode);break;
            case LocalBuilder x:il.Emit(c.opcode,x);break;
            case Label x:il.Emit(c.opcode,x);break;
            case Label[] x:il.Emit(c.opcode,x);break;
            case MethodInfo x:il.Emit(c.opcode,x);break;
            case ConstructorInfo x:il.Emit(c.opcode,x);break;
            case FieldInfo x:il.Emit(c.opcode,x);break;
            case Type x:il.Emit(c.opcode,x);break;
            case string x:il.Emit(c.opcode,x);break;
            case double x:il.Emit(c.opcode,x);break;
            case float x:il.Emit(c.opcode,x);break;
            case long x:il.Emit(c.opcode,x);break;
            case int x:il.Emit(c.opcode,x);break;
            case byte x:il.Emit(c.opcode,x);break;
            case sbyte x:il.Emit(c.opcode,x);break;
            case short x:il.Emit(c.opcode,x);break;
            default:throw new Exception("Unsupported operand "+c.operand.GetType());
        }
    }
    public static void Main()
    {
        var process=Build(out int requests,out int stores,out int replacements);
        var results=new List<object>();
        foreach(var resource in new[]{"Dirt","Gypsum","MetallicOre","Substrate","Supplies"})
        foreach(var accepted in new[]{0.25,0.0})
        {
            var recipe=new ConversionRecipe();
            recipe.SetInputs(new List<ResourceRatio>{new("ElectricCharge",2,false)});
            recipe.SetOutputs(new List<ResourceRatio>{new(resource,3,true)});
            recipe.SetRequirements(new List<ResourceRatio>());recipe.TakeAmount=1;recipe.FillAmount=1;
            var broker=new SyntheticBroker(accepted);
            var converter=new ResourceConverter(broker);
            var frame=Ledger.Enter("detached",broker,PartIdentity,resource,100,2,true);
            EventRequested=0;
            var native=process(converter,2,recipe,null,null,1);
            if(!Ledger.Complete(frame,true,native.TimeFactor))throw new Exception("Ledger failed");
            var actual=frame.Outputs[resource];
            if(actual!=6*accepted||frame.Inputs["ElectricCharge"]!=4*accepted||EventRequested!=6)throw new Exception("Wrong accepted quantities");
            results.Add(new{resource,seconds=2,syntheticAcceptanceFraction=accepted,nativeRequestedOutput=EventRequested,acceptedOutput=actual,achievedOutputPerSecond=actual/2,nativeTimeFactor=native.TimeFactor,pass=true});
        }
        // A real native normal zero-efficiency early return has no broker calls.
        var zeroRecipe=new ConversionRecipe();zeroRecipe.SetInputs(new());zeroRecipe.SetOutputs(new(){new("Gypsum",3,true)});zeroRecipe.SetRequirements(new());
        var zeroBroker=new SyntheticBroker(1);var zeroFrame=Ledger.Enter("detached",zeroBroker,PartIdentity,"zero",101,2,true);
        var zeroResult=process(new ResourceConverter(zeroBroker),2,zeroRecipe,null,null,0);
        if(!Ledger.Complete(zeroFrame,true,zeroResult.TimeFactor)||zeroResult.TimeFactor!=0||zeroFrame.Outputs.Count!=0)throw new Exception("Zero completion failed");
        var storageResults=new List<object>();
        foreach(double storage in new[]{0.0,3.0,1000.0})
        {
            var recipe=new ConversionRecipe();recipe.SetInputs(new(){new("ElectricCharge",2,false)});recipe.SetOutputs(new(){new("Gypsum",3,false)});recipe.SetRequirements(new());recipe.FillAmount=1;recipe.TakeAmount=1;
            var broker=new SyntheticBroker(1){Storage=storage};var frame=Ledger.Enter("detached",broker,PartIdentity,"storage",102,2,true);EventRequested=0;
            var native=process(new ResourceConverter(broker),2,recipe,null,null,1);
            if(!Ledger.Complete(frame,true,native.TimeFactor))throw new Exception("Storage frame failed");
            var delivered=frame.Outputs.TryGetValue("Gypsum",out var amount)?amount:0;
            double expected=Math.Min(6,storage);
            if(delivered!=expected||native.TimeFactor!=expected/3||EventRequested!=expected)throw new Exception("Native storage constraint failed");
            if(storage==0&&(broker.MutationCalls!=0||frame.Inputs.Count!=0||frame.Outputs.Count!=0))throw new Exception("Full storage did not stop native requests");
            storageResults.Add(new{syntheticStorageAvailable=storage,preparedRecipeOutputPerSecond=3,acceptedOutput=delivered,achievedPerSecond=delivered/2,timeFactor=native.TimeFactor,brokerMutationCalls=broker.MutationCalls,nativeNormalCompletion=true,pass=true});
        }
        var usiResults=new List<object>();
        foreach(double supplyAvailable in new[]{0.0,0.00125,1000.0})
        foreach(double mulchStorage in new[]{0.0,0.00125,1000.0})
        {
            var recipe=new ConversionRecipe();recipe.SetInputs(new(){new("Supplies",(double)(.0005f*5*.5f),true){FlowMode=ResourceFlowMode.ALL_VESSEL}});recipe.SetOutputs(new(){new("Mulch",(double)(.0005f*5*.5f),true){FlowMode=ResourceFlowMode.ALL_VESSEL}});recipe.SetRequirements(new());recipe.FillAmount=1;recipe.TakeAmount=1;
            var broker=new SyntheticBroker(1){Available=supplyAvailable,Storage=mulchStorage};var frame=Ledger.Enter("detached",broker,PartIdentity,"USI-supply",103,2,true);EventRequested=0;
            var native=process(new ResourceConverter(broker),2,recipe,null,null,1);if(!Ledger.Complete(frame,true,native.TimeFactor))throw new Exception("USI completion failed");
            double expectedInput=Math.Min(recipe.Inputs[0].Ratio*2,supplyAvailable);double expectedOutput=Math.Min(expectedInput,mulchStorage);
            double input=frame.Inputs.GetValueOrDefault("Supplies"),output=frame.Outputs.GetValueOrDefault("Mulch");
            if(Math.Abs(input-expectedInput)>1e-12||Math.Abs(output-expectedOutput)>1e-12)throw new Exception("USI shortage/store mismatch");
            usiResults.Add(new{channel="Supplies/Mulch",supplyAvailable,mulchStorage,seconds=2,acceptedInput=input,acceptedOutput=output,timeFactor=native.TimeFactor,pass=true});
        }
        foreach(double available in new[]{0.0,0.025,1000.0})
        {
            var recipe=new ConversionRecipe();recipe.SetInputs(new(){new("ElectricCharge",(double)(.01f*5),true){FlowMode=ResourceFlowMode.ALL_VESSEL}});recipe.SetOutputs(new());recipe.SetRequirements(new());recipe.FillAmount=1;recipe.TakeAmount=1;
            var broker=new SyntheticBroker(1){Available=available};var frame=Ledger.Enter("detached",broker,PartIdentity,"USI-EC",104,2,true);
            var native=process(new ResourceConverter(broker),2,recipe,null,null,1);if(!Ledger.Complete(frame,true,native.TimeFactor))throw new Exception("USI EC completion failed");
            double accepted=frame.Inputs.GetValueOrDefault("ElectricCharge");if(Math.Abs(accepted-Math.Min(recipe.Inputs[0].Ratio*2,available))>1e-12)throw new Exception("USI EC shortage mismatch");
            usiResults.Add(new{channel="crew EC",available,seconds=2,accepted,timeFactor=native.TimeFactor,pass=true});
        }
        int exactQualificationCases=Qualification.Run();
        Console.WriteLine(JsonSerializer.Serialize(new{scope="SYNTHETIC DETACHED: installed native ProcessRecipe IL copy, native data types, synthetic IResourceBroker; no live observations; recipe inputs are synthetic, never claimed as native PrepareRecipe output",requests,stores,detachedReplacements=replacements,zeroEfficiencyNormalCompletion=true,results,storageResults,usiResults,exactQualificationCases},new JsonSerializerOptions{WriteIndented=true}));
    }
    sealed class SyntheticBroker(double fraction):IResourceBroker
    {
        public double Storage=1000,Available=1000;public int MutationCalls;
        public double AmountAvailable(Part p,string r,double s,ResourceFlowMode f)=>Available;
        public double StorageAvailable(Part p,string r,double s,ResourceFlowMode f,double fill)=>Storage;
        public double RequestResource(Part p,string r,double amount,double s,ResourceFlowMode f){MutationCalls++;double accepted=amount*fraction;Ledger.Record(this,PartIdentity,r,amount,accepted,false);return accepted;}
        public double StoreResource(Part p,string r,double amount,double s,ResourceFlowMode f){MutationCalls++;double returned=-Math.Min(amount*fraction,Storage);Ledger.Record(this,PartIdentity,r,amount,returned,true);return returned;}
        public double AmountAvailable(Part p,int r,double s,ResourceFlowMode f)=>throw new NotSupportedException();
        public double StorageAvailable(Part p,int r,double s,ResourceFlowMode f,double fill)=>throw new NotSupportedException();
        public double RequestResource(Part p,int r,double a,double s,ResourceFlowMode f)=>throw new NotSupportedException();
        public double StoreResource(Part p,int r,double a,double s,ResourceFlowMode f)=>throw new NotSupportedException();
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Expanse.BrpColony
{
    public sealed class ColonyRecipe
    {
        public const string Contract="colony-proportional15-v1";
        public ConfigNode Record;
        public uint FlightId,ModuleId;
        public string Profile;
        public double Multiplier,Fill;
        public string BindingHash;
        public List<ResourceRatio> Inputs,Outputs,Requirements;
        static string Text(ConfigNode n,string key)=>n.GetValue(key)??throw new InvalidOperationException("Missing recipe "+key+".");
        static double Number(ConfigNode n,string key)=>double.Parse(Text(n,key),CultureInfo.InvariantCulture);
        static List<ResourceRatio> Rows(ConfigNode n,string key)
        {
            var result=n.GetNodes(key).Select(r=>new ResourceRatio{ResourceName=Text(r,"ResourceName"),Ratio=Number(r,"Ratio"),
                DumpExcess=bool.Parse(Text(r,"DumpExcess")),FlowMode=(ResourceFlowMode)Enum.Parse(typeof(ResourceFlowMode),Text(r,"FlowMode"))}).ToList();
            if(result.Count>16||result.Any(r=>!ProportionalMath.Finite(r.Ratio)||string.IsNullOrEmpty(r.ResourceName))||result.Select(r=>r.ResourceName).Distinct().Count()!=result.Count)throw new InvalidOperationException("Invalid bounded recipe vector.");
            return result;
        }
        static bool Ratio(double actual,double expected)=>Math.Abs(actual-expected)<=Math.Abs(expected)*1e-6;
        static bool Vector(List<ResourceRatio> rows,params KeyValuePair<string,double>[] expected)=>rows.Count==expected.Length&&expected.All(e=>rows.Any(r=>r.ResourceName==e.Key&&Ratio(r.Ratio,e.Value)));
        static KeyValuePair<string,double> R(string name,double value)=>new KeyValuePair<string,double>(name,value);
        public static ColonyRecipe Read(ConfigNode record)
        {
            if(record==null||record.GetValue("Contract")!=Contract||record.GetValue("Status")!="supported")throw new InvalidOperationException(record?.GetValue("Reason")??"Missing supported colony recipe.");
            var r=new ColonyRecipe{Record=record.CreateCopy(),Profile=Text(record,"Profile"),BindingHash=Text(record,"BindingHash"),FlightId=uint.Parse(Text(record,"FlightId"),CultureInfo.InvariantCulture),ModuleId=uint.Parse(Text(record,"ModuleId"),CultureInfo.InvariantCulture),Multiplier=Number(record,"Multiplier"),Fill=Number(record,"FillAmount"),Inputs=Rows(record,"INPUT_RESOURCE"),Outputs=Rows(record,"OUTPUT_RESOURCE"),Requirements=Rows(record,"REQUIRED_RESOURCE")};
            if(r.FlightId==0||r.ModuleId==0||!ProportionalMath.Finite(r.Multiplier)||r.Multiplier<=0||r.Multiplier>10000||Number(record,"TakeAmount")!=1||!ProportionalMath.Finite(r.Fill)||r.Fill<=0||r.Fill>1||r.Inputs.Any(x=>x.Ratio<0)||r.Outputs.Any(x=>x.Ratio<0)||r.Requirements.Any(x=>x.Ratio==0))throw new InvalidOperationException("Unsupported native recipe limits.");
            bool fixedSource=r.Profile=="ranger"&&r.Multiplier==1&&Vector(r.Inputs,R("Plutonium-238",1e-6))&&Vector(r.Outputs,R("ElectricCharge",50))&&r.Outputs[0].DumpExcess&&Vector(r.Requirements,R("Plutonium-238",20))&&r.Inputs[0].FlowMode==ResourceFlowMode.NO_FLOW;
            bool farm=r.Profile=="cultivate-s"&&Vector(r.Inputs,R("Substrate",.0026),R("Water",.0026),R("Fertilizer",.000026),R("ElectricCharge",5.49),R("Machinery",.000002))&&Vector(r.Outputs,R("Supplies",.00026),R("Recyclables",.000002))&&Vector(r.Requirements,R("Machinery",100))&&r.Outputs.Single(x=>x.ResourceName=="Recyclables").DumpExcess&&!r.Outputs.Single(x=>x.ResourceName=="Supplies").DumpExcess&&r.Outputs.All(x=>x.FlowMode==ResourceFlowMode.ALL_VESSEL);
            if(!fixedSource&&!farm)throw new InvalidOperationException("Owned native recipe differs from supported Ranger/Cultivate(S) vector.");
            return r;
        }
        public static List<ResourceRatio> Scale(List<ResourceRatio> rows,double scale)=>rows.Select(r=>new ResourceRatio{ResourceName=r.ResourceName,Ratio=r.Ratio*scale,DumpExcess=r.DumpExcess,FlowMode=r.FlowMode}).ToList();
    }
}

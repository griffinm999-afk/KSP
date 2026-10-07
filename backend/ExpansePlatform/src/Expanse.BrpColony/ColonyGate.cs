using System;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.IO;

namespace Expanse.BrpColony
{
    internal sealed class ColonyAuthorityUnavailableException:InvalidOperationException
    {internal ColonyAuthorityUnavailableException(string message):base(message){}}
    internal static class ColonyGate
    {
        internal static MethodInfo Method(string name)
        {
            var types=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="Expanse.WorldBridge").Select(a=>a.GetType("Expanse.WorldBridge.ColonyRuntime",false)).Where(t=>t!=null).ToArray();
            if(types.Length!=1)throw new InvalidOperationException("Exact colony owner unavailable.");
            return types[0].GetMethod(name,BindingFlags.Public|BindingFlags.Static)??throw new MissingMethodException(name);
        }
        internal static ConfigNode Capture(PartModule module)=>Method("CaptureColonyBrpRecipe").Invoke(null,new object[]{module}) as ConfigNode;
        internal static void Validate(ConfigNode record,Vessel vessel)
        {
            string reason=Method("ValidateColonyBrpRecipe").Invoke(null,new object[]{record,vessel}) as string;
            if(reason=="Proportional owner hold: Selected paid colony authority unavailable.")throw new ColonyAuthorityUnavailableException(reason);
            if(reason!=string.Empty)throw new InvalidOperationException(reason??"Colony recipe validation unavailable.");
        }
        static readonly Lazy<string> provenance=new Lazy<string>(ReadProvenance);
        internal static void Provenance()
        {string failure=provenance.Value;if(failure.Length>0)throw new InvalidOperationException(failure);}
        static string ReadProvenance()
        {
            try
            {
            foreach(var row in new[]{Tuple.Create(typeof(BackgroundResourceProcessing.BackgroundResourceProcessor).Assembly,"D514CFEF35388FECEAAFC79D235D8F800D7748FF273814F1E511B9E30ECC8E55"),Tuple.Create(typeof(ResourceConverter).Assembly,"DE21D1371A113617BF4F47036C5B087383258A42F5FD99402784AD1BEEF344D8"),Tuple.Create(typeof(USITools.USI_Converter).Assembly,"B67965E8C41A79634B17216E987222F57D4A5E1D584FA2FF92A803F72243FAC0")})
            {
                using(var stream=File.OpenRead(row.Item1.Location))using(var sha=SHA256.Create())
                    if(BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","")!=row.Item2)throw new InvalidOperationException("Proportional adapter native assembly provenance differs.");
            }
            return "";
            }
            catch(Exception ex){return ex.Message;}
        }
    }
}

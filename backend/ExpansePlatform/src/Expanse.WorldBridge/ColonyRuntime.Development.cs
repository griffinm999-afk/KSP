using System;
using System.IO;
using System.Linq;

namespace Expanse.WorldBridge
{
    public sealed partial class ColonyRuntime
    {
        private string developmentAuthorizationEpoch, developmentAuthorizedRoot, developmentAuthorizedSave, developmentAuthorizedPipe;
        private bool IsAuthorizedDevelopmentContext()
        {
            try
            {
                string root = Path.GetFullPath(KSPUtil.ApplicationRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                const string prefix = "-expanseColonyPipe=";
                var pipes = Environment.GetCommandLineArgs().Where(x=>x.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToArray();
                if (pipes.Length!=1) return false;
                string pipe = pipes[0].Substring(prefix.Length);
                Guid nonce;
                if (!pipe.StartsWith("ExpanseFoundations.Colonies.dev.",StringComparison.Ordinal) || !Guid.TryParseExact(pipe.Substring(pipe.LastIndexOf('.')+1),"N",out nonce) || nonce==Guid.Empty) return false;
                string save = HighLogic.SaveFolder;
                if (string.IsNullOrEmpty(save) || !save.StartsWith("ColonyBuild-",StringComparison.Ordinal) || save!=Path.GetFileName(save)) return false;
                if (developmentAuthorizationEpoch!=loadEpoch)
                {
                    developmentAuthorizationEpoch=loadEpoch;developmentAuthorizedRoot=null;developmentAuthorizedSave=null;developmentAuthorizedPipe=null;
                    string path=Path.Combine(root,"colony-development-authorization.cfg");
                    var file=new FileInfo(path);
                    if(!file.Exists || file.Length<=0 || file.Length>16384)return false;
                    var document=ConfigNode.Load(path);if(document==null)return false;
                    var node=document.GetNode("EXPANSE_COLONY_DEVELOPMENT_AUTHORIZATION") ?? document;
                    string configuredRoot=node.GetValue("root");
                    if(string.IsNullOrWhiteSpace(configuredRoot) || !Path.IsPathRooted(configuredRoot))return false;
                    developmentAuthorizedRoot=Path.GetFullPath(configuredRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
                    developmentAuthorizedSave=node.GetValue("saveFolder");developmentAuthorizedPipe=node.GetValue("colonyPipe");
                }
                return string.Equals(root,developmentAuthorizedRoot,StringComparison.OrdinalIgnoreCase) && save==developmentAuthorizedSave && pipe==developmentAuthorizedPipe;
            }
            catch { return false; }
        }
    }
}

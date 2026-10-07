using System.Runtime.CompilerServices;
[assembly: KSPAssembly("Expanse.BrpColony",1,0,0)]
[assembly: KSPAssemblyDependency("BackgroundResourceProcessing",0,2,7)]
// Installed USITools has no KSPAssembly metadata: its stock loader version is
// 0.0.0 under the DLL filename, independently of managed AssemblyVersion1.0.
[assembly: KSPAssemblyDependency("USITools",0,0,0)]
// Stock dependency filtering does not propagate an excluded dependency's
// dependencies to its dependants. Repeat BRP's actual loader dependencies.
[assembly: KSPAssemblyDependency("HarmonyKSP",1,0,0)]
[assembly: KSPAssemblyDependency("KSPBurst",1,5,5)]
[assembly: InternalsVisibleTo("Expanse.BrpColony.Tests")]

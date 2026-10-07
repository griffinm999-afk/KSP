using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Expanse.Foundations
{
    public sealed class Member
    {
        public uint Id, FlightId;
        public Pose Local;
        public bool TetherWasOn;
        public bool BodyWasKinematic,ServoWasKinematic;
        public ConfigNode Save()
        {
            var n=new ConfigNode("MEMBER");n.AddValue("id",Id);n.AddValue("flightId",FlightId);
            n.AddValue("position",Local.Position);n.AddValue("rotation",Local.Rotation);n.AddValue("tetherWasOn",TetherWasOn);
            n.AddValue("bodyWasKinematic",BodyWasKinematic);n.AddValue("servoWasKinematic",ServoWasKinematic);return n;
        }
        public static Member Load(ConfigNode n)
        {
            return new Member {Id=uint.Parse(n.GetValue("id")),FlightId=uint.Parse(n.GetValue("flightId")),
                Local=new Pose(DVector.Parse(n.GetValue("position")),DRotation.Parse(n.GetValue("rotation"))),
                TetherWasOn=n.GetValue("tetherWasOn")=="True",BodyWasKinematic=n.GetValue("bodyWasKinematic")=="True",ServoWasKinematic=n.GetValue("servoWasKinematic")=="True"};
        }
    }
    public sealed class Anchor
    {
        public string Id,Body,Name;
        public double BodyRadius;
        public uint ReferenceId;
        public Pose Surface;
        public int Revision;
        public bool GroundContactWasOn;
        public readonly List<Member> Members=new List<Member>();
        public ConfigNode Save()
        {
            var n=new ConfigNode("ANCHOR");n.AddValue("schema",1);n.AddValue("id",Id);n.AddValue("body",Body);
            n.AddValue("bodyRadius",Number.Write(BodyRadius));n.AddValue("name",Name);n.AddValue("reference",ReferenceId);
            n.AddValue("position",Surface.Position);n.AddValue("rotation",Surface.Rotation);n.AddValue("revision",Revision);n.AddValue("groundContactWasOn",GroundContactWasOn);
            foreach(var p in Members)n.AddNode(p.Save());return n;
        }
        public static Anchor Load(ConfigNode n)
        {
            if(n.GetValue("schema")!="1")throw new FormatException("Unsupported anchor schema");
            Guid.Parse(n.GetValue("id"));
            var a=new Anchor {Id=n.GetValue("id"),Body=n.GetValue("body"),Name=n.GetValue("name"),
                BodyRadius=Number.Read(n.GetValue("bodyRadius"),1)[0],ReferenceId=uint.Parse(n.GetValue("reference")),
                Surface=new Pose(DVector.Parse(n.GetValue("position")),DRotation.Parse(n.GetValue("rotation"))),Revision=int.Parse(n.GetValue("revision")),GroundContactWasOn=n.GetValue("groundContactWasOn")=="True"};
            foreach(var m in n.GetNodes("MEMBER"))a.Members.Add(Member.Load(m));
            if(a.BodyRadius<=0||a.Members.Count==0||a.Members.Select(m=>m.Id).Distinct().Count()!=a.Members.Count||!a.Members.Any(m=>m.Id==a.ReferenceId))
                throw new FormatException("Invalid anchor membership");
            return a;
        }
    }
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,GameScenes.FLIGHT,GameScenes.SPACECENTER,GameScenes.TRACKSTATION)]
    public sealed class FoundationRegistry : ScenarioModule
    {
        public static FoundationRegistry Instance;
        public readonly Dictionary<string,Anchor> Anchors=new Dictionary<string,Anchor>();
        readonly List<ConfigNode> unrecognized=new List<ConfigNode>();
        public bool Ready;
        public override void OnAwake() {base.OnAwake();Instance=this;}
        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);Hold.Clear();Anchors.Clear();unrecognized.Clear();Ready=false;
            foreach(var n in node.GetNodes("ANCHOR"))
            {
                try {var a=Anchor.Load(n);if(Anchors.ContainsKey(a.Id))throw new FormatException("Duplicate anchor");Anchors.Add(a.Id,a);}
                catch(Exception e) {unrecognized.Add(n.CreateCopy());Debug.LogError("[Foundations] Anchor retained but suspended: "+e.Message);}
            }
            Ready=true;
        }
        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);foreach(var a in Anchors.Values)node.AddNode(a.Save());foreach(var n in unrecognized)node.AddNode(n.CreateCopy());
        }
        public void OnDestroy() {if(Instance==this){Instance=null;Hold.Clear();}}
    }
}

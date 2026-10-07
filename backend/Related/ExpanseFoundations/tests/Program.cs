using System;
using System.Globalization;
using Expanse.Foundations;

static class Program
{
    static int checks;
    static void Check(bool yes,string message){checks++;if(!yes)throw new Exception(message);}
    static int Main()
    {
        try
        {
            var saved=new Pose(new DVector(60000.123456789,-317.111111111,750.12345),new DRotation(.03,.04,.01,.998).Unit());
            string p=saved.Position.ToString(),q=saved.Rotation.ToString();
            foreach(var culture in new[]{"en-US","de-DE","fr-FR","ar-SA"})
            {
                CultureInfo.CurrentCulture=new CultureInfo(culture);
                var round=saved;
                for(int i=0;i<1000;i++)
                {
                    round=new Pose(DVector.Parse(round.Position.ToString()),DRotation.Parse(round.Rotation.ToString()));
                    Check(round.Position.ToString()==p&&round.Rotation.ToString()==q,"Serialized pose changed");
                }
            }
            CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
            for(int i=0;i<1000;i++)
            {
                double a=i*.173;
                var body=new Pose(new DVector(1e8+i*111,2e8-i*231,4e8),new DRotation(0,Math.Sin(a/2),0,Math.Cos(a/2)));
                var local=new Pose(new DVector(35,-2,18),new DRotation(.02,.1,.2,.97).Unit());
                var world=body*(saved*local);
                var recovered=body.Inverse*world;
                Check((recovered.Position-(saved*local).Position).Length<1e-6,"Origin/rotation reconstruction error");
                Check(recovered.Rotation.AngleDegrees((saved*local).Rotation)<1e-10,"Orientation reconstruction error");
                // Rebase the reference algebraically without resampling a world pose.
                var newAnchor=saved*local;var newLocal=local.Inverse;
                Check(((newAnchor*newLocal).Position-saved.Position).Length<1e-9,"Reference rebase moved foundation");
                Check((newAnchor*newLocal).Rotation.AngleDegrees(saved.Rotation)<1e-10,"Reference rebase rotated foundation");
                Check(saved.Position.ToString()==p&&saved.Rotation.ToString()==q,"Authoritative pose mutated");
            }
            var sign=new DRotation(-saved.Rotation.X,-saved.Rotation.Y,-saved.Rotation.Z,-saved.Rotation.W);
            Check(sign.AngleDegrees(saved.Rotation)<1e-10,"Quaternion sign equivalence");
            foreach(string bad in new[]{"NaN,0,0","Infinity,0,0","0,0","a,b,c"})
            {bool rejected=false;try{DVector.Parse(bad);}catch(FormatException){rejected=true;}Check(rejected,"Bad position accepted");}
            foreach(string bad in new[]{"0,0,0,0","0,0,0,2","0,NaN,0,1"})
            {bool rejected=false;try{DRotation.Parse(bad);}catch(FormatException){rejected=true;}Check(rejected,"Bad rotation accepted");}
            var gate=new Settler();var still=new Pose(new DVector(0,0,0),DRotation.Identity);
            Check(!gate.Ready(still,10,true,2,.05),"Settled instantly");
            Check(!gate.Ready(still,11,true,2,.05),"Settled too soon");
            Check(gate.Ready(still,12,true,2,.05),"Still body did not settle");
            Check(!gate.Ready(still,13,false,2,.05),"Ineligible body accepted");
            Check(!gate.Ready(still,14,true,2,.05),"Ineligibility failed to reset gate");
            var tipped=new Pose(still.Position,new DRotation(0,0,System.Math.Sin(.1),System.Math.Cos(.1)));
            Check(!gate.Ready(tipped,16,true,2,.05),"Rotating body accepted");
            Check(gate.Ready(tipped,18,true,2,.05),"Settled rotation did not recover");
            Check(!gate.Ready(tipped,1,true,2,.05),"Backward clock accepted");
            Console.WriteLine("PASS: "+checks+" assertions; 4,000 culture-specific exact pose round trips; 1,000 origin/rotation/reference-rebase cases; invalid input rejection.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine("FAIL: "+e);return 1;}
    }
}

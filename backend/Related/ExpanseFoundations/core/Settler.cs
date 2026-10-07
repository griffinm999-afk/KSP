namespace Expanse.Foundations
{
    public sealed class Settler
    {
        double since=-1;
        DRotation rotation;
        public void Reset() {since=-1;}
        public bool Ready(Pose surfacePose,double now,bool eligible,double seconds,double degreesPerSecond)
        {
            if(!eligible||!Number.Finite(now)){Reset();return false;}
            if(since<0||now<since){since=now;rotation=surfacePose.Rotation;return false;}
            double elapsed=now-since;
            if(elapsed<seconds)return false;
            if(rotation.AngleDegrees(surfacePose.Rotation)/elapsed>degreesPerSecond)
            {since=now;rotation=surfacePose.Rotation;return false;}
            return true;
        }
    }
}

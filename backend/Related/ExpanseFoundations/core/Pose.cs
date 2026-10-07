using System;
using System.Globalization;

namespace Expanse.Foundations
{
    // Deliberately has no Unity/KSP dependency. The exact same source is compiled
    // into the plugin and exercised by the console verification suite.
    public struct DVector
    {
        public readonly double X, Y, Z;
        public DVector(double x, double y, double z) { X=x; Y=y; Z=z; }
        public double Length { get { return Math.Sqrt(X*X+Y*Y+Z*Z); } }
        public bool Finite { get { return Number.Finite(X)&&Number.Finite(Y)&&Number.Finite(Z); } }
        public static DVector operator +(DVector a,DVector b) { return new DVector(a.X+b.X,a.Y+b.Y,a.Z+b.Z); }
        public static DVector operator -(DVector a,DVector b) { return new DVector(a.X-b.X,a.Y-b.Y,a.Z-b.Z); }
        public static DVector operator *(DVector a,double b) { return new DVector(a.X*b,a.Y*b,a.Z*b); }
        public override string ToString() { return Number.Write(X)+","+Number.Write(Y)+","+Number.Write(Z); }
        public static DVector Parse(string value) { var v=Number.Read(value,3); return new DVector(v[0],v[1],v[2]); }
    }
    public struct DRotation
    {
        public readonly double X,Y,Z,W;
        public DRotation(double x,double y,double z,double w) { X=x;Y=y;Z=z;W=w; }
        public static DRotation Identity { get { return new DRotation(0,0,0,1); } }
        public DRotation Unit()
        {
            double n=Math.Sqrt(X*X+Y*Y+Z*Z+W*W);
            if(!Number.Finite(n)||n<1e-15) throw new FormatException("Invalid orientation");
            return new DRotation(X/n,Y/n,Z/n,W/n);
        }
        public DRotation Inverse { get { var q=Unit();return new DRotation(-q.X,-q.Y,-q.Z,q.W); } }
        public static DRotation operator *(DRotation a,DRotation b)
        { return new DRotation(a.W*b.X+a.X*b.W+a.Y*b.Z-a.Z*b.Y,a.W*b.Y-a.X*b.Z+a.Y*b.W+a.Z*b.X,a.W*b.Z+a.X*b.Y-a.Y*b.X+a.Z*b.W,a.W*b.W-a.X*b.X-a.Y*b.Y-a.Z*b.Z); }
        public DVector Apply(DVector p)
        {
            var q=Unit(); var u=new DVector(q.X,q.Y,q.Z);
            var t=Cross(u,p)*2; return p+t*q.W+Cross(u,t);
        }
        static DVector Cross(DVector a,DVector b) {return new DVector(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);}
        public double AngleDegrees(DRotation b)
        {
            var d=(Inverse*b).Unit();
            return 2*Math.Atan2(Math.Sqrt(d.X*d.X+d.Y*d.Y+d.Z*d.Z),Math.Abs(d.W))*180/Math.PI;
        }
        public override string ToString() {return Number.Write(X)+","+Number.Write(Y)+","+Number.Write(Z)+","+Number.Write(W);}
        public static DRotation Parse(string s)
        {
            var v=Number.Read(s,4);var q=new DRotation(v[0],v[1],v[2],v[3]);
            double n=v[0]*v[0]+v[1]*v[1]+v[2]*v[2]+v[3]*v[3];
            if(Math.Abs(n-1)>1e-5)throw new FormatException("Non-unit saved orientation");
            return q; // Validation must not round or renormalize the authoritative record.
        }
    }
    public struct Pose
    {
        public readonly DVector Position;
        public readonly DRotation Rotation;
        public Pose(DVector position,DRotation rotation) {Position=position;Rotation=rotation;}
        public Pose Inverse {get {var q=Rotation.Inverse; return new Pose(q.Apply(Position*-1),q);}}
        public static Pose operator *(Pose a,Pose b) {return new Pose(a.Position+a.Rotation.Apply(b.Position),a.Rotation*b.Rotation);}
    }
    public static class Number
    {
        public static bool Finite(double v) {return !double.IsNaN(v)&&!double.IsInfinity(v);}
        public static string Write(double v) {if(!Finite(v))throw new FormatException("Non-finite number");return v.ToString("R",CultureInfo.InvariantCulture);}
        public static double[] Read(string text,int count)
        {
            if(text==null)throw new FormatException("Missing coordinate");
            var parts=text.Split(',');if(parts.Length!=count)throw new FormatException("Wrong coordinate count");
            var result=new double[count];
            for(int i=0;i<count;i++) {result[i]=double.Parse(parts[i],CultureInfo.InvariantCulture);if(!Finite(result[i]))throw new FormatException("Non-finite coordinate");}
            return result;
        }
    }
}

using System;
using System.Linq;
using System.Reflection;
using Expanse.WorldBridge;
using UnityEngine;

// Development read-only native geometry observation; never alters a collider.
internal static class ColonyPlacementColliderShapeWitness
{
    internal static Bounds Read(Collider c, Vector3 origin, Quaternion inverse)
    {
        if(!Finite(origin)||!Finite(inverse.x)||!Finite(inverse.y)||!Finite(inverse.z)||!Finite(inverse.w)||Math.Abs(Quaternion.Dot(inverse,inverse)-1)>.001)throw new InvalidOperationException("Actual shape observation frame invalid");
        var box = c as BoxCollider; var mesh = c as MeshCollider;
        if (box != null || mesh != null)
        {
            if (mesh != null && mesh.sharedMesh == null) throw new InvalidOperationException("Actual mesh collider has no shared mesh");
            Bounds local = box != null ? new Bounds(box.center, box.size) : mesh.sharedMesh.bounds;
            Bounds result = default(Bounds); bool first = true;
            for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
            {
                Vector3 point = inverse * (c.transform.TransformPoint(local.center + Vector3.Scale(local.extents, new Vector3(x,y,z))) - origin);
                if(!Finite(point))throw new InvalidOperationException("Actual transformed collider point invalid");
                if (first) { result = new Bounds(point, Vector3.zero); first = false; } else result.Encapsulate(point);
            }
            return result;
        }
        Matrix4x4 matrix=c.transform.localToWorldMatrix;
        Vector3 a=new Vector3(matrix.m00,matrix.m10,matrix.m20),b=new Vector3(matrix.m01,matrix.m11,matrix.m21),axisC=new Vector3(matrix.m02,matrix.m12,matrix.m22);
        if(!Finite(a)||!Finite(b)||!Finite(axisC)||!Finite(a.sqrMagnitude)||!Finite(b.sqrMagnitude)||!Finite(axisC.sqrMagnitude)||a.sqrMagnitude<=0||b.sqrMagnitude<=0||axisC.sqrMagnitude<=0||Math.Abs(Vector3.Dot(a.normalized,b.normalized))>.00001||Math.Abs(Vector3.Dot(a.normalized,axisC.normalized))>.00001||Math.Abs(Vector3.Dot(b.normalized,axisC.normalized))>.00001)throw new InvalidOperationException("Actual analytic collider transform sheared/singular");
        var scale = c.transform.lossyScale; scale = new Vector3(Math.Abs(scale.x), Math.Abs(scale.y), Math.Abs(scale.z));
        var sphere = c as SphereCollider;
        if (sphere != null)
        {
            float radius = sphere.radius * Math.Max(scale.x, Math.Max(scale.y, scale.z));
            return new Bounds(inverse * (c.transform.TransformPoint(sphere.center) - origin), Vector3.one * (radius*2));
        }
        var capsule = c as CapsuleCollider;
        if (capsule != null)
        {
            int d = capsule.direction; if (d < 0 || d > 2) throw new InvalidOperationException("Unknown actual capsule axis");
            float axial = d == 0 ? scale.x : d == 1 ? scale.y : scale.z;
            float radius = capsule.radius * (d == 0 ? Math.Max(scale.y,scale.z) : d == 1 ? Math.Max(scale.x,scale.z) : Math.Max(scale.x,scale.y));
            float segment = Math.Max(0, capsule.height*axial/2-radius);
            Vector3 axis = inverse * c.transform.TransformDirection(d == 0 ? Vector3.right : d == 1 ? Vector3.up : Vector3.forward).normalized;
            Vector3 extents = new Vector3(Math.Abs(axis.x),Math.Abs(axis.y),Math.Abs(axis.z))*segment + Vector3.one*radius;
            return new Bounds(inverse*(c.transform.TransformPoint(capsule.center)-origin),extents*2);
        }
        throw new InvalidOperationException("Unsupported actual collider shape: " + c.GetType().FullName);
    }

    internal static void Diagnose(Vessel vessel, ColonyPlacementRequest request, ColonyPlacementStatus status, ConfigNode witness)
    {
        var body = vessel.mainBody; double height = status.SurveyTerrainHeight;
        Vector3d center = body.GetWorldSurfacePosition(request.Latitude,request.Longitude,height);
        Vector3 up = (Vector3)(center-body.position).normalized; RaycastHit terrain;
        if (!Physics.Raycast((Vector3)center+up*50,-up,out terrain,100,1<<15,QueryTriggerInteraction.Ignore)) throw new InvalidOperationException("Actual loaded held terrain missing");
        var correctNorth = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(request.Latitude+.001,request.Longitude,height)-center),terrain.normal).normalized;
        var oldNorth = Vector3.ProjectOnPlane((Vector3)(body.GetWorldSurfacePosition(request.Latitude+.001,request.Longitude,0)-center),terrain.normal).normalized;
        var correct = Quaternion.LookRotation(Quaternion.AngleAxis((float)request.HeadingDegrees,terrain.normal)*correctNorth,terrain.normal);
        var old = Quaternion.LookRotation(Quaternion.AngleAxis((float)request.HeadingDegrees,terrain.normal)*oldNorth,terrain.normal);
        witness.AddValue("targetOperationId",request.OperationId); witness.AddValue("vesselId",vessel.id); witness.AddValue("requestFingerprint",request.Fingerprint()); witness.AddValue("body",body.bodyName); witness.AddValue("terrainHeight",height);
        witness.AddValue("oldNorthSameHeightAngleErrorDegrees",Vector3.Angle(oldNorth,correctNorth)); witness.AddValue("correctNorth",correctNorth); witness.AddValue("oldNorth",oldNorth); witness.AddValue("actualNormal",terrain.normal); witness.AddValue("loaded",vessel.loaded); witness.AddValue("packed",vessel.packed);
        var native = typeof(ColonyPlacementScenario).Assembly.GetType("Expanse.WorldBridge.ColonyPlacementRuntime");
        foreach (string method in new[]{"SettledEnvelope","DeclaredDeploymentClearance"})
            witness.AddValue("installed"+method,native.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{vessel,request}));
        foreach(var c in vessel.parts.SelectMany(p=>p.GetComponentsInChildren<Collider>()).Where(c=>c.enabled&&!c.isTrigger&&c.gameObject.layer!=21).Distinct())
        {
            var row = witness.AddNode("ACTUAL_COLLIDER"); var owner = c.GetComponentInParent<Part>(); row.AddValue("type",c.GetType().FullName); row.AddValue("name",c.name); row.AddValue("layer",c.gameObject.layer); row.AddValue("ownerPersistentId",owner==null?0:owner.persistentId);
            row.AddValue("lossyScale",c.transform.lossyScale.ToString("F6"));
            var box=c as BoxCollider; var mesh=c as MeshCollider;
            if(box!=null){row.AddValue("localCenter",box.center.ToString("F6"));row.AddValue("localSize",box.size.ToString("F6"));}
            if(mesh!=null&&mesh.sharedMesh!=null){row.AddValue("localCenter",mesh.sharedMesh.bounds.center.ToString("F6"));row.AddValue("localSize",mesh.sharedMesh.bounds.size.ToString("F6"));row.AddValue("convex",mesh.convex);row.AddValue("meshVertexCount",mesh.sharedMesh.vertexCount);}
            foreach(var frame in new[]{new{Key="sameAltitudeFrame",Value=correct},new{Key="oldAltitudeZeroFrame",Value=old}})
            {
                Quaternion inverse=Quaternion.Inverse(frame.Value); Bounds shape=Read(c,terrain.point,inverse);
                var entry=row.AddNode(frame.Key); entry.AddValue("shapeMinimum",shape.min.ToString("F6")); entry.AddValue("shapeMaximum",shape.max.ToString("F6")); entry.AddValue("shapeWithinDeclared02m",Within(shape,request));
                Bounds world=c.bounds; Bounds projected=default(Bounds); bool first=true;
                for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)for(int z=-1;z<=1;z+=2){var point=inverse*(world.center+Vector3.Scale(world.extents,new Vector3(x,y,z))-terrain.point);if(first){projected=new Bounds(point,Vector3.zero);first=false;}else projected.Encapsulate(point);}
                entry.AddValue("worldAabbMinimum",projected.min.ToString("F6")); entry.AddValue("worldAabbMaximum",projected.max.ToString("F6")); entry.AddValue("worldAabbWithinDeclared02m",Within(projected,request));
            }
        }
    }
    private static bool Within(Bounds b,ColonyPlacementRequest r) => b.min.x>=r.MinX-.02&&b.max.x<=r.MaxX+.02&&b.min.z>=r.MinZ-.02&&b.max.z<=r.MaxZ+.02&&b.max.y<=r.MaximumHeight+.02;
    private static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
    private static bool Finite(Vector3 value)=>Finite(value.x)&&Finite(value.y)&&Finite(value.z);
}

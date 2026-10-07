"""Read exact installed Mu assets for static candidate review, never native physics.

The separately retained, SHA-pinned GPL Mu review reader is not distributed with
the product. Override its Blender coordinate conversion to retain Unity XYZ.
Model bounds include switchable geometry, and do not certify live colliders.
"""
from pathlib import Path
import hashlib, importlib.util, itertools, math
import ColonyPlacementTemplates as t

READER_SHA = 'f13e4eff37db6e77aec24ebb1d8f9e06cc3001fdae5c213682f836ea3551544d'
MESH_PATHS = {
    'batteryBankLarge': 'Squad/Parts/Electrical/z-4kBattery/model.mu',
    'structuralPanel2': 'Squad/Parts/Structural/structuralPanel2x2/model.mu',
    'structuralPanel1': 'Squad/Parts/Structural/structuralPanel1x1/model.mu',
    'strutOcto': 'Squad/Parts/Structural/strutOcto/model.mu',
}

def bounds(points):
    if not points: raise ValueError('Missing physical model points')
    if not all(math.isfinite(v) for p in points for v in p): raise ValueError('Nonfinite model geometry')
    return {'Min': [min(p[i] for p in points) for i in range(3)], 'Max': [max(p[i] for p in points) for i in range(3)]}

def corners(center, size):
    return [t.add(center, [sign[i]*size[i]/2 for i in range(3)]) for sign in itertools.product((-1,1), repeat=3)]

def intersect(a,b,margin=.02):
    return all(min(a['Max'][i],b['Max'][i])-max(a['Min'][i],b['Min'][i]) > margin for i in range(3))

class Models:
    def __init__(self, builder, reader):
        if hashlib.sha256(reader.read_bytes()).hexdigest() != READER_SHA: raise ValueError('Unreviewed Mu reader')
        spec=importlib.util.spec_from_file_location('colony_retained_mu_review',reader); m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
        class UnityMu(m.Mu):
            def read_vector(self): return self.read_float(3)
            def read_quaternion(self): return self.read_float(4)
        self.reader,self.builder,self.UnityMu,self.results=reader,builder,UnityMu,{}

    def part(self,name):
        name=name.replace('.','_') if name.replace('.','_') in self.builder.parts else name
        if name in self.results: return self.results[name]
        cfg=self.builder.cfg(name); models=t.children(cfg,'MODEL'); assets=[]
        if not models:
            if name not in MESH_PATHS: raise ValueError('Unreviewed legacy mesh path '+name)
            models=[t.fresh('MODEL',model=MESH_PATHS[name][:-3])]
        points=[]; colliders=[]; airlocks=[]; animations=[]
        for model in models:
            if t.vec(t.val(model,'rotation','0,0,0')) != [0,0,0]: raise ValueError('Unreviewed MODEL rotation')
            path=(self.builder.install/'GameData'/(t.val(model,'model')+'.mu')).resolve()
            if not path.is_relative_to((self.builder.install/'GameData').resolve()): raise ValueError('Model traversal')
            data=path.read_bytes(); mu=self.UnityMu().read(str(path)); assert mu is not None
            # Exact installed GameDatabase model preload resets Mu root position
            # and rotation; PartLoader then applies MODEL scale/position/rotation.
            # Exporter scene placement is not part-local physical geometry.
            mu.obj.transform.localPosition=(0,0,0); mu.obj.transform.localRotation=(0,0,0,1); mu.obj.transform.localScale=(1,1,1)
            assets.append({'RelativePath':str(path.relative_to(self.builder.install)).replace('\\','/'),'Sha256':hashlib.sha256(data).hexdigest()})
            mp=t.vec(t.val(model,'position','0,0,0')); ms=t.vec(t.val(model,'scale','1,1,1')); rescale=float(t.val(cfg,'rescaleFactor','1'))
            def transform(p,chain):
                for tr in reversed(chain): p=t.add(tr.localPosition,t.rotate(tr.localRotation,[p[i]*tr.localScale[i] for i in range(3)]))
                return [(p[i]*ms[i]+mp[i])*rescale for i in range(3)]
            def visit(obj,chain):
                chain=chain+[obj.transform]; label=obj.transform.name
                mesh=getattr(obj,'shared_mesh',None)
                if mesh: points.extend(transform(p,chain) for p in mesh.verts)
                skin=getattr(obj,'skinned_mesh_renderer',None)
                if skin: raise ValueError('Skinned geometry requires separate review: '+name)
                col=getattr(obj,'collider',None)
                if col is not None:
                    cp=[]
                    if hasattr(col,'mesh'): cp=col.mesh.verts
                    elif hasattr(col,'size'): cp=corners(col.center,col.size)
                    elif hasattr(col,'radius'):
                        size=[col.radius*2]*3
                        if hasattr(col,'height'): size[col.direction]=max(size[col.direction],col.height)
                        cp=corners(col.center,size)
                    else: raise ValueError('Unsupported collider '+type(col).__name__)
                    bp=[transform(p,chain) for p in cp]
                    tag=getattr(getattr(obj,'tag_and_layer',None),'tag','')
                    row={'Name':label,'Type':type(col).__name__,'Trigger':getattr(col,'isTrigger',False),'Tag':tag,'Bounds':bounds(bp)}
                    if str(tag).lower()=='airlock' or 'airlock' in label.lower(): airlocks.append(row)
                    if not row['Trigger']: colliders.append(row); points.extend(bp)
                if hasattr(obj,'animation'): animations.append(label)
                for child in obj.children: visit(child,chain)
            visit(mu.obj,[])
        result={'Name':name,'Bounds':bounds(points),'Colliders':colliders,'Airlocks':airlocks,'Animations':animations,'Assets':assets}
        self.results[name]=result;return result

    def world(self,part):
        model=self.part(self.builder.part_name(part)); q=t.vec(t.val(part,'rot')); pos=t.vec(t.val(part,'pos'))
        def move(b):
            return bounds([t.add(pos,t.rotate(q,p)) for p in corners([(b['Min'][i]+b['Max'][i])/2 for i in range(3)], [b['Max'][i]-b['Min'][i] for i in range(3)])])
        return {'CraftPartId':int(t.val(part,'part').rsplit('_',1)[1]),'Bounds':move(model['Bounds']),
                'Colliders':[dict(c,Bounds=move(c['Bounds'])) for c in model['Colliders']],
                'Airlocks':[dict(c,Bounds=move(c['Bounds'])) for c in model['Airlocks']]}

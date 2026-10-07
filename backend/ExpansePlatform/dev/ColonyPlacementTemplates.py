"""Build candidate colony .craft packages from installed patched parts/known VAB trees.

Reads the named installation only. Writes only new source package templates/manifests.
It never creates a VESSEL/save fixture or installs anything. Runtime certification is
deliberately false until the product adapter and commissioning tests qualify it.
"""
from pathlib import Path
import argparse, copy, hashlib, json, math, re, collections


def parse(text):
    root = {'tag': 'ROOT', 'v': {}, 'c': []}; stack = [root]; pending = ''
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith('//'): continue
        if line == '{':
            node = {'tag': pending, 'v': {}, 'c': []}; stack[-1]['c'].append(node); stack.append(node)
        elif line == '}': stack.pop()
        elif '=' in line:
            key, value = line.split('=', 1); stack[-1]['v'].setdefault(key.strip(), []).append(value.strip())
        else: pending = line
    if len(stack) != 1: raise ValueError('Unbalanced installed ConfigNode')
    return root


def children(node, tag): return [x for x in node['c'] if x['tag'] == tag]
def val(node, key, default=''): return node['v'].get(key, [default])[0]
def setv(node, key, value): node['v'][key] = [str(value)]
def walk(node):
    yield node
    for child in node['c']: yield from walk(child)
def fresh(tag, **values): return {'tag': tag, 'v': {k: [str(v)] for k, v in values.items()}, 'c': []}
def serialize(node, depth=0):
    tab = '\t' * depth; lines = []
    if node['tag'] != 'ROOT': lines = [tab + node['tag'], tab + '{']; depth += 1; tab = '\t' * depth
    for key, values in node['v'].items():
        for value in values: lines.append(tab + key + ' = ' + value)
    for child in node['c']: lines.append(serialize(child, depth))
    if node['tag'] != 'ROOT': lines.append('\t' * (depth - 1) + '}')
    return '\n'.join(lines)
def vec(value): return [float(x.strip()) for x in value.replace('\\t', '').split(',') if x.strip()]
def vstr(value): return ','.join(format(x, '.10g') for x in value)
def add(a, b): return [x + y for x, y in zip(a, b)]
def sub(a, b): return [x - y for x, y in zip(a, b)]
def dot(a, b): return sum(x * y for x, y in zip(a, b))
def cross(a, b): return [a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0]]
def unit(v):
    n = math.sqrt(dot(v, v))
    if n < 1e-10: raise ValueError('Zero vector/quaternion')
    return [x / n for x in v]
def rotate(q, v):
    u = q[:3]; s = q[3]
    return add(add([2*dot(u, v)*x for x in u], [(s*s-dot(u, u))*x for x in v]), [2*s*x for x in cross(u, v)])
def from_to(a, b):
    a, b = unit(a), unit(b); d = dot(a, b)
    if d < -0.999999:
        axis = cross(a, [1, 0, 0] if abs(a[0]) < .9 else [0, 1, 0]); return unit(axis) + [0]
    return unit(cross(a, b) + [1+d])


def physical_units(amount):
    """Conservative fixed point, never above the native double tank amount."""
    if not math.isfinite(amount) or not 0 <= amount <= 1_000_000_000:
        raise ValueError('Invalid physical resource amount')
    units = math.floor(amount * 1_000_000)
    # At this bounded magnitude a multiplication-rounding overrun is <1 unit.
    # Test the same int->double division the placement protocol will perform.
    if units / 1_000_000.0 > amount: units -= 1
    assert 0 <= units and units / 1_000_000.0 <= amount
    return units


class Builder:
    def __init__(self, install, out):
        self.install, self.out = install, out
        cache = install / 'GameData/ModuleManager.ConfigCache'; data = cache.read_bytes(); self.cache_hash = hashlib.sha256(data).hexdigest()
        self.cache = parse(data.decode('utf-8-sig'))
        self.parts = {val(n, 'name'): n for n in walk(self.cache) if n['tag'] == 'PART' and val(n, 'name')}
        self.resources = {val(n, 'name'): n for n in walk(self.cache) if n['tag'] == 'RESOURCE_DEFINITION'}
        kis_configs = [n for n in walk(self.cache) if n['tag'] == 'KISConfig']
        if len(kis_configs) != 1: raise ValueError('Required KIS compiler settings missing or duplicated')
        self.compiler_inputs = {'KisConfigurationSha256': hashlib.sha256(serialize(kis_configs[0]).encode()).hexdigest()}
        self.out.mkdir(parents=True, exist_ok=True); self.manifests = []

    def cfg(self, name):
        if name in self.parts: return self.parts[name]
        return self.parts[name.replace('.', '_')]

    def part_name(self, part): return val(part, 'part').rsplit('_', 1)[0]
    def module(self, part, name): return next(x for x in children(part, 'MODULE') if val(x, 'name') == name)
    def node(self, part, node):
        return vec(val(self.cfg(self.part_name(part)), 'node_stack_' + node))

    def new_part(self, name):
        cfg = self.cfg(name)
        part = fresh('PART', part=name+'_0', partName='Part', persistentId=0, pos='0,0,0', attPos='0,0,0', attPos0='0,0,0',
                     rot='0,0,0,1', attRot='0,0,0,1', attRot0='0,0,0,1', mir='1,1,1', symMethod='Radial', autostrutMode='Off',
                     rigidAttachment='False', istg=-1, resPri=0, dstg=0, sidx=-1, sqor=-1, sepI=-1, attm=0,
                     sameVesselCollision='False', modCost=0, modMass=0, modSize='0,0,0')
        for module in children(cfg, 'MODULE'):
            state = copy.deepcopy(module); setv(state, 'isEnabled', True)
            if val(module, 'name') == 'TweakScale':
                setv(state, 'defaultScale', val(module, 'defaultScale', '100')); setv(state, 'currentScale', val(module, 'defaultScale', '100'))
            part['c'].append(state)
        for resource in children(cfg, 'RESOURCE'):
            state = copy.deepcopy(resource); setv(state, 'amount', 0); part['c'].append(state)
        return part

    def header(self, name, parts):
        craft = fresh('ROOT', ship=name, version='1.12.5', description='Expanse colony candidate v1; native placement/commissioning pending.', type='VAB',
                      size='0,0,0', steamPublishedFileId=0, persistentId=0, rot='0,0,0,1', missionFlag='Squad/Flags/default', vesselType='Base',
                      OverrideDefault='False,False,False,False', OverrideActionControl='0,0,0,0', OverrideAxisControl='0,0,0,0', OverrideGroupNames=',,,')
        craft['c'] = parts; return craft

    def attach(self, parts, parent_index, parent_node, child, child_node, rotation=None):
        parent = parts[parent_index]; own = self.node(child, child_node); target = self.node(parent, parent_node)
        pq = vec(val(parent, 'rot')); cq = unit(rotation) if rotation is not None else from_to(own[3:6], [-x for x in rotate(pq, target[3:6])])
        pos = sub(add(vec(val(parent, 'pos')), rotate(pq, target[:3])), rotate(cq, own[:3]))
        child_id = 100 + len(parts); setv(child, 'part', self.part_name(child) + '_' + str(child_id)); setv(child, 'pos', vstr(pos)); setv(child, 'rot', vstr(cq))
        parent['v'].setdefault('link', []).append(val(child, 'part'))
        self.att(parent, parent_node, val(child, 'part'), target)
        self.att(child, child_node, val(parent, 'part'), own)
        parts.append(child); return len(parts)-1

    def att(self, part, name, key, definition):
        position = '|'.join(format(x, '.10g') for x in definition[:3]); normal = '|'.join(format(x, '.10g') for x in definition[3:6])
        part['v'].setdefault('attN', []).append(name + ',' + key + '_' + position + '_' + normal + '_' + position + '_' + normal)

    def tank(self, resource):
        for name in ['C3_FlatTank_01', 'C3_FlatRnd_01']:
            cfg = self.cfg(name); switch = next(x for x in children(cfg, 'MODULE') if val(x, 'name') == 'FSfuelSwitch')
            names = val(switch, 'resourceNames').split(';')
            if resource not in names: continue
            index = names.index(resource); capacity = float(val(switch, 'resourceAmounts').split(';')[index])
            part = self.new_part(name)
            setv(self.module(part, 'FSfuelSwitch'), 'selectedTankSetup', index); setv(self.module(part, 'FSfuelSwitch'), 'configLoaded', True); setv(self.module(part, 'FSfuelSwitch'), 'hasLaunched', False)
            setv(self.module(part, 'FStextureSwitch2'), 'selectedTexture', index)
            setv(part, 'modCost', val(switch, 'tankCost').split(';')[index])
            part['c'].append(fresh('RESOURCE', name=resource, amount=0, maxAmount=capacity, flowState=True, isTweakable=True, hideFlow=False, flowMode='Both'))
            return part
        raise ValueError('No installed flat tank setup for ' + resource)

    def compact(self, purpose):
        parts = [self.new_part('KKAOSS_Central_Hub')]; setv(parts[0], 'part', 'KKAOSS_Central_Hub_100')
        wings = ['KKAOSS_Habitat_MK2_g', 'KKAOSS_Habitat_MK2_g'] if purpose == 'housing' else ['KKAOSS_MKS_Workshop', 'KKAOSS_Storage_g'] if purpose == 'service' else ['KKAOSS_Storage_g', 'KKAOSS_Storage_g']
        wing_indices = []
        for side, wing in zip([-1, 1], wings):
            q = [.5, .5, -.5, .5] if side == -1 else [-.5, .5, -.5, -.5]
            index = self.attach(parts, 0, 'right' if side == -1 else 'left', self.new_part(wing), 'top', q); wing_indices.append(index)
            self.attach(parts, index, 'bottom', self.new_part('KKAOSS_airlock_end_g'), 'bottom', q)
            if wing == 'KKAOSS_Habitat_MK2_g':
                setv(self.module(parts[index], 'PlanetaryModule'), 'animationTime', 1)
                setv(self.module(parts[index], 'ModuleKPBSDependentLight'), 'animationTime', 1)
        control = self.attach(parts, 0, 'top', self.new_part('KKAOSS_Landing_Control_g'), 'bottom', [0, 0, 0, 1])
        battery = self.attach(parts, control, 'top', self.new_part('batteryBank'), 'bottom', [0, 0, 0, 1])
        self.attach(parts, battery, 'top', self.new_part('rtg'), 'bottom', [0, 0, 0, 1])
        if purpose != 'housing':
            tank_resources = ['Metals', 'Chemicals', 'Polymers', 'Machinery', 'MaterialKits', 'SpecializedParts'] if purpose == 'service' else ['Supplies', 'Machinery', 'MaterialKits', 'SpecializedParts', 'Fertilizer', 'Gypsum', 'LiquidFuel', 'Oxidizer', 'Ore', 'Water', 'Recyclables', 'Organics']
            storage_indices = [i for i in wing_indices if self.part_name(parts[i]) == 'KKAOSS_Storage_g']
            for j, resource in enumerate(tank_resources):
                parent = storage_indices[j//6]; mount = ('left' if j%6 < 3 else 'right') + str(j%3+1)
                self.attach(parts, parent, mount, self.tank(resource), 'bottom')
        return self.header('Expanse ' + purpose.title() + ' v1', parts)

    def lamp(self):
        parts = [self.new_part('structuralPanel2')]; setv(parts[0], 'part', 'structuralPanel2_100')
        control = self.attach(parts, 0, 'topNW', self.new_part('KKAOSS_Landing_Control_g'), 'bottom', [0,0,0,1])
        pole = self.attach(parts, 0, 'topNE', self.new_part('structuralIBeam2'), 'bottom', [0,0,0,1])
        self.attach(parts, pole, 'top', self.new_part('rtg'), 'bottom', [0,0,0,1])
        light = self.new_part('spotLight3'); setv(light, 'part', 'spotLight3_104'); setv(light, 'attm', 1)
        setv(light, 'pos', vstr(add(vec(val(parts[pole], 'pos')), [0,1,.12])))
        setv(light, 'srfN', 'srfAttach,'+val(parts[pole],'part')+',,0|0|0,0|0|-1,0|0|0')
        parts[pole]['v'].setdefault('link',[]).append(val(light,'part'))
        setv(self.module(light,'ModuleLight'),'isOn',True); parts.append(light)
        return self.header('Expanse stock lamp mast v1',parts)

    def imported(self, source):
        path = self.install / ('saves/The Expanse/Ships/VAB/' + source + '.craft'); craft = parse(path.read_text(encoding='utf-8-sig'))
        parts = children(craft, 'PART'); parents = {val(x, 'part'): [] for x in parts}
        for parent in parts:
            for child in parent['v'].get('link', []): parents[child].append(val(parent, 'part'))
        roots = [x for x in parts if not parents[val(x, 'part')]]
        if len(roots) != 1: raise ValueError('Known VAB source has multiple roots')
        by_key = {val(x, 'part'): x for x in parts}; order = []
        def visit(part):
            order.append(part)
            for key in part['v'].get('link', []): visit(by_key[key])
        visit(roots[0]); assert len(order) == len(parts)
        mapping = {val(p, 'part'): self.part_name(p) + '_' + str(100+i) for i, p in enumerate(order)}
        origin = vec(val(order[0], 'pos'))
        for p in order:
            old = val(p, 'part'); setv(p, 'part', mapping[old]); setv(p, 'pos', vstr(sub(vec(val(p, 'pos')), origin)))
            for key in ['link', 'sym']:
                if key in p['v']: p['v'][key] = [mapping[x] for x in p['v'][key]]
            for key in ['attN']:
                if key in p['v']:
                    for old_key, new_key in mapping.items(): p['v'][key] = [s.replace(','+old_key+'_', ','+new_key+'_').replace(', '+old_key+'_', ', '+new_key+'_') for s in p['v'][key]]
            if 'srfN' in p['v']:
                records = []
                for surface in p['v']['srfN']:
                    fields = surface.split(',')
                    if len(fields) > 1 and fields[1].strip() in mapping: fields[1] = mapping[fields[1].strip()]
                    records.append(','.join(fields))
                p['v']['srfN'] = records
            q = unit(vec(val(p, 'rot'))); setv(p, 'rot', vstr(q))
        result = self.header('Expanse ' + source + ' v1', order)
        return result, hashlib.sha256(path.read_bytes()).hexdigest()

    def add_control_if_missing(self, craft):
        ps = children(craft, 'PART')
        if any(val(m, 'name') == 'ModuleCommand' and val(m, 'minimumCrew', '0') == '0' for p in ps for m in children(self.cfg(self.part_name(p)), 'MODULE')): return
        # Agriculture's saved round water tank has a genuinely free top stack node.
        index = next(i for i, p in enumerate(ps) if self.part_name(p).replace('.', '_') == 'C3_FlatRnd_01')
        self.attach(ps, index, 'top', self.new_part('KKAOSS_Landing_Control_g'), 'bottom', [0, 0, 0, 1]); craft['c'] = ps

    def append_rtg(self, craft):
        ps = children(craft, 'PART')
        preferred = sorted(range(len(ps)), key=lambda i: 0 if 'probe' in self.part_name(ps[i]).lower() or 'Control' in self.part_name(ps[i]) else 1)
        for index in preferred:
            cfg = self.cfg(self.part_name(ps[index])); used = {x.split(',',1)[0].strip() for x in ps[index]['v'].get('attN', []) if ',Null_0_' not in x}
            for name in ['bottom', 'top']:
                if 'node_stack_'+name in cfg['v'] and name not in used:
                    # Remove the null placeholder before writing the new attachment.
                    ps[index]['v']['attN'] = [x for x in ps[index]['v'].get('attN', []) if x.split(',',1)[0].strip() != name]
                    self.attach(ps, index, name, self.new_part('rtg'), 'bottom'); craft['c'] = ps; return
        raise ValueError('No free attached RTG node')

    def power_support(self, craft):
        # Homes0 power needs the Pioneer/PDU engineer seats, not the dormant
        # MiniHab whose low tent collider supports the heavy base off-centre.
        # Keep the RTG but move it from the underside to a real free side node.
        ps = children(craft, 'PART')
        hab = next(p for p in ps if self.part_name(p).replace('.', '_') == 'Ranger_MiniHab')
        pioneer = next(p for p in ps if self.part_name(p).replace('.', '_') == 'Duna_Pioneer')
        rtg = next(p for p in ps if self.part_name(p) == 'rtg')
        assert not hab['v'].get('link'), 'Cannot discard attached MiniHab children'
        assert val(hab, 'part').endswith('_103') and val(rtg, 'part').endswith('_113')
        for part, node in [(hab, 'pod01'), (rtg, 'top')]:
            key = val(part, 'part')
            assert key in pioneer['v']['link']
            pioneer['v']['link'].remove(key)
            records = [x for x in pioneer['v']['attN'] if x.split(',', 1)[0].strip() == node]
            assert len(records) == 1 and ',' + key + '_' in records[0]
            pioneer['v']['attN'] = [x for x in pioneer['v']['attN'] if x not in records]
            self.att(pioneer, node, 'Null_0', self.node(pioneer, node))
        ps.remove(hab)
        target = self.node(pioneer, 'pod05'); own = self.node(rtg, 'bottom')
        free = [x for x in pioneer['v']['attN'] if x.split(',', 1)[0].strip() == 'pod05']
        assert len(free) == 1 and ',Null_0_' in free[0], 'Power RTG side node occupied'
        pioneer['v']['attN'].remove(free[0])
        pq = vec(val(pioneer, 'rot'))
        q = from_to(own[3:6], [-x for x in rotate(pq, target[3:6])])
        pos = sub(add(vec(val(pioneer, 'pos')), rotate(pq, target[:3])), rotate(q, own[:3]))
        setv(rtg, 'pos', vstr(pos)); setv(rtg, 'rot', vstr(q))
        setv(rtg, 'attPos0', vstr(rotate([-pq[0], -pq[1], -pq[2], pq[3]], sub(pos, vec(val(pioneer, 'pos'))))))
        setv(rtg, 'attRot', vstr(q)); setv(rtg, 'attRot0', vstr(q))
        rtg['v']['attN'] = []
        self.att(rtg, 'bottom', val(pioneer, 'part'), own)
        pioneer['v']['link'].append(val(rtg, 'part'))
        self.att(pioneer, 'pod05', val(rtg, 'part'), target)
        craft['c'] = ps

    def clean(self, craft):
        setv(craft, 'persistentId', 0)
        for part in children(craft, 'PART'):
            setv(part, 'persistentId', 0)
            for key in ['crew','uid','mid','flightID','missionID','launchID']: part['v'].pop(key, None)
            for m in children(part, 'MODULE'):
                name = val(m, 'name')
                if name == 'WOLF_HopperModule':
                    for key in ['HopperId', 'DepotBody', 'DepotBiome']: setv(m, key, '')
                    setv(m, 'IsConnectedToDepot', False)
                if name == 'USI_InertialDampener': setv(m, 'isActive', False)
                if name == 'FMRS_PM': setv(m, 'parent_vessel', 0)
                for key in ['IsActivated','isActivated','isRunning','IsRunning','isHopperActive']:
                    if key in m['v']: setv(m, key, False)
            for resource in children(part, 'RESOURCE'): setv(resource, 'amount', 0)

    def native_part_keys(self, craft):
        # PartLoader changes config-name '_' to '.', and KSPUtil.GetPartInfo
        # splits at the FIRST '_'. Names in saved craft must use the native alias.
        ps=children(craft,'PART'); mapping={val(p,'part'):self.part_name(p).replace('_','.')+'_'+val(p,'part').rsplit('_',1)[1] for p in ps}
        for p in ps:
            setv(p,'part',mapping[val(p,'part')])
            for key in ['link','sym']:
                if key in p['v']:p['v'][key]=[mapping[x] for x in p['v'][key]]
            for key in ['attN','srfN']:
                if key not in p['v']:continue
                revised=[]
                for record in p['v'][key]:
                    fields=record.split(','); payload=fields[1].strip()
                    if key=='srfN' and payload in mapping:fields[1]=mapping[payload]
                    elif key=='attN':
                        old=next((x for x in mapping if payload.startswith(x+'_')),None)
                        if old:fields[1]=mapping[old]+payload[len(old):]
                    revised.append(','.join(fields))
                p['v'][key]=revised

    def validate(self, craft):
        ps = children(craft, 'PART'); keys = {val(p, 'part'): p for p in ps}; parent = collections.defaultdict(list)
        assert len(keys) == len(ps) and 1 <= len(ps) <= 256
        for p in ps:
            native_name, native_id = val(p,'part').split('_',1)
            assert native_id.isdecimal() and int(native_id)>0 and native_name.replace('.','_') in self.parts, ('native part key cannot be parsed by KSPUtil.GetPartInfo',val(p,'part'))
            for key in p['v'].get('link', []): assert key in keys; parent[key].append(val(p, 'part'))
            assert len(parent[val(p,'part')]) <= 1
            assert val(p,'persistentId') == '0' and 'crew' not in p['v']
            assert all(float(val(r,'amount')) == 0 for r in children(p,'RESOURCE'))
            assert abs(dot(vec(val(p,'rot')),vec(val(p,'rot'))) - 1) < 1e-5
            for surface in p['v'].get('srfN', []):
                fields = [x.strip() for x in surface.split(',')]
                if len(fields) == 2 and fields[1] == 'Null_0': continue
                assert len(fields) == 6 and fields[1] in keys, ('malformed/stale surface parent', val(p,'part'), surface)
                rules = vec(val(self.cfg(self.part_name(p)), 'attachRules')); parent_rules = vec(val(self.cfg(self.part_name(keys[fields[1]])), 'attachRules'))
                assert rules[1] == 1 and parent_rules[3] == 1, ('surface attachRules prohibited', val(p,'part'))
                definition = vec(val(self.cfg(self.part_name(p)), 'node_attach'))
                assert len(definition) >= 6 and math.dist(definition[:3], [float(x) for x in fields[3].split('|')]) < .002
                assert dot(unit(definition[3:6]), unit([float(x) for x in fields[4].split('|')])) > .999
                assert parent[val(p,'part')] == [fields[1]], ('surface parent is not the serialized connected parent',val(p,'part'))
            for record in p['v'].get('attN', []):
                node_name, payload = record.split(',', 1); node_name = node_name.strip(); payload = payload.strip()
                # Match complete serialized part keys. Their trailing _0 may be part of
                # the key; never split it into a mistaken attachment offset.
                key = next((k for k in keys if payload.startswith(k+'_')), None)
                if payload.startswith('Null_0_'): continue
                assert key is not None, record
                offsets = payload[len(key)+1:].split('_'); assert len(offsets) == 4 and all(len(x.split('|')) == 3 for x in offsets), record
                cfg_node = self.node(p, node_name); local_node = [float(x) for x in offsets[0].split('|')]
                # Saved source nodes can be scaled by stock/TweakScale (inspect this
                # explicitly); fresh parts always match current installed definitions.
                ratio = 1
                tweaks = [m for m in children(p,'MODULE') if val(m,'name') == 'TweakScale']
                if tweaks: ratio = float(val(tweaks[0],'currentScale',val(tweaks[0],'defaultScale','1'))) / float(val(tweaks[0],'defaultScale','1'))
                assert math.dist(local_node,[x*ratio for x in cfg_node[:3]]) < .002, (self.part_name(p),node_name,local_node,cfg_node[:3],ratio)
                position = add(vec(val(p,'pos')),rotate(vec(val(p,'rot')),local_node))
                matches = []
                for peer in keys[key]['v'].get('attN', []):
                    if val(p,'part')+'_' not in peer: continue
                    peer_payload = peer.split(',',1)[1].strip(); peer_offsets = peer_payload[len(val(p,'part'))+1:].split('_')
                    peer_position = add(vec(val(keys[key],'pos')),rotate(vec(val(keys[key],'rot')),[float(x) for x in peer_offsets[0].split('|')]))
                    normal = rotate(vec(val(p,'rot')),[float(x) for x in offsets[1].split('|')]); peer_normal = rotate(vec(val(keys[key],'rot')),[float(x) for x in peer_offsets[1].split('|')])
                    matches.append(math.dist(position,peer_position) < .003 and dot(unit(normal),unit(peer_normal)) < -.999)
                assert any(matches), ('nonreciprocal/misaligned attachment',val(p,'part'),node_name,key)
        roots = [k for k in keys if not parent[k]]; assert len(roots) == 1 and roots[0] == val(ps[0],'part')
        seen = set(); todo = roots[:]
        while todo:
            key = todo.pop(); assert key not in seen; seen.add(key); todo += keys[key]['v'].get('link',[])
        assert len(seen) == len(ps)
        assert any(val(m,'name')=='ModuleCommand' and val(m,'minimumCrew','0')=='0' for p in ps for m in children(self.cfg(self.part_name(p)),'MODULE'))
        return {'ConnectedTree':True,'UniqueCraftIds':True,'ZeroResourceDefaults':True,'ReciprocalAlignedStackNodes':True,'SurfaceParentsAndRules':True,'UncrewedCommandPresent':True,'RuntimePlacement':'pending'}

    def emit(self, identifier, name, craft, envelope, homes=0, workers=0, trait='', source_hash='', purpose=''):
        self.native_part_keys(craft); self.clean(craft)
        # MM config rows are localized while loading prefabs; saved craft module
        # curves bypass that step. Stock FloatCurve.Load parses whitespace tokens,
        # so a literal backslash-t is not a saved numeric token.
        for node in walk(craft):
            if 'key' in node['v']:
                rows = []
                for row in node['v']['key']:
                    tokens = row.replace('\\t', ' ').split()
                    if len(tokens) not in (2, 4) or not all(math.isfinite(float(token)) for token in tokens): raise ValueError('Invalid native saved curve row: ' + row)
                    rows.append(' '.join(tokens))
                node['v']['key'] = rows
        checks = self.validate(craft); text = serialize(craft)+'\n'; path=self.out/(identifier+'.craft'); path.write_text(text,encoding='utf-8',newline='\n')
        ps=children(craft,'PART'); capacities=[]; allocations=[]; raw=[]; dry_cost=0.; dry_mass=0.; nominal_seats=0; recipes=[]; power=[]; services=[]
        for p in ps:
            cfg=self.cfg(self.part_name(p)); cid=int(val(p,'part').rsplit('_',1)[1]); modules=children(cfg,'MODULE'); mass=float(val(cfg,'mass','0'))+float(val(p,'modMass','0')); cost=float(val(cfg,'cost','0'))+float(val(p,'modCost','0'))
            nominal_seats+=int(val(cfg,'CrewCapacity','0'))
            for resource in children(p,'RESOURCE'):
                r=val(resource,'name'); cap=float(val(resource,'maxAmount')); definition=self.resources[r]; cost-=cap*float(val(definition,'unitCost','0'))
                capacities.append({'CraftPartId':cid,'ResourceName':r,'Capacity':physical_units(cap),'Warehouse':any(val(m,'name')=='USI_ModuleResourceWarehouse' for m in modules),'FlowMode':val(definition,'flowMode'),'DensityTonnesPerUnit':float(val(definition,'density','0'))})
                amount=0
                if r=='ElectricCharge': amount=cap
                elif r=='Machinery': amount=min(cap,2000 if self.part_name(p).replace('.','_') in ['Tundra_ASM','KKAOSS_MKS_Workshop'] else 100 if 'Agriculture' in self.part_name(p) else 500)
                elif r=='EnrichedUranium': amount=min(cap,1)
                elif r=='Plutonium-238': amount=min(cap,2)
                elif purpose=='agriculture' and r in ['Water','Substrate','Fertilizer','Supplies']: amount=min(cap, {'Water':300,'Substrate':300,'Fertilizer':100,'Supplies':120}[r])
                elif purpose=='fertilizer' and r=='Gypsum': amount=min(cap,1000)
                if amount: allocations.append({'CraftPartId':cid,'ResourceName':r,'Amount':physical_units(amount)})
            dry_mass+=mass; dry_cost+=cost
            raw.append({'CraftPartId':cid,'PartName':val(cfg,'name'),'PartConfigSha256':hashlib.sha256(serialize(cfg).encode()).hexdigest(),'DryMassTonnes':mass,'StockDryReferenceFunds':cost,'NominalSeats':int(val(cfg,'CrewCapacity','0')),'RequiredTech':val(cfg,'TechRequired'),'ModuleNames':[val(m,'name') for m in modules]})
            for m in modules:
                module=val(m,'name')
                if any(t in module for t in ['Converter','HopperSwap']): recipes.append({'CraftPartId':cid,'Module':module,'Recipe':val(m,'ConverterName',val(m,'RecipeName','')),'Inputs':[r['v'] for r in children(m,'INPUT_RESOURCE')],'Outputs':[r['v'] for r in children(m,'OUTPUT_RESOURCE')],'Required':[r['v'] for r in children(m,'REQUIRED_RESOURCE')],'SpecialistEffect':val(m,'ExperienceEffect'),'Provenance':'installed-patched-nominal; inactive until commissioning'})
                if any(t in module for t in ['Generator','Solar','CoreHeat','SystemHeat','Fission','PowerCoupler','PowerDistributor']): power.append({'CraftPartId':cid,'Module':module,'Values':m['v'],'Provenance':'installed-patched-nominal; grid/heat/throughput unqualified'})
                if any(t in module for t in ['Repair','LogisticsConsumer','ResourceDistributor','ResourceWarehouse']):services.append({'CraftPartId':cid,'Module':module,'BackgroundContext':'pending native provider qualification','Worker':'Engineer occupying workshop required for ModuleAutoRepairer' if module=='ModuleAutoRepairer' else 'module-specific qualification pending'})
        labor=math.ceil(max(1,dry_mass)*3600); materials=[{'Resource':'MaterialKits','Amount':math.ceil(dry_mass*500)*1000000},{'Resource':'SpecializedParts','Amount':math.ceil(dry_mass*20)*1000000}]
        embedded=collections.Counter()
        for x in allocations:embedded[x['ResourceName']]+=x['Amount']
        fees=math.ceil(labor/3600*250)+math.ceil(max(0,dry_cost)*.05)
        x0,x1,z0,z1,height,rotation=envelope
        manifest={'Id':identifier,'Name':name,'Version':1,'Hash':'','CraftSha256':hashlib.sha256(path.read_bytes()).hexdigest(),'CraftRelativePath':path.name,'BuildFunds':fees,'LaborSeconds':labor,'WidthMeters':x1-x0,'LengthMeters':z1-z0,'Homes':homes,'Workers':workers,'WorkerTrait':trait,'Materials':materials,'EmbeddedContents':[{'Resource':r,'Amount':a} for r,a in sorted(embedded.items())],'RequiredTech':sorted({x['RequiredTech'] for x in raw}),'RequiredPartNames':sorted({x['PartName'] for x in raw}),'RuntimeCertified':False,'CertificationEvidence':'Native placement, deployment, utility, input/service and save/reload qualification pending','CertificationId':identifier+'-candidate','PartConfigurationHash':self.cache_hash,'ExpectedPartCount':len(ps),'MinX':x0,'MaxX':x1,'MinZ':z0,'MaxZ':z1,'MaximumHeight':height,'TemplateRotationX':rotation[0],'TemplateRotationY':rotation[1],'TemplateRotationZ':rotation[2],'TemplateRotationW':rotation[3],'MaximumSlopeDegrees':3,'MaximumSupportGapMetres':.25,'ClearanceMetres':1,'StartupContents':allocations,'Purpose':purpose,'StaticValidation':checks,'SourceCraftSha256':source_hash,'StockDryReferenceFunds':dry_cost,'DryMassTonnes':dry_mass,'NominalSeats':nominal_seats,'ResourceCapacities':capacities,'RawParts':raw,'Recipes':recipes,'Utilities':power,'Services':services,'ProductionOwner':'physical/MKS/WOLF/BRP; no colony-simulation conversion','EnvelopeProvenance':'conservative development proposal; actual collider/deployment test pending','BalanceRules':{'Status':'configurable development defaults','MaterialKitsPerDryTonne':500,'SpecializedPartsPerDryTonne':20,'LaborSecondsPerDryTonne':3600,'LaborFundsPerHour':250,'FabricationFeeFractionOfStockDryReference':.05,'HardwareCharge':'BOM creates hardware; stock dry reference is not separately debited','ContentsCharge':'StartupContents aggregate into EmbeddedContents once; no default contents'}}
        manifest['RuntimeCompilerInputs'] = self.compiler_inputs
        manifest['LaborFunds'] = math.ceil(labor / 3600 * 250)
        manifest['HomeCraftPartIds'] = [101, 103] if purpose == 'housing' else []
        if identifier == 'power-duna-v1':
            manifest['EnvelopeProvenance'] = 'Engineering trial, deployed UNQUALIFIED:13-part power removes dormant MiniHab and relocates RTG to matched Pioneer side node after actual native centre-of-mass support-margin refusal of the prior14-part craft. Retained initial native shapes predict x[-4.142,1.893],z[-2.123,2.054],y[-2.877,1.644]; transformed prior-shape estimate only. Source-only radiator endpoint indicates deployed height above11m; reserved x[-9,7],z[-7,7],height13 with existing1m clearance. New actual preview/deployment/terrain/collision/footing/centre-of-mass and commissioning qualification pending; no predicate relaxation.'
        manifest['Hash']=hashlib.sha256(json.dumps(manifest,sort_keys=True,separators=(',',':')).encode()).hexdigest()
        (self.out/(identifier+'.manifest.json')).write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8'); self.manifests.append(manifest)


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--install',default='C:/Kerbal Space Program');parser.add_argument('--output',default='ExpansePlatform/package/GameData/ExpanseWorldBridge/Templates');parser.add_argument('--template',choices=['power-duna-v1']); args=parser.parse_args()
    b=Builder(Path(args.install),Path(args.output)); identity=[0,0,0,1]; yaw=[0,math.sin(math.pi/4),0,math.cos(math.pi/4)]
    if args.template:
        catalog_path = b.out/'colony-template-catalog.json'
        catalog = json.loads(catalog_path.read_text(encoding='utf-8'))
        assert catalog['PartConfigurationHash'] == b.cache_hash and len([x for x in catalog['Templates'] if x['Id'] == args.template]) == 1
        craft, source_hash = b.imported('Pioneer + PDU'); b.add_control_if_missing(craft); b.append_rtg(craft); b.power_support(craft)
        b.emit(args.template, 'Duna power and heat rejection', craft, (-9,7,-7,7,13,identity), workers=1, trait='Engineer', source_hash=source_hash, purpose='power')
        for i, template in enumerate(catalog['Templates']):
            if template['Id'] == args.template: catalog['Templates'][i] = b.manifests[0]
        catalog_path.write_text(json.dumps(catalog,indent=2)+'\n',encoding='utf-8')
        print(json.dumps({'Id':args.template,'StaticValidation':b.manifests[0]['StaticValidation'],'NativeQualification':'pending'},indent=2)); return
    b.emit('housing-kpbs-v1','Compact KPBS resident home',b.compact('housing'),(-4,4,-8,8,6,yaw),homes=6,purpose='housing')
    b.emit('service-kpbs-v1','Workshop with inputs and service reserves',b.compact('service'),(-5,5,-9,9,7,yaw),workers=1,trait='Engineer',purpose='service')
    b.emit('storage-kpbs-v1','Trade and protected reserve storage',b.compact('storage'),(-5,5,-9,9,7,yaw),purpose='storage')
    b.emit('lamp-stock-v1','Sparse stock lamp mast',b.lamp(),(-2,2,-2,2,6,identity),purpose='lighting')
    for source,identifier,name,envelope,purpose,workers,trait in [
        ('Pioneer + PDU','power-duna-v1','Duna power and heat rejection',(-9,7,-7,7,13,identity),'power',1,'Engineer'),
        ('Hoppers','wolf-hoppers-v1','WOLF extraction hoppers with attached buffers',(-14,14,-23,15,10,identity),'wolf',0,''),
        ('Ag Support','fertilizer-tundra-v1','Gypsum fertilizer with input and machinery reserves',(-14,14,-18,8,10,identity),'fertilizer',1,'Engineer'),
        ('Agriculture','agriculture-duna-v1','Substrate agriculture with complete local buffers',(-5,5,-5,7,8,identity),'agriculture',1,'Scientist')]:
        craft,source_hash=b.imported(source);b.add_control_if_missing(craft);b.append_rtg(craft)
        if identifier == 'power-duna-v1': b.power_support(craft)
        b.emit(identifier,name,craft,envelope,workers=workers,trait=trait,source_hash=source_hash,purpose=purpose)
    catalog={'SchemaVersion':1,'PartConfigurationHash':b.cache_hash,'Status':'candidate packages; no runtime certification claimed','Templates':b.manifests}
    (b.out/'colony-template-catalog.json').write_text(json.dumps(catalog,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'status':'static validation passed; native certification pending','templates':[{'Id':x['Id'],'Parts':x['ExpectedPartCount'],'Mass':x['DryMassTonnes'],'BuildFunds':x['BuildFunds'],'CraftSha256':x['CraftSha256']} for x in b.manifests]},indent=2))


if __name__=='__main__':main()

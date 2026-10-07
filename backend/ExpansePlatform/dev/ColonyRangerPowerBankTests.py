"""Disconnected source/artifact/mutation checks. Never call KSP or install."""
from pathlib import Path
import argparse, copy, hashlib, json, unittest
import ColonyPlacementTemplates as t
import ColonyRangerPowerBank as bank
from ColonyRangerBankGeometry import Models

class Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.b=t.Builder(Path(args.install),Path(args.evidence)/'validator-scratch');cls.models=Models(cls.b,Path(args.mu_reader))
        cls.c=t.parse((Path(args.templates)/(bank.ID+'.craft')).read_text());cls.manifest=json.loads((Path(args.templates)/(bank.ID+'.manifest.json')).read_text())
    def check(self,c=None,m=None,models=None):return bank.validate(self.b,c or copy.deepcopy(self.c),models or self.models,m or copy.deepcopy(self.manifest))
    def reject(self,change):
        c=copy.deepcopy(self.c);m=copy.deepcopy(self.manifest);change(c,m);m['Hash']=bank.terms_hash(m)
        with self.assertRaises((AssertionError,ValueError,StopIteration,KeyError)):self.check(c,m)
    def test_source_candidate(self):self.assertEqual(len(self.check()['CenterOfMassCases']),16)
    def test_deterministic_source_geometry(self):
        c=bank.craft(self.b);self.b.native_part_keys(c);self.b.clean(c)
        self.assertEqual(t.serialize(c)+'\n',(Path(args.templates)/(bank.ID+'.craft')).read_text())
    def test_old_ten_files_preserved(self):
        for row in json.loads((Path(args.evidence)/'before/old-template-hashes.json').read_text(encoding='utf-8-sig')):
            self.assertEqual(hashlib.sha256((Path(args.templates)/row['Name']).read_bytes()).hexdigest().upper(),row['Sha256'])
    def test_old_catalog_entries_preserved(self):
        old=json.loads((Path(args.evidence)/'before/colony-template-catalog.json').read_text());new=json.loads((Path(args.templates)/'colony-template-catalog.json').read_text())
        self.assertEqual(old['Templates'],new['Templates'][:10]);self.assertEqual(old['PartConfigurationHash'],new['PartConfigurationHash'])
        self.assertEqual([self.b.cache_hash],new['AdditionalSourcePartCacheHashes'])
    def test_complete_offsets_required(self):self.reject(lambda c,m:t.children(c,'PART')[0]['v']['attN'].__setitem__(0,t.children(c,'PART')[0]['v']['attN'][0].replace('0.9|0|0','0.9|0',1)))
    def test_native_normal_required(self):self.reject(lambda c,m:t.children(c,'PART')[0]['v']['attN'].__setitem__(0,t.children(c,'PART')[0]['v']['attN'][0].replace('_1|0|0_','_0|1|0_',1)))
    def test_missing_reciprocal_node(self):self.reject(lambda c,m:t.children(c,'PART')[1]['v'].pop('attN'))
    def test_duplicate_node(self):self.reject(lambda c,m:t.children(c,'PART')[0]['v']['attN'].append(t.children(c,'PART')[0]['v']['attN'][0]))
    def test_floating_pack(self):self.reject(lambda c,m:t.setv(t.children(c,'PART')[1],'pos','3,0,0'))
    def test_duplicate_craft_identity(self):self.reject(lambda c,m:t.setv(t.children(c,'PART')[1],'part',t.val(t.children(c,'PART')[2],'part')))
    def test_disconnected_tree(self):self.reject(lambda c,m:t.children(c,'PART')[0]['v']['link'].pop())
    def test_resource_defaults(self):self.reject(lambda c,m:t.setv(t.children(t.children(c,'PART')[1],'RESOURCE')[0],'amount',20))
    def test_foreign_saved_crew(self):self.reject(lambda c,m:t.setv(t.children(c,'PART')[6],'crew','Someone'))
    def test_changed_generator_recipe(self):
        def change(c,m):
            mod=next(x for x in t.children(t.children(c,'PART')[1],'MODULE') if t.val(x,'name')=='USI_Converter');t.setv(t.children(mod,'OUTPUT_RESOURCE')[0],'Ratio',500)
        self.reject(change)
    def test_additional_mixed_reactor(self):self.reject(lambda c,m:t.children(c,'PART')[1]['c'].append(t.fresh('MODULE',name='ModuleSystemHeatFissionReactor')))
    def test_pack_not_surface_attachable(self):self.reject(lambda c,m:t.children(c,'PART')[1]['v'].update(srfN=['srfAttach,Ranger.AnchorHub_100,,0|0|0,0|0|1,0|0|0']))
    def test_rescaling_disallowed(self):
        def change(c,m):t.setv(next(x for x in t.children(t.children(c,'PART')[1],'MODULE') if t.val(x,'name')=='TweakScale'),'currentScale',5)
        self.reject(change)
    def test_no_inherited_two_unit_fuel(self):self.reject(lambda c,m:next(x for x in m['StartupContents'] if x['ResourceName']=='Plutonium-238').update(Amount=2000000))
    def test_duplicate_embedded_charge(self):self.reject(lambda c,m:m['EmbeddedContents'].append({'Resource':'Plutonium-238','Amount':80000000}))
    def test_no_self_certification(self):self.reject(lambda c,m:m.update(RuntimeCertified=True))
    def test_no_resident_homes(self):self.reject(lambda c,m:m.update(Homes=4))
    def test_no_default_planning_change(self):self.reject(lambda c,m:m.update(DefaultPlanningRole='power'))
    def test_no_replacement_certificate(self):self.reject(lambda c,m:m['OperatingLimits'].update(NativeReplacementServiceCertified=True))
    def test_no_missing_physical_foot(self):
        self.models.part('structuralPanel1');old=self.models.results['structuralPanel1'];self.models.results['structuralPanel1']=dict(old,Colliders=[])
        try:
            with self.assertRaises(AssertionError):self.check()
        finally:self.models.results['structuralPanel1']=old
    def test_no_missing_actual_airlock(self):
        self.models.part('crewCabin');old=self.models.results['crewCabin'];self.models.results['crewCabin']=dict(old,Airlocks=[])
        try:
            with self.assertRaises(AssertionError):self.check()
        finally:self.models.results['crewCabin']=old
    def test_raised_single_foot_is_not_coplanar(self):
        source=self.models
        class Changed:
            def world(_,p):
                row=copy.deepcopy(source.world(p))
                if row['CraftPartId']==111:row['Colliders'][0]['Bounds']['Min'][1]+=.03
                return row
        with self.assertRaisesRegex(AssertionError,'Noncoplanar'):self.check(models=Changed())
    def test_pack_spacer_collision_is_rejected(self):
        source=self.models
        class Changed:
            def world(_,p):
                row=copy.deepcopy(source.world(p))
                if row['CraftPartId']==101:row['Bounds']['Min'][0]-=.05
                return row
        with self.assertRaisesRegex(AssertionError,'obstruction'):self.check(models=Changed())
    def test_obstructed_airlock_corridor_is_rejected(self):
        source=self.models
        class Changed:
            def world(_,p):
                row=copy.deepcopy(source.world(p))
                if row['CraftPartId']==118:row['Bounds']={'Min':[-.5,2,-3],'Max':[.5,3,-2]}
                return row
        with self.assertRaisesRegex(AssertionError,'corridor blocked'):self.check(models=Changed())
    def test_center_of_mass_outside_support_holds(self):
        cfg=self.b.cfg('crewCabin');old=copy.deepcopy(cfg['v']);t.setv(cfg,'CoMOffset','30,0,0')
        try:
            with self.assertRaisesRegex(AssertionError,'CoM'):self.check()
        finally:cfg['v']=old
    def test_changed_source_part_hash(self):
        source=copy.deepcopy(self.b.cfg('Ranger_PowerPack'));source['c']=[x for x in source['c'] if not(x['tag']=='MODULE' and t.val(x,'name')=='ColonyPlacementMarker')]
        expected=next(x for x in self.manifest['RawParts'] if x['PartName']=='Ranger_PowerPack')['PartConfigSha256'];self.assertEqual(bank.digest(t.serialize(source).encode()),expected)
        t.setv(source,'mass',12345);self.assertNotEqual(bank.digest(t.serialize(source).encode()),expected)

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--install',required=True);p.add_argument('--templates',required=True);p.add_argument('--mu-reader',required=True);p.add_argument('--evidence',required=True);args=p.parse_args()
    result=unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(Tests));raise SystemExit(0 if result.wasSuccessful() else 1)

"""Focused artifact conservation/attachment regression tests, no KSP/save writes."""
from pathlib import Path
import copy, hashlib, importlib.util, json, unittest

spec=importlib.util.spec_from_file_location('templates',Path(__file__).with_name('ColonyPlacementTemplates.py'))
t=importlib.util.module_from_spec(spec);spec.loader.exec_module(t)
OUT=Path('ExpansePlatform/package/GameData/ExpanseWorldBridge/Templates')


class TemplateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.builder=t.Builder(Path('C:/Kerbal Space Program'),OUT)
        cls.catalog=json.loads((OUT/'colony-template-catalog.json').read_text())

    def test_current_craft_bytes_match_every_manifest(self):
        for manifest in self.catalog['Templates']:
            data=(OUT/manifest['CraftRelativePath']).read_bytes()
            self.assertEqual(manifest['CraftSha256'],hashlib.sha256(data).hexdigest())
            self.assertFalse(manifest['RuntimeCertified'])
            self.builder.validate(t.parse(data.decode()))

    def test_manifest_has_exact_once_paid_stock_and_matching_physical_tanks(self):
        for manifest in self.catalog['Templates']:
            capacities={(r['CraftPartId'],r['ResourceName']):r['Capacity'] for r in manifest['ResourceCapacities']}
            keys=set(); summed={}
            for row in manifest['StartupContents']:
                key=(row['CraftPartId'],row['ResourceName']);self.assertNotIn(key,keys);keys.add(key)
                self.assertIsInstance(row['Amount'],int);self.assertGreater(row['Amount'],0);self.assertLessEqual(row['Amount'],capacities[key])
                summed[row['ResourceName']]=summed.get(row['ResourceName'],0)+row['Amount']
            self.assertEqual(summed,{r['Resource']:r['Amount'] for r in manifest['EmbeddedContents']})

    def test_stale_surface_parent_regression_is_rejected(self):
        craft=t.parse((OUT/'power-duna-v1.craft').read_text());part=next(x for x in t.children(craft,'PART') if 'srfN' in x['v'])
        fields=part['v']['srfN'][0].split(',');fields[1]='Duna.PDU_4278339576';part['v']['srfN'][0]=','.join(fields)
        with self.assertRaises(AssertionError):self.builder.validate(craft)

    def test_missing_three_component_stack_offset_is_rejected(self):
        craft=t.parse((OUT/'housing-kpbs-v1.craft').read_text());part=t.children(craft,'PART')[0]
        fields=part['v']['attN'][0].rsplit('_',4);fields[1]='0|0';part['v']['attN'][0]='_'.join(fields)
        with self.assertRaises(AssertionError):self.builder.validate(craft)

    def test_moved_attached_wing_is_rejected(self):
        craft=t.parse((OUT/'housing-kpbs-v1.craft').read_text());part=t.children(craft,'PART')[1]
        xyz=t.vec(t.val(part,'pos'));xyz[0]+=1;t.setv(part,'pos',t.vstr(xyz))
        with self.assertRaises(AssertionError):self.builder.validate(craft)

    def test_nonzero_default_stock_is_rejected(self):
        craft=t.parse((OUT/'housing-kpbs-v1.craft').read_text());resource=t.children(t.children(craft,'PART')[0],'RESOURCE')[0]
        t.setv(resource,'amount',1)
        with self.assertRaises(AssertionError):self.builder.validate(craft)

    def test_surface_mount_forbidden_by_installed_part_rules_is_rejected(self):
        craft=t.parse((OUT/'lamp-stock-v1.craft').read_text());lamp=next(x for x in t.children(craft,'PART') if 'srfN' in x['v'])
        # Stock deployable ground lamp has no surface attachment permission. It cannot
        # be substituted for the certified-direction mast light in this topology.
        t.setv(lamp,'part','groundLight1_104')
        for parent in t.children(craft,'PART'):
            if 'link' in parent['v']:parent['v']['link']=[x.replace('spotLight3_104','groundLight1_104') for x in parent['v']['link']]
        with self.assertRaises(AssertionError):self.builder.validate(craft)

    def test_config_underscore_names_cannot_be_native_craft_names(self):
        craft=t.parse((OUT/'housing-kpbs-v1.craft').read_text());part=t.children(craft,'PART')[0]
        t.setv(part,'part','KKAOSS_Central_Hub_100')
        with self.assertRaises(AssertionError):self.builder.validate(craft)

    def test_saved_curve_rows_use_native_numeric_whitespace(self):
        for manifest in self.catalog['Templates']:
            craft=t.parse((OUT/manifest['CraftRelativePath']).read_text())
            for node in t.walk(craft):
                for row in node['v'].get('key', []):
                    # Stock FloatCurve.Load splits spaces/tabs, then Single.Parse.
                    # A literal escape token in the old RTG PassiveEnergy rows fails.
                    tokens=row.split();self.assertIn(len(tokens),(2,4));self.assertNotIn('\\t',row)
                    self.assertTrue(all(t.math.isfinite(float(x)) for x in tokens))
        with self.assertRaises(ValueError):float('\\t25000')

    def test_only_explicit_habitat_parts_are_proposed_homes(self):
        housing=next(m for m in self.catalog['Templates'] if m['Id']=='housing-kpbs-v1')
        self.assertEqual([101,103],housing['HomeCraftPartIds'])
        parts={p['CraftPartId']:p['PartName'] for p in housing['RawParts']}
        self.assertTrue(all(parts[i]=='KKAOSS_Habitat_MK2_g' for i in housing['HomeCraftPartIds']))


if __name__=='__main__':unittest.main(verbosity=2)

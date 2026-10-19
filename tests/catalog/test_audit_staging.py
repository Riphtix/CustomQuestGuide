import unittest
import csv
from pathlib import Path
import importlib.util
p=Path(__file__).resolve().parents[2] / 'tools/catalog/stage_from_quest_audit.py'
spec=importlib.util.spec_from_file_location('staging',p)
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
class TestStage(unittest.TestCase):
    def test_preserve_duplicate_names_separate_ids(self):
        rows=[{'ClassJob ID':'7','ClassJob':'thaumaturge','Quest ID':str(q),'Quest Name':'The Threat of Paucity','Class Level':'1','Quest Role':'Progression','Applicable':'True','CQG Quest State':'NotStarted'} for q in (65884,65885)]
        result=m.stage(rows,7)
        self.assertEqual([r['questId'] for r in result],['65884','65885'])
        self.assertTrue(all(r['reviewStatus']=='candidate' and not r['referenceUrl'] for r in result))
    def test_no_wrong_class(self):
        rows=[{'ClassJob ID':'7','ClassJob':'thaumaturge','Quest ID':'65884','Quest Name':'Q','Class Level':'1','Quest Role':'Progression','Applicable':'True','CQG Quest State':'NotStarted'}]
        self.assertEqual(m.stage(rows,26),[])
if __name__=='__main__':unittest.main()

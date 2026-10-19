import json
import pathlib
import unittest

ROOT=pathlib.Path(__file__).resolve().parents[2]
DOC=json.loads((ROOT/'Data/Verified/job-progression.json').read_text())
JOBS={j['classJobId']:j for j in DOC['jobs']}

def path(job, first):
    return [qid for stage in JOBS[job]['stages'] for qid in
        stage.get('always', []) + stage.get('startingClass' if first==job else 'laterClass', [])]

class ExpandedClassCatalogTests(unittest.TestCase):
    def test_coverage(self):
        self.assertEqual(set(JOBS), {1,5,7,29})
        self.assertTrue(all(j['verifiedComplete'] for j in JOBS.values()))
    def test_rogue(self):
        self.assertEqual(path(29,1), [65640,65646,65662,65680,65681,65682,65684,65690,65691])
        self.assertNotIn(65748, path(29,1)) # Ninja unlock not Rogue progression
    def test_archer_variants(self):
        self.assertEqual(path(5,1),[65755,65582,65670,65604,65606,65607,65612])
        self.assertEqual(path(5,5),[65582,65603,65604,65606,65607,65612])
    def test_gladiator_variants(self):
        self.assertEqual(path(1,1),[65792,65797,65798,65799,65800,65801])
        self.assertEqual(path(1,5),[65822,65792,65824,65798,65799,65800,65801])
    def test_no_duplicate_or_zero_ids(self):
        for id in JOBS:
            for first in (1,id):
                ids=path(id,first)
                self.assertEqual(len(ids),len(set(ids)))
                self.assertTrue(all(i>0 for i in ids))

if __name__=='__main__': unittest.main()

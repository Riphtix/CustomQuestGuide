import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]

class RuntimeCatalogSchema(unittest.TestCase):
    def test_thaumaturge_variants(self):
        doc = json.loads((ROOT/'Data/Verified/job-progression.json').read_text())
        self.assertEqual(doc['schemaVersion'], 2)
        thm = next(x for x in doc['jobs'] if x['classJobId'] == 7)
        self.assertTrue(thm['verifiedComplete'])
        def path(is_start):
            return [q for s in thm['stages'] for q in
                    s.get('always', []) + s.get('startingClass' if is_start else 'laterClass', [])]
        self.assertEqual(path(True), [65883,65884,65886,65887,65888,65889])
        self.assertEqual(path(False), [65882,65883,65885,65886,65887,65888,65889])
        self.assertEqual(len(set(path(False))), len(path(False)))

if __name__ == '__main__': unittest.main()

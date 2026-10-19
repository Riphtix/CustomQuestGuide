import unittest
from pathlib import Path
import importlib.util

p = Path(__file__).resolve().parents[2] / 'tools/catalog/build_verified_catalog.py'
spec = importlib.util.spec_from_file_location('builder', p)
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)

def row(job, quest, order, status='verified'):
    return dict(classJobId=str(job), questId=str(quest), order=str(order),
                referenceUrl='https://na.finalfantasyxiv.com/', reviewedBy='reviewer', reviewStatus=status)

class TestCompiler(unittest.TestCase):
    def test_valid_order(self):
        result = m.compile_rows([row(7, 301, 2), row(7, 300, 1)])
        self.assertEqual(result['jobs'][0]['progressionQuestIds'], [300, 301])
    def test_unreviewed_fails_closed(self):
        with self.assertRaises(ValueError): m.compile_rows([row(7, 300, 1, 'candidate')])
    def test_duplicates_rejected(self):
        with self.assertRaises(ValueError): m.compile_rows([row(7, 300, 1), row(7, 300, 2)])
    def test_gaps_rejected(self):
        with self.assertRaises(ValueError): m.compile_rows([row(7, 300, 2)])
    def test_unproven_url_rejected(self):
        r = row(7, 300, 1); r['referenceUrl'] = ''
        with self.assertRaises(ValueError): m.compile_rows([r])

if __name__ == '__main__': unittest.main()

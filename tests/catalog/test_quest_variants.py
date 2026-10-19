import importlib.util, unittest
from pathlib import Path
spec = importlib.util.spec_from_file_location('variants', Path(__file__).resolve().parents[2] / 'tools/catalog/validate_quest_variants.py')
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
class Variants(unittest.TestCase):
    def test_staged(self):
        import csv
        path = Path(__file__).resolve().parents[2] / 'tools/catalog/thaumaturge-variant-review.csv'
        with path.open(newline='') as f: rows=list(csv.DictReader(f))
        self.assertEqual(m.validate(rows), [])
    def test_conflicting_alternatives(self):
        base={'classJobId':'7','questId':'1','order':'1','kind':'alternative','variantCondition':'starting_class','referenceUrl':'https://example.com'}
        other={**base,'questId':'2'}
        self.assertTrue(m.validate([base,other]))
    def test_missing_source(self):
        self.assertTrue(m.validate([{'classJobId':'7','questId':'1','order':'1','kind':'progression','referenceUrl':''}]))
if __name__ == '__main__': unittest.main()

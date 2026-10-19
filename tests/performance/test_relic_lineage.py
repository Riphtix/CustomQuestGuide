"""Structural guards: shared, conservative inference for evolving relic sets."""
from pathlib import Path
import unittest
root = Path(__file__).parents[2]
helper = (root/'Domain'/'RelicStageLineage.cs').read_text()
ui = (root/'UI'/'Plugin.TargetManagement.cs').read_text()
class RelicLineageGuards(unittest.TestCase):
    def test_supported_upgrade_families(self):
        for family in ('relic-phantom', 'relic-zodiac', 'relic-anima', 'relic-eureka',
                       'relic-resistance', 'relic-manderville', 'tool-skysteel', 'tool-splendorous'):
            self.assertIn(f'["{family}"]', helper)
    def test_later_stage_only_same_weapon(self):
        self.assertIn('grouped[identity]', helper)
        self.assertIn('entry.Stage <= highest', helper)
        self.assertIn('physicallyOwned.Contains(entry.Id)', helper)
        self.assertIn('group.Count() > 1', helper)
        self.assertIn('name.Contains("Replica"', helper)
    def test_shared_relic_progress(self):
        self.assertIn('RelicStageLineage.InferAttainedStages(set.Id, members, owned)', ui)
        self.assertIn('DrawFilteredRelicGuidance(TargetSet set', ui)
        self.assertIn('var attainedStages = GetEffectivelyOwnedSetItems(set, members);', ui)
        self.assertIn('members.Length == applicableIds.Length', ui)
if __name__ == '__main__': unittest.main()

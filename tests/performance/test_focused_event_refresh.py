"""Guard the event-to-Focused-Tracking refresh connection."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]

class FocusedEventRefreshTests(unittest.TestCase):
    def test_quest_unlock_job_and_territory_events_invalidate_guidance(self):
        code = (ROOT / 'UI/Plugin.PinnedCompletion.cs').read_text()
        self.assertIn('private void QueuePinnedGuidanceRefresh()', code)
        self.assertIn('nextFocusedGuidanceRefreshUtc = DateTime.UtcNow.AddMilliseconds(300);', code)
        for token in ['private void QueuePinnedUnlockVerification()',
                      'private void QueueJobCompletionCheck(uint classJobId)',
                      'private void OnPinnedTerritoryChanged(uint territoryId)']:
            body = code.split(token, 1)[1].split('\n    }', 1)[0]
            self.assertIn('QueuePinnedGuidanceRefresh();', body, token)
        self.assertIn('=> QueuePinnedUnlockVerification();', code)

    def test_refresh_is_consumed_by_snapshot_not_drawing(self):
        code = (ROOT / 'UI/Plugin.FocusedTracking.cs').read_text()
        self.assertIn('now >= nextFocusedGuidanceRefreshUtc', code)
        self.assertIn('RefreshFocusedTargetStates(focused);', code)
        self.assertIn('focusedDisplaySnapshots[key] = GetPinnedUiDerived(pin);', code)
        self.assertIn('focusedDisplaySnapshots.TryGetValue(key, out var derived)', code)

if __name__ == '__main__':
    unittest.main()

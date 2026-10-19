from pathlib import Path
import re

s = (Path(__file__).resolve().parents[2] / 'UI' / 'Plugin.FocusedTracking.cs').read_text()
render = s.split('            foreach (var pin in focused)', 2)[-1]
# The refresh loop builds snapshots; the draw loop only reads them.
render = s[s.index('        var edit = config.FocusedTrackingEditMode;'):s.index('    /// <summary>')]
assert 'GetPinnedUiDerived(' not in render
assert 'ResolveFocusedGameIcon(' not in render
assert 'GetCachedFocusedGameIcon(' not in render
assert 'RefreshFocusedTargetStates(' not in render
assert 'focusedDisplaySnapshots.TryGetValue' in render
assert 'focusedDisplayIcons.TryGetValue' in render
assert s.count('RefreshFocusedTargetStates(focused);') == 1
assert 'nextFocusedGuidanceRefreshUtc = now.Add(PinnedUiRefreshInterval);' in s
print('Focused render-separation checks passed')

using System;
using System.Collections.Generic;

namespace CustomQuestGuide;

/// <summary>
/// Optional hand-curated prerequisite guides for targets where "where it drops"
/// is not enough.
///
/// Generic items do NOT require a curated guide. They can still appear in the
/// catalog and resolve activity sources. Curated guides are only added when a
/// target needs a real prerequisite chain.
/// </summary>
public static class CuratedGuides
{
    public static CuratedGuide? ForTarget(TargetEntry target)
    {
        // First-stage Endwalker relic weapons use the Manderville prerequisite
        // chain. Keep this narrow so later Amazing/Majestic/Mandervillous stages
        // are not accidentally described as the first stage.
        if (target.Category == "Weapons" &&
            target.Name.StartsWith("Manderville ", StringComparison.OrdinalIgnoreCase))
        {
            return EndwalkerMandervilleFirstStage;
        }

        return null;
    }

    public static readonly CuratedGuide EndwalkerMandervilleFirstStage =
        new(
            "Endwalker Manderville Relic Weapon",
            [
                new GuideChain(
                    "Hildibrand Adventures — A Realm Reborn",
                    "A Realm Reborn Hildibrand prerequisite chain.",
                    [
                        new("The Rise and Fall of Gentlemen", "Wymond", "Ul'dah - Steps of Nald", 9.8f, 8.7f),
                        new("Back in the Saddle", "Hildibrand", "Southern Thanalan", 21.5f, 39.1f),
                        new("After Her Own Heart", "Wymond", "Ul'dah - Steps of Nald", 9.8f, 8.7f),
                        new("The Immaculate Deception", "Ellie", "Ul'dah - Steps of Nald", 8.3f, 11.1f),
                        new("The Science of Deduction", "Ellie", "Eastern Thanalan", 10.9f, 16.4f),
                        new("The Hammer", "Hildibrand", "Western Thanalan", 20.1f, 24.6f),
                        new("Manderville Men", "Hildibrand", "Northern Thanalan", 23.6f, 23.9f),
                        new("The Three Collectors", "Hildibrand", "Western Thanalan", 13.1f, 14.1f),
                        new("The Business of Betrothal", "Ellie", "Western Thanalan", 13.0f, 14.1f),
                        new("A Burst of Inspiration", "Briardien", "Eastern La Noscea", 32.0f, 32.6f),
                        new("Seeds of Rebellion", "Tiny Trader", "Western La Noscea", 27.7f, 28.3f),
                        new("A Case of Indecency", "Hildibrand", "Eastern La Noscea", 20.5f, 21.6f),
                        new("Eight-armed and Dangerous", "Ellie", "Eastern La Noscea", 32.0f, 30.4f),
                        new("What Price Victory", "Hildibrand", "Ul'dah - Steps of Nald", 8.3f, 11.1f),
                        new("The Trouble with Truffles", "Hildibrand", "Ul'dah - Steps of Nald", 8.3f, 11.1f),
                        new("The Coliseum Conundrum", "Briardien", "Ul'dah - Steps of Thal", 14.0f, 10.0f),
                        new("Shades of Sil'dih", "Ellie", "Ul'dah - Steps of Thal", 9.8f, 11.2f),
                        new("Sibling Strife", "Hildibrand", "Western Thanalan", 28.7f, 24.9f),
                        new("Beneath the Mask", "Hildibrand", "Ul'dah - Steps of Thal", 12.0f, 11.7f),
                        new("Truths Untold", "Hyuran woman", "Ul'dah - Steps of Thal", 12.0f, 11.8f),
                        new("Her Last Vow", "Julyan", "Ul'dah - Steps of Thal", 12.0f, 11.8f),
                    ]),

                new GuideChain(
                    "Further Hildibrand Adventures — Heavensward",
                    "Heavensward Hildibrand prerequisite chain.",
                    [
                        new("A Gentleman Falls, Rather than Flies", "Nashu Mhakaracca", "The Pillars", 5.9f, 9.9f),
                        new("Don't Call It a Comeback", "Cyr", "Coerthas Western Highlands", 18.1f, 10.9f),
                        new("The Gigi Situation", "Cyr", "Foundation", 9.9f, 11.4f),
                        new("The Measure of a Mammet", "Hildibrand", "The Pillars", 6.0f, 9.9f),
                        new("A Gazebo to Call Our Own", "Cyr", "Foundation", 9.9f, 11.4f),
                        new("Don't Trust Anyone over Sixty", "Hildibrand", "Idyllshire", 6.3f, 7.8f),
                        new("The Proud and the Pointy-eyed", "Cyr", "Foundation", 9.9f, 11.4f),
                        new("If I Could Turn Back Time", "Hildibrand", "The Dravanian Forelands", 20.3f, 22.1f),
                    ]),

                new GuideChain(
                    "Even Further Hildibrand Adventures — Stormblood",
                    "Stormblood Hildibrand prerequisite chain.",
                    [
                        new("A Hingan Tale: Nashu Goes East", "Conspicuously Inconspicuous Man", "Kugane", 10.6f, 9.8f),
                        new("Life Imitates Art Imitates Life", "Nashu Mhakaracca", "Kugane", 11.7f, 8.8f),
                        new("Of Wolves and Gentlemen", "Shigure", "The Ruby Sea", 38.1f, 38.1f),
                        new("In the Eye of the Hingan", "Shigure", "Kugane", 13.2f, 13.3f),
                        new("The Blade Mislaid", "Shigure", "Kugane", 10.0f, 7.8f),
                        new("The Black Heart Beneath", "Shigure", "Kugane", 12.5f, 8.1f),
                        new("Good Swords, Good Dogs", "Hildibrand", "Kugane", 12.1f, 5.9f),
                        new("The Past Is Never Past", "Nashu Mhakaracca", "Kugane", 10.5f, 8.1f),
                        new("Don't Do the Dewprism", "Hildibrand", "Kugane", 11.7f, 8.8f),
                    ]),

                new GuideChain(
                    "Somehow Further Hildibrand Adventures — Endwalker",
                    "Endwalker Hildibrand quests required for the first Manderville weapon.",
                    [
                        new("The Sleeping Gentleman", "Excitable Youth", "Radz-at-Han", 11.8f, 11.2f),
                        new("A Soulful Reunion", "Hildibrand", "Lakeland", 25.8f, 26.0f),
                        new("Lunar Conspiracy", "Nashu Mhakaracca", "Radz-at-Han", 11.8f, 11.2f),
                        new("The Imperfect Gentleman", "Hildibrand", "Mare Lamentorum", 27.9f, 35.7f),
                    ]),

                new GuideChain(
                    "Manderville Weapon Unlock",
                    "Unlock and receive the first Manderville weapon.",
                    [
                        new(
                            "Make It a Manderville",
                            "House Manderville Manservant",
                            "Radz-at-Han",
                            11.8f,
                            11.2f,
                            "Requires a level 90 Disciple of War or Magic and 3 Manderium Meteorites."),
                    ]),
            ]);
}

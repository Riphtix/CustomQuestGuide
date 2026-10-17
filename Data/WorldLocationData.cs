using System;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace CustomQuestGuide;

// ===== MapCoordinateConverter.cs =====
/// <summary>
/// Converts FFXIV world-space X/Z positions into the player-facing X/Y
/// coordinates printed by the in-game map.
///
/// Source providers must not guess whether a world position is already a map
/// coordinate. Normalize it once at the provider boundary, retain the original
/// world position for precise map-link opening, and let presentation code consume
/// only the normalized values.
/// </summary>
public static class MapCoordinateConverter
{
    /// <summary>
    /// Converts a world-space X/Z pair for <paramref name="territoryTypeId"/>
    /// into the corresponding map coordinates.
    /// </summary>
    public static bool TryWorldToMap(
        IDataManager data,
        uint territoryTypeId,
        float worldX,
        float worldZ,
        out uint mapId,
        out float mapX,
        out float mapY)
    {
        mapId = 0;
        mapX = 0;
        mapY = 0;

        if (territoryTypeId == 0 || !float.IsFinite(worldX) || !float.IsFinite(worldZ))
            return false;

        try
        {
            var territory = data.GetExcelSheet<TerritoryType>().GetRow(territoryTypeId);
            mapId = ReflectionUtil.GetNestedRowId(territory, "Map");
            if (mapId == 0)
                return false;

            var map = data.GetExcelSheet<Map>().GetRow(mapId);
            var scale = map.SizeFactor / 100f;
            if (scale <= 0f)
                return false;

            // This is the same world -> displayed-map transform used by Dalamud's
            // MapLinkPayload. World X maps to map X; world Z maps to map Y.
            mapX = WorldAxisToMapCoordinate(worldX, map.OffsetX, scale);
            mapY = WorldAxisToMapCoordinate(worldZ, map.OffsetY, scale);

            return float.IsFinite(mapX) && float.IsFinite(mapY) &&
                   mapX >= 0f && mapX <= 60f && mapY >= 0f && mapY <= 60f;
        }
        catch
        {
            mapId = 0;
            mapX = 0;
            mapY = 0;
            return false;
        }
    }


    /// <summary>
    /// Converts player-facing map X/Y coordinates back into world-space X/Z for
    /// <paramref name="territoryTypeId"/>. This is useful when a supplemental
    /// provider supplies already-normalized map coordinates but Dalamud's map-link
    /// API needs a world position together with the territory/map identity.
    /// </summary>
    public static bool TryMapToWorld(
        IDataManager data,
        uint territoryTypeId,
        float mapX,
        float mapY,
        out uint mapId,
        out float worldX,
        out float worldZ)
    {
        mapId = 0;
        worldX = 0;
        worldZ = 0;

        if (territoryTypeId == 0 || !float.IsFinite(mapX) || !float.IsFinite(mapY))
            return false;

        try
        {
            var territory = data.GetExcelSheet<TerritoryType>().GetRow(territoryTypeId);
            mapId = ReflectionUtil.GetNestedRowId(territory, "Map");
            if (mapId == 0)
                return false;

            var map = data.GetExcelSheet<Map>().GetRow(mapId);
            var scale = map.SizeFactor / 100f;
            if (scale <= 0f)
                return false;

            worldX = MapCoordinateToWorldAxis(mapX, map.OffsetX, scale);
            worldZ = MapCoordinateToWorldAxis(mapY, map.OffsetY, scale);

            return float.IsFinite(worldX) && float.IsFinite(worldZ);
        }
        catch
        {
            mapId = 0;
            worldX = 0;
            worldZ = 0;
            return false;
        }
    }

    private static float MapCoordinateToWorldAxis(float mapCoordinate, float offset, float scale)
    {
        var scaledPosition = ((mapCoordinate - 1f) * scale / 41f * 2048f) - 1024f;
        return (scaledPosition / scale) - offset;
    }

    private static float WorldAxisToMapCoordinate(float worldPosition, float offset, float scale)
    {
        var scaledPosition = (worldPosition + offset) * scale;
        return (41f / scale * ((scaledPosition + 1024f) / 2048f)) + 1f;
    }
}

// ===== MobSpawnCoordinateOverrides.cs =====
internal enum MobSpawnCoordinateDisposition
{
    Normal = 0,
    Unverified = 1,
    Suppress = 2,
}

/// <summary>
/// Explicit compatibility classifications for the LuminaSupplemental MobSpawn snapshot
/// audited by CQG. Runtime code does not infer placeholder coordinates heuristically.
/// Re-run the Debug MobSpawn audit after an upstream data change, review candidates,
/// then regenerate/update this table in a CQG release.
/// </summary>
internal static class MobSpawnCoordinateOverrides
{
    public const string SourcePackageVersion = "5.1.5";
    public const string SourceDataFingerprintSha256 = "A4930B862715D1356B6961E2DE6C454268198E0E45582A102FEA7467B1BF5B31";

    private sealed record Entry(uint TerritoryTypeId, float X, float Y, float Z, MobSpawnCoordinateDisposition Disposition);

    private static readonly Entry[] Entries =
    [
        new(134u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(135u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(137u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(138u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(139u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(140u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(141u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(142u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(145u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(146u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(147u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(148u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(152u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(153u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(154u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(155u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(156u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(180u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(190u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(191u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(192u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(214u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(215u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(216u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(219u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(220u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(221u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(222u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(223u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(225u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(226u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(227u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(228u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(229u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(230u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(231u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(232u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(233u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(234u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(235u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(236u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(237u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(239u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(240u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(248u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(249u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(255u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(256u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(257u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(258u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(260u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(261u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(263u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(264u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(265u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(266u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(268u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(269u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(270u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(271u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(272u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(273u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(275u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(277u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(279u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(280u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(285u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(287u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(289u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(290u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(291u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(298u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(299u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(300u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(301u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(302u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(303u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(304u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(305u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(306u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(307u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(308u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(309u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(310u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(311u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(312u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(313u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(314u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(315u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(316u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(317u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(318u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(319u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(320u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(321u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(322u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(323u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(324u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(325u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(326u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(327u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(328u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(330u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(368u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(379u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(394u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(405u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(406u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(408u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(410u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(411u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(412u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(413u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(414u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(415u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(453u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(454u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(465u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(466u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(468u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(471u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(473u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(475u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(483u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(484u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(486u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(487u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(488u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(491u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(494u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(495u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(496u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(500u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(533u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(552u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(558u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(612u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(613u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(614u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(620u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(621u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(622u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(634u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(640u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(647u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(648u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(657u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(668u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(669u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(670u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(671u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(672u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(675u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(678u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(684u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(685u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(686u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(687u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(688u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(699u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(701u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(703u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(711u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(712u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(713u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(716u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(718u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(723u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(725u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(726u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(732u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(757u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(760u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(763u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(781u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(795u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(797u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(813u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(814u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(815u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(816u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(817u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(818u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(838u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(859u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(860u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(861u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(862u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(864u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(867u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(868u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(869u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(870u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(871u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(872u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(874u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(875u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(876u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(879u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(893u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(911u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(932u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(939u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(956u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(957u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(958u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(959u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(960u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(961u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(975u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(977u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1000u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1010u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1011u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1012u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1014u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1015u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1049u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1120u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1187u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1188u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1192u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1212u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1236u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1246u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1252u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1273u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1310u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1313u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1319u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1346u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(130u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(133u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(151u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(159u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(160u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(167u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(174u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(188u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(189u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(193u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(194u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(195u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(238u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(241u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(242u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(243u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(244u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(245u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(251u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(259u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(329u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(349u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(350u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(355u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(356u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(357u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(358u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(360u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(361u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(362u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(363u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(365u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(367u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(369u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(372u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(380u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(381u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(382u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(383u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(387u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(404u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(409u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(420u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(443u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(449u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(451u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(474u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(490u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(499u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(508u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(510u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(520u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(529u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(532u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(557u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(586u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(627u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(664u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(665u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(676u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(690u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(700u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(704u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(706u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(714u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(721u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(742u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(788u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(830u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(837u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(840u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(865u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(884u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(898u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1016u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1020u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1021u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1036u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1038u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1039u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1041u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1042u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1043u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1044u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1062u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1063u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1066u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1068u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1069u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1109u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1111u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1114u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1142u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1143u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1144u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1173u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1174u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1193u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1215u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1245u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1267u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1303u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1330u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1331u, 11.24f, 11.24f, -2.0f, MobSpawnCoordinateDisposition.Suppress),
        new(397u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(398u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(399u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(400u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(401u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(402u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(455u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(459u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(461u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(464u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(467u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(470u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(472u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(476u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(477u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(479u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(481u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(482u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(485u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(489u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(492u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(493u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(497u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(498u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(501u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(503u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(709u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(715u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(866u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(1023u, 22.557896f, 22.557896f, -0.95f, MobSpawnCoordinateDisposition.Suppress),
        new(561u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(562u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(563u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(564u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(565u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(587u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(593u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(594u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(595u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(596u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(597u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(598u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(599u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(600u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(601u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(602u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(603u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(604u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(605u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(606u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(607u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(770u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(771u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(772u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(773u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(774u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(775u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(782u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(783u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(784u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(785u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1044u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1101u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1104u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1105u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1107u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1281u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1283u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1284u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1288u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1289u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(1290u, 21.48f, 21.48f, -1.0f, MobSpawnCoordinateDisposition.Suppress),
        new(777u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(794u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(796u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(801u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(802u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(803u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(804u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(806u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(811u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(824u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(845u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(853u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(854u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(858u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(873u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(885u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(887u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(897u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(907u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(908u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(912u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(924u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(935u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(946u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(948u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(968u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(996u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(998u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1003u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1005u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1007u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1082u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1084u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1086u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1096u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1122u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1141u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1258u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1262u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1270u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1279u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1293u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1300u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1307u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1322u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1325u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1357u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1362u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1363u, 4.12f, 4.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(778u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(779u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(794u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(796u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(798u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(799u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(800u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(801u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(805u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(806u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(824u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(825u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(845u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(846u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(847u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(848u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(849u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(853u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(885u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(887u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(904u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(905u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(908u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(909u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(924u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(934u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(935u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(944u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(948u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(950u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(992u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(993u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(995u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(996u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(997u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1009u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1071u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1072u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1081u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1095u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1123u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1200u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1201u, 6.1f, 6.1f, 100.0f, MobSpawnCoordinateDisposition.Unverified),
        new(561u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(562u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(563u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(564u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(565u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(593u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(594u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(595u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(596u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(597u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(598u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(599u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(600u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(601u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(602u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(603u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(604u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(605u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(606u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(607u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(770u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(771u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(772u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(773u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(774u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(775u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(782u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(783u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(784u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(791u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1099u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1100u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1101u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1102u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1103u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1104u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1105u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1106u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1107u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1108u, 21.4f, 21.4f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(779u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(800u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(801u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(805u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(845u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(846u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(848u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(852u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(856u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(885u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(902u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(903u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(906u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(907u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(922u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(923u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(942u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(943u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(946u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(947u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(995u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(996u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1002u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1006u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1008u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1071u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1072u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1083u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1140u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1231u, 6.1f, 5.9f, 90.0f, MobSpawnCoordinateDisposition.Unverified),
        new(288u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(294u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(297u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(348u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(377u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(407u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(446u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(524u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(566u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(684u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(695u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(696u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(697u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(698u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(720u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(730u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(733u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(748u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(752u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(753u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(754u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1045u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1372u, 6.12f, 6.12f, -4.0f, MobSpawnCoordinateDisposition.Unverified),
        new(295u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(374u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(375u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(426u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(436u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(437u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(447u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(448u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(637u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(638u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(679u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(698u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(730u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(746u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(751u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(755u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(758u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(954u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1032u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1045u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1067u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1116u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1138u, 6.1f, 6.1f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(794u, 6.1f, 6.1f, 99.99231f, MobSpawnCoordinateDisposition.Unverified),
        new(924u, 6.1f, 6.1f, 99.99231f, MobSpawnCoordinateDisposition.Unverified),
        new(934u, 6.1f, 6.1f, 99.99231f, MobSpawnCoordinateDisposition.Unverified),
        new(950u, 6.1f, 6.1f, 99.99231f, MobSpawnCoordinateDisposition.Unverified),
        new(951u, 6.1f, 6.1f, 99.99231f, MobSpawnCoordinateDisposition.Unverified),
        new(1072u, 6.1f, 6.1f, 99.99231f, MobSpawnCoordinateDisposition.Unverified),
        new(160u, 11.2f, 11.2f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(167u, 11.2f, 11.2f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(196u, 11.2f, 11.2f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(452u, 11.2f, 11.2f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(508u, 11.2f, 11.2f, 0.0f, MobSpawnCoordinateDisposition.Unverified),
        new(403u, 6.24f, 13.799999f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1146u, 6.24f, 13.799999f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1217u, 6.24f, 13.799999f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
        new(521u, 11.24f, 14.24f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
        new(530u, 11.24f, 14.24f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
        new(933u, 11.24f, 14.24f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
        new(560u, 10.88f, 10.88f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
        new(1110u, 10.88f, 10.88f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
        new(749u, 6.0f, 6.0f, -1.458081f, MobSpawnCoordinateDisposition.Unverified),
        new(753u, 6.0f, 6.0f, -1.458081f, MobSpawnCoordinateDisposition.Unverified),
        new(297u, 6.1f, 6.1f, -0.06342204f, MobSpawnCoordinateDisposition.Unverified),
        new(1047u, 6.1f, 6.1f, -0.06342204f, MobSpawnCoordinateDisposition.Unverified),
        new(358u, 11.2f, 11.2f, 0.006120025f, MobSpawnCoordinateDisposition.Unverified),
        new(383u, 11.2f, 11.2f, 0.006120025f, MobSpawnCoordinateDisposition.Unverified),
        new(373u, 7.826667f, 7.826667f, -3.0f, MobSpawnCoordinateDisposition.Unverified),
        new(949u, 6.1f, 6.1f, -75.0f, MobSpawnCoordinateDisposition.Unverified),
        new(952u, 10.24f, 10.24f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
        new(966u, 25.24f, 25.24f, -2.0f, MobSpawnCoordinateDisposition.Unverified),
    ];

    public static MobSpawnCoordinateDisposition Classify(uint territoryTypeId, float x, float y, float? z)
    {
        if (territoryTypeId == 0 || !z.HasValue)
            return MobSpawnCoordinateDisposition.Normal;

        foreach (var entry in Entries)
        {
            if (entry.TerritoryTypeId == territoryTypeId &&
                NearlyEqual(entry.X, x) &&
                NearlyEqual(entry.Y, y) &&
                NearlyEqual(entry.Z, z.Value))
            {
                return entry.Disposition;
            }
        }

        return MobSpawnCoordinateDisposition.Normal;
    }

    public static bool IsExplicitlyClassified(uint territoryTypeId, float x, float y, float? z) =>
        Classify(territoryTypeId, x, y, z) != MobSpawnCoordinateDisposition.Normal;

    private static bool NearlyEqual(float left, float right, float epsilon = 0.0005f) =>
        MathF.Abs(left - right) <= epsilon;
}

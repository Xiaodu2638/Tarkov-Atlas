using TarkovAtlas.Models;

namespace TarkovAtlas.Services;

/// <summary>
/// Chooses which building level the player is standing on from a capture's
/// world position.
///
/// Height alone is not enough. On Customs the outdoor ground runs up to roughly
/// Y 2.7 and "2nd Floor" starts exactly there, so a hill at Y 5 used to be read
/// as a first-floor building. Reserve is worse: "Bunkers" covers -10000..18 and
/// the synthetic ground entry covers -1000..18, so the ground entry (order 0)
/// always won the overlap and the underground never showed.
///
/// Upstream ships each level as one or more <em>extents</em>: a Y band plus the
/// axis-aligned footprints it covers. Requiring the player's XZ to be inside a
/// footprint is what separates "on the hill outside Dorms" from "inside Dorms".
/// </summary>
public static class FloorPicker
{
    /// <summary>Slack for comparing height bands, matching float noise in the data.</summary>
    private const double Epsilon = 0.001;

    /// <summary>How far above the base layer's top a whole-map band must start
    /// before it is trusted on a map that has footprints. Keeps authoritative
    /// upper decks usable while ignoring bands that merely restate "not the
    /// ground" -- Shoreline ships "3rd Floor: 2..1000" with no footprint at all,
    /// and trusting that would swallow the whole surface.</summary>
    private const double BaseMargin = 0.5;

    /// <summary>How convincing a candidate is. A footprint hit is proof the
    /// player is inside that building, so it outranks everything.</summary>
    private enum Match
    {
        /// <summary>A band that covers the whole map, with no footprint to test.</summary>
        WholeMap = 0,
        /// <summary>A band whose footprint contains the player.</summary>
        Footprint = 1,
    }

    /// <summary>
    /// Pick the level for a player at world <paramref name="x"/>/<paramref name="z"/>
    /// with height <paramref name="y"/>. Falls back to the map's own artwork when
    /// no level claims the position, and returns null only when there are no
    /// layers at all.
    /// </summary>
    public static MapLayer? Select(IReadOnlyList<MapLayer> layers, double x, double z, double y)
    {
        if (layers.Count == 0) return null;

        MapLayer? baseLayer = null;
        foreach (var layer in layers)
        {
            // An empty image is the map's own artwork: the outdoor ground, and
            // the fallback for every position no building claims.
            if (string.IsNullOrEmpty(layer.Image)) { baseLayer = layer; break; }
        }

        // On a map with no footprints at all (older data, or a map upstream has
        // not described yet) a whole-map band is the only kind there is, so it
        // must be trusted; otherwise every answer would be the base artwork.
        var hasFootprints = HasFootprints(layers, baseLayer);

        MapLayer? best = null;
        var bestMatch = Match.WholeMap;
        var hasBest = false;
        double bestLo = double.MinValue, bestSpan = double.MaxValue;

        foreach (var layer in layers)
        {
            // The base level is never a candidate here: it has no extents, and
            // its envelope spans the whole map, so it would beat every real deck
            // on the tie-break. Falling back to it is the final step instead.
            if (ReferenceEquals(layer, baseLayer)) continue;

            // Data written before extents existed has only the envelope. Treat
            // it as one map-wide band so old maps keep switching floors.
            if (layer.Extents.Count == 0)
            {
                if (!Covers(layer.MinHeight, layer.MaxHeight, y)) continue;
                Consider(layer, Match.WholeMap,
                         layer.MinHeight ?? double.MinValue,
                         Span(layer.MinHeight, layer.MaxHeight),
                         ref best, ref hasBest, ref bestMatch, ref bestLo, ref bestSpan);
                continue;
            }

            foreach (var extent in layer.Extents)
            {
                if (!Covers(extent.MinHeight, extent.MaxHeight, y)) continue;

                var lo = extent.MinHeight ?? double.MinValue;
                var span = Span(extent.MinHeight, extent.MaxHeight);

                if (extent.IsWholeMap)
                {
                    if (hasFootprints && !TrustsWholeMapBand(extent, baseLayer)) continue;
                    Consider(layer, Match.WholeMap, lo, span,
                             ref best, ref hasBest, ref bestMatch, ref bestLo, ref bestSpan);
                }
                else
                {
                    if (!Contains(extent, x, z)) continue;
                    Consider(layer, Match.Footprint, lo, span,
                             ref best, ref hasBest, ref bestMatch, ref bestLo, ref bestSpan);
                }
            }
        }

        return best ?? baseLayer;
    }

    private static bool HasFootprints(IReadOnlyList<MapLayer> layers, MapLayer? baseLayer)
    {
        foreach (var layer in layers)
        {
            if (ReferenceEquals(layer, baseLayer)) continue;
            foreach (var extent in layer.Extents)
            {
                if (!extent.IsWholeMap) return true;
            }
        }
        return false;
    }

    /// <summary>True when <paramref name="y"/> falls inside the band; a missing
    /// bound means "unbounded on that side".</summary>
    private static bool Covers(double? lo, double? hi, double y) =>
        (lo is null || y >= lo.Value - Epsilon) &&
        (hi is null || y <= hi.Value + Epsilon);

    private static bool Contains(LayerExtent extent, double x, double z)
    {
        foreach (var rect in extent.Bounds)
        {
            if (rect.Length < 4) continue;
            var minX = Math.Min(rect[0], rect[2]);
            var maxX = Math.Max(rect[0], rect[2]);
            var minZ = Math.Min(rect[1], rect[3]);
            var maxZ = Math.Max(rect[1], rect[3]);
            if (x >= minX && x <= maxX && z >= minZ && z <= maxZ) return true;
        }
        return false;
    }

    private static double Span(double? lo, double? hi) =>
        lo is null || hi is null ? double.MaxValue : hi.Value - lo.Value;

    /// <summary>
    /// A map-wide band carries no footprint, so on a map that <em>does</em> have
    /// footprints it is only believable when it is unambiguous about being a
    /// genuinely separate deck: entirely below the outdoor ground, or starting
    /// clearly above it.
    /// </summary>
    private static bool TrustsWholeMapBand(LayerExtent extent, MapLayer? baseLayer)
    {
        if (baseLayer?.MinHeight is not { } baseLo) return true;
        if (extent.MaxHeight is { } hi && hi <= baseLo + Epsilon) return true;
        if (baseLayer.MaxHeight is not { } baseHi) return false;
        return extent.MinHeight is { } lo && lo >= baseHi + BaseMargin;
    }

    /// <summary>
    /// Stronger match kind wins; then the higher band (decks stack upward); then
    /// the narrower band, which is the more specific answer.
    /// </summary>
    private static void Consider(
        MapLayer layer, Match match, double lo, double span,
        ref MapLayer? best, ref bool hasBest, ref Match bestMatch,
        ref double bestLo, ref double bestSpan)
    {
        if (hasBest)
        {
            if (match < bestMatch) return;
            if (match == bestMatch)
            {
                if (lo < bestLo - Epsilon) return;
                if (Math.Abs(lo - bestLo) <= Epsilon && span >= bestSpan) return;
            }
        }

        best = layer;
        bestMatch = match;
        bestLo = lo;
        bestSpan = span;
        hasBest = true;
    }
}

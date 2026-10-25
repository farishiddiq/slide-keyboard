using SlideKeyboard.Core.Dictionaries;
using SlideKeyboard.Core.Geometry;

namespace SlideKeyboard.Core.Swipe;

/// <summary>
/// Precomputed swipe templates for one language pack on one key geometry, bucketed by (first key, last key)
/// so recognition only scores words whose ends are near the gesture's ends.
/// </summary>
public sealed class SwipeIndex
{
    internal sealed class Item
    {
        public Item(WordEntry entry, char[] keys, Vec2[] points)
        {
            Entry = entry;
            Keys = keys;
            Points = points;
            Length = PathMath.Length(points);
        }

        public WordEntry Entry { get; }
        public char[] Keys { get; }

        /// <summary>Ideal path: the key centers in order.</summary>
        public Vec2[] Points { get; }

        public double Length { get; }

        /// <summary>Lazily computed resampled template (benign race: every thread computes the same value).</summary>
        public Vec2[]? Resampled;
    }

    readonly Dictionary<(char, char), List<Item>> _byEnds = new();

    SwipeIndex(LanguagePack pack, SwipeKeyMap map)
    {
        Pack = pack;
        Map = map;
    }

    public LanguagePack Pack { get; }
    public SwipeKeyMap Map { get; }
    public int Count { get; private set; }

    public static SwipeIndex Build(LanguagePack pack, SwipeKeyMap map)
    {
        var index = new SwipeIndex(pack, map);
        foreach (var entry in pack.Words.Entries)
        {
            var keys = map.KeySequence(entry.Word);
            if (keys == null) continue;
            var points = new Vec2[keys.Length];
            for (int i = 0; i < keys.Length; i++) points[i] = map.Center(keys[i]);

            var bucket = (keys[0], keys[^1]);
            if (!index._byEnds.TryGetValue(bucket, out var list))
                index._byEnds[bucket] = list = new List<Item>();
            list.Add(new Item(entry, keys, points));
            index.Count++;
        }
        return index;
    }

    internal IReadOnlyList<Item> Get(char first, char last) =>
        _byEnds.TryGetValue((first, last), out var list) ? list : Array.Empty<Item>();
}

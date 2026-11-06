using SanctionAndForgive.Matching.Names;

namespace SanctionAndForgive.Matching.Text;

/// <summary>Finds the fold keys in a vocabulary within two edits of a key: the candidates the token comparer may
/// align by edits. Two lookups cover section 5.4's edit rules. Short keys (4-7 letters, where a single edit leaves
/// few trigrams in common: <c>marta</c> and <c>matra</c> share none) use a delete-one index: two keys one edit apart
/// (a transposition included) share a key with one letter deleted. Longer keys, where two edits are allowed, use
/// trigrams. Every candidate is then checked by edit distance. Compact: the delete keys are hashes in one sorted
/// array, and a collision costs only one extra check.</summary>
internal sealed class FuzzyVocabulary
{
    private const int ShortMax = 7;

    private readonly string[] folds;
    private readonly long[] deleteKeys;
    private readonly Dictionary<string, int[]> byTrigram;

    public FuzzyVocabulary(IEnumerable<string> vocabulary)
    {
        folds = [.. vocabulary.Where(fold => fold.Length >= 4).Distinct(StringComparer.Ordinal)];
        var keys = new List<long>();
        var trigrams = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var id = 0; id < folds.Length; id++)
        {
            var fold = folds[id];
            if (fold.Length <= ShortMax + 1)
            {
                foreach (var variant in DeleteVariants(fold))
                {
                    keys.Add(((long)Hash(variant) << 32) | (uint)id);
                }
            }

            foreach (var trigram in Trigrams(fold).Distinct())
            {
                if (!trigrams.TryGetValue(trigram, out var list))
                {
                    trigrams[trigram] = list = [];
                }

                list.Add(id);
            }
        }

        keys.Sort();
        deleteKeys = [.. keys];
        byTrigram = trigrams.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Vocabulary keys other than <paramref name="fold"/> within two edits of it.</summary>
    public IEnumerable<string> Near(string fold)
    {
        if (fold.Length < 4)
        {
            return [];
        }

        var ids = new HashSet<int>();
        if (fold.Length <= ShortMax + 1)
        {
            foreach (var variant in DeleteVariants(fold))
            {
                var hash = (long)Hash(variant) << 32;
                var index = Array.BinarySearch(deleteKeys, hash);
                for (index = index < 0 ? ~index : index; index < deleteKeys.Length && (deleteKeys[index] & ~0xFFFFFFFFL) == hash; index++)
                {
                    ids.Add((int)(deleteKeys[index] & 0xFFFFFFFFL));
                }
            }
        }

        foreach (var trigram in Trigrams(fold).Distinct())
        {
            ids.UnionWith(byTrigram.GetValueOrDefault(trigram) ?? []);
        }

        return ids.Select(id => folds[id]).Where(other => other != fold && EditDistance.DamerauLevenshtein(fold, other, 3) <= 2);
    }

    /// <summary>The key itself and every key with one letter deleted.</summary>
    private static IEnumerable<string> DeleteVariants(string fold)
    {
        yield return fold;
        for (var i = 0; i < fold.Length; i++)
        {
            yield return fold.Remove(i, 1);
        }
    }

    /// <summary>A stable hash (FNV-1a), the same in every process.</summary>
    private static int Hash(string value)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in value)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }
    }

    private static IEnumerable<string> Trigrams(string value)
    {
        for (var i = 0; i + 3 <= value.Length; i++)
        {
            yield return value.Substring(i, 3);
        }
    }
}

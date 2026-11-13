using System.Text;

namespace Capacitor.Cli.Core.Text;

/// <summary>
/// Canonical Unicode normalization (NFC and NFD) that works under <c>InvariantGlobalization</c>,
/// where <c>string.Normalize</c> returns its input unchanged and
/// <c>string.IsNormalized</c> answers <c>true</c> for any non-ASCII text.
/// A lone surrogate passes through as a starter with no decomposition.
/// </summary>
public static class UnicodeNormalization {
    public static string ToNfd(string s) => IsAscii(s) ? s : Encode(Decompose(s));

    public static string ToNfc(string s) => IsAscii(s) ? s : Encode(Compose(Decompose(s)));

    public static bool IsNfc(string s) => IsAscii(s) || string.Equals(ToNfc(s), s, StringComparison.Ordinal);

    static bool IsAscii(string s) => System.Text.Ascii.IsValid(s);

    const int SBase = 0xAC00, LBase = 0x1100, VBase = 0x1161, TBase = 0x11A7;
    const int LCount = 19, VCount = 21, TCount = 28, NCount = VCount * TCount, SCount = LCount * NCount;

    static readonly Dictionary<long, int> Compositions = BuildCompositions();

    static Dictionary<long, int> BuildCompositions() {
        var firsts  = UnicodeNormalizationData.CompositionFirsts;
        var seconds = UnicodeNormalizationData.CompositionSeconds;
        var results = UnicodeNormalizationData.Composites;
        var map     = new Dictionary<long, int>(results.Length);
        for (var i = 0; i < results.Length; i++) map[Pair(firsts[i], seconds[i])] = results[i];
        return map;
    }

    static long Pair(int first, int second) => ((long)first << 21) | (uint)second;

    static int CombiningClass(int cp) {
        if (cp < 0x300) return 0;
        var i = UnicodeNormalizationData.CombiningClassCodePoints.BinarySearch(cp);
        return i < 0 ? 0 : UnicodeNormalizationData.CombiningClasses[i];
    }

    static List<int> Decompose(string s) {
        var result = new List<int>(s.Length + 8);
        for (var i = 0; i < s.Length; i++) {
            int cp = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                cp = char.ConvertToUtf32(s[i], s[++i]);
            AppendDecomposed(result, cp);
        }
        return result;
    }

    static void AppendDecomposed(List<int> result, int cp) {
        var s = cp - SBase;
        if (s is >= 0 and < SCount) {
            result.Add(LBase + s / NCount);
            result.Add(VBase + s % NCount / TCount);
            if (s % TCount != 0) result.Add(TBase + s % TCount);
            return;
        }

        var i = cp < 0xC0 ? -1 : UnicodeNormalizationData.DecompositionCodePoints.BinarySearch(cp);
        if (i < 0) {
            AppendOrdered(result, cp);
            return;
        }

        var offsets = UnicodeNormalizationData.DecompositionOffsets;
        foreach (var part in UnicodeNormalizationData.Decompositions[offsets[i]..offsets[i + 1]])
            AppendOrdered(result, part);
    }

    /// <summary>Canonical ordering: a mark moves back past every mark of a higher class, never past a
    /// starter, and marks of equal class keep their order.</summary>
    static void AppendOrdered(List<int> result, int cp) {
        var cc = CombiningClass(cp);
        var at = result.Count;
        if (cc != 0)
            while (at > 0 && CombiningClass(result[at - 1]) > cc) at--;
        result.Insert(at, cp);
    }

    static List<int> Compose(List<int> cps) {
        if (cps.Count == 0) return cps;

        var result     = new List<int>(cps.Count) { cps[0] };
        var starterPos = 0;
        var lastClass  = CombiningClass(cps[0]) == 0 ? 0 : 256;

        for (var i = 1; i < cps.Count; i++) {
            var cp = cps[i];
            var cc = CombiningClass(cp);
            if ((lastClass == 0 || lastClass < cc) && TryCompose(result[starterPos], cp, out var composite)) {
                result[starterPos] = composite;
                continue;
            }
            if (cc == 0) starterPos = result.Count;
            lastClass = cc;
            result.Add(cp);
        }
        return result;
    }

    static bool TryCompose(int first, int second, out int composite) {
        var l = first - LBase;
        var v = second - VBase;
        if (l is >= 0 and < LCount && v is >= 0 and < VCount) {
            composite = SBase + (l * VCount + v) * TCount;
            return true;
        }

        var s = first - SBase;
        var t = second - TBase;
        if (s is >= 0 and < SCount && s % TCount == 0 && t is > 0 and < TCount) {
            composite = first + t;
            return true;
        }

        return Compositions.TryGetValue(Pair(first, second), out composite);
    }

    static string Encode(List<int> cps) {
        var sb = new StringBuilder(cps.Count);
        foreach (var cp in cps) {
            if (cp > 0xFFFF) sb.Append(char.ConvertFromUtf32(cp));
            else sb.Append((char)cp);
        }
        return sb.ToString();
    }
}

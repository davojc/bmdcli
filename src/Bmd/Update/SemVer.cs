namespace Bmd.Update;

/// <summary>A semantic version, parsed from a release tag (<c>v1.2.0</c>) or from the
/// build-stamped version string (<c>1.2.0</c>, or <c>0.0.0-dev</c> for a local build).
/// Ordering follows the semver 2.0.0 precedence rules, which is what makes
/// <c>0.1.0-rc.1 &lt; 0.1.0</c> come out right — the pre-release tags this project publishes
/// must not read as newer than the release they precede.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string PreRelease)
    : IComparable<SemVer>
{
    // Null-safe even though every string produced by TryParse is non-null: a plain `default(SemVer)`
    // (a failed TryParse, or an uninitialized field elsewhere) leaves PreRelease null, and this type
    // should not throw just for being asked its precedence status.
    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    /// <summary>Parses a version, tolerating a leading <c>v</c>/<c>V</c> (git tags carry one)
    /// and surrounding whitespace. Build metadata (<c>+…</c>) is discarded: semver says it takes
    /// no part in precedence, so keeping it would make two equal versions compare unequal.</summary>
    public static bool TryParse(string? raw, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();
        if (text[0] is 'v' or 'V') text = text[1..];

        var plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        var pre = "";
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
            if (pre.Length == 0) return false;
            foreach (var part in pre.Split('.'))
            {
                if (part.Length == 0) return false;
                foreach (var c in part)
                    if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
                // semver 2.0.0: numeric identifiers MUST NOT include leading zeroes. A bare "0"
                // is fine; alphanumeric identifiers (e.g. "0abc") aren't numeric so are unaffected.
                if (IsNumeric(part) && part.Length > 1 && part[0] == '0') return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        if (!TryNumber(parts[0], out var major)) return false;
        if (!TryNumber(parts[1], out var minor)) return false;
        if (!TryNumber(parts[2], out var patch)) return false;

        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    /// <summary>Digits only, then <see cref="int.TryParse(string?, out int)"/>. The digit check
    /// rejects the signs and whitespace TryParse would otherwise accept; TryParse then rejects
    /// anything too large for an int. Also enforces the semver 2.0.0 rule that numeric identifiers
    /// (major/minor/patch are numeric identifiers too) MUST NOT include leading zeroes — "0" alone
    /// is fine, "01" is not.</summary>
    static bool TryNumber(string part, out int value)
    {
        value = 0;
        if (part.Length == 0) return false;
        if (part.Length > 1 && part[0] == '0') return false;
        foreach (var c in part)
            if (!char.IsAsciiDigit(c)) return false;
        return int.TryParse(part, out value);
    }

    public int CompareTo(SemVer other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        // Null-safe like IsPreRelease/ToString: default(SemVer) leaves PreRelease null, and a
        // null pre-release means the same thing as an empty one — this is not a pre-release.
        var pre = PreRelease ?? "";
        var otherPre = other.PreRelease ?? "";
        if (pre == otherPre) return 0;
        // "1.2.3" is newer than "1.2.3-rc.1": a version without a pre-release suffix wins.
        if (pre.Length == 0) return 1;
        if (otherPre.Length == 0) return -1;
        return ComparePreRelease(pre, otherPre);
    }

    /// <summary>Semver pre-release precedence: compare dot-separated identifiers left to right;
    /// all-numeric identifiers compare numerically and rank below alphanumeric ones; if every
    /// shared identifier is equal, the longer set wins.</summary>
    static int ComparePreRelease(string left, string right)
    {
        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = IsNumeric(a[i]);
            var bNumeric = IsNumeric(b[i]);
            int result;
            if (aNumeric && bNumeric)
            {
                // Compared as digit strings rather than parsed ints: a pre-release identifier
                // has no length limit, so parsing could overflow. With leading zeros stripped,
                // "longer number is larger, else ordinal" is exactly numeric ordering.
                var x = a[i].TrimStart('0');
                var y = b[i].TrimStart('0');
                result = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            }
            else if (aNumeric) result = -1;
            else if (bNumeric) result = 1;
            else result = string.CompareOrdinal(a[i], b[i]);

            if (result != 0) return Math.Sign(result);
        }
        return a.Length.CompareTo(b.Length);
    }

    static bool IsNumeric(string part)
    {
        foreach (var c in part)
            if (!char.IsAsciiDigit(c)) return false;
        return part.Length > 0;
    }

    public override string ToString() =>
        string.IsNullOrEmpty(PreRelease) ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

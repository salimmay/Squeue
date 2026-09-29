using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Squeue.Core.Copy;

/// Short random names for temp files: "~tq" + 10 base32 characters + ".tmp" (17 characters).
/// Short enough for any filesystem's name-length limit; random so they never collide or tunnel.
public static partial class TempNames
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string New()
    {
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);
        Span<char> chars = stackalloc char[10];
        for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[random[i] & 31];
        return string.Concat("~tq", chars, ".tmp");
    }

    /// Shape only. Ownership is always decided by the journal and the file id, never by the name.
    public static bool HasTempShape(string fileName) => Shape().IsMatch(fileName);

    [GeneratedRegex("^~tq[a-z2-7]{10}\\.tmp\\z")]
    private static partial Regex Shape();
}

using System.Security.Cryptography;
using System.Text;

namespace Aether.Sdk;

/// <summary>
/// Pure, offline helpers for the Connections
/// API's connect-session redirect signature. No network calls; safe to run
/// in whatever request handler the application's backend uses for the
/// OAuth callback.
/// </summary>
public static class AetherConnections
{
    /// <summary>
    /// Verify a <see cref="AetherClient.CreateConnectSessionAsync"/> redirect's
    /// signature (docs/SDK_API_CONTRACT.md §4.18).
    /// </summary>
    /// <remarks>
    /// <paramref name="clientSecret"/> is the value returned exactly once by
    /// <see cref="AetherClient.CreateConnectSessionAsync"/>. The comparison
    /// is constant-time (<see cref="CryptographicOperations.FixedTimeEquals"/>)
    /// so this method itself never becomes a timing oracle on the signature.
    /// <para>
    /// Returns <c>true</c> iff <paramref name="sig"/> matches the recomputed
    /// signature:
    /// <code>
    /// sig = hex(HMAC-SHA256(
    ///         key = SHA-256(clientSecret),
    ///         message = "&lt;session&gt;|&lt;status&gt;|&lt;connectionId&gt;"))
    /// </code>
    /// </para>
    /// </remarks>
    public static bool VerifyRedirectSignature(
        string clientSecret,
        string session,
        string status,
        string connectionId,
        string sig)
    {
        byte[] key;
        using (var sha256 = SHA256.Create())
        {
            key = sha256.ComputeHash(Encoding.UTF8.GetBytes(clientSecret));
        }
        var message = Encoding.UTF8.GetBytes($"{session}|{status}|{connectionId}");
        byte[] expected;
        using (var hmac = new HMACSHA256(key))
        {
            expected = hmac.ComputeHash(message);
        }

        byte[]? actual = TryParseHex(sig);
        if (actual == null || expected.Length != actual.Length)
            return false;
        return FixedTimeEquals(expected, actual);
    }

    // netstandard2.0 has neither Convert.FromHexString nor
    // CryptographicOperations.FixedTimeEquals, so both are hand-rolled here
    // rather than gated behind a TFM-specific implementation.
    private static byte[]? TryParseHex(string s)
    {
        if (s.Length % 2 != 0)
            return null;
        var bytes = new byte[s.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(s.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }

    private static bool FixedTimeEquals(byte[] a, byte[] b)
    {
        // Same length is already required by the caller; this still walks
        // every byte regardless of an early mismatch, so timing does not
        // leak which byte differed.
        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }
        return diff == 0;
    }
}

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace frcastr.Infrastructure.Data;

/// <summary>
/// Encrypts a column at rest with ASP.NET Data Protection, transparently to everything that reads
/// it. Applied to <c>DataSource.Config</c>, which holds the MQTT broker password and every upstream
/// API key — a single choke point, so the adapters, the MQTT service and the ingest key lookup all
/// keep seeing plaintext and none of them had to change.
/// </summary>
/// <remarks>
/// Values written before this existed are stored in the clear. They are recognised by the absence
/// of <see cref="Marker"/> and passed through unchanged, so an upgrade reads its own history; the
/// startup pass in Program.cs then rewrites them encrypted. Keep the pass-through until every
/// deployment has run that at least once.
/// </remarks>
public sealed class ProtectedStringConverter : ValueConverter<string?, string?>
{
    /// <summary>
    /// Prefix identifying a protected value. Explicit rather than inferred: telling ciphertext from
    /// a plaintext JSON config by shape alone is guesswork, and guessing wrong either corrupts the
    /// value or throws on perfectly good data.
    /// </summary>
    public const string Marker = "enc:v1:";

    public ProtectedStringConverter(IDataProtector protector)
        : base(v => Protect(v, protector), v => Unprotect(v, protector))
    { }

    private static string? Protect(string? plaintext, IDataProtector protector)
        => plaintext is null ? null : Marker + protector.Protect(plaintext);

    private static string? Unprotect(string? stored, IDataProtector protector)
    {
        if (stored is null) return null;
        if (!stored.StartsWith(Marker, StringComparison.Ordinal)) return stored;

        try
        {
            return protector.Unprotect(stored[Marker.Length..]);
        }
        catch (CryptographicException ex)
        {
            // The key ring that encrypted this is gone or unreadable. Failing loudly is the point:
            // returning null here would present the source as having no config, and the next save
            // would write that emptiness over a secret that is merely unreadable, not lost.
            throw new InvalidOperationException(
                "A stored data source secret could not be decrypted. This normally means the " +
                "Data Protection key ring in the app's data-protection-keys folder was replaced " +
                "or deleted — restore it from backup. Deleting the affected data source's Config " +
                "and re-entering its credentials is the only other way forward.", ex);
        }
    }
}

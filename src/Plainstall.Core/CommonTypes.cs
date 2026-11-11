using System.Security.Cryptography;

namespace Plainstall.Core;

/// <summary>Progress of a download or copy. <see cref="BytesTotal" /> is null while the size is unknown.</summary>
public readonly record struct TransferProgress(long BytesDone, long? BytesTotal);

/// <summary>
///     Calls the handler on the thread that reports. <see cref="Progress{T}" /> posts to a captured
///     context instead, so its reports can arrive late or out of order.
/// </summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value)
    {
        handler(value);
    }
}

public static class HashHex
{
    /// <summary>SHA-256 of a file as 64 upper-case hex characters.</summary>
    public static async Task<string> Sha256OfFileAsync(string path, IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long done = 0, total = stream.Length;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            done += read;
            progress?.Report(new TransferProgress(done, total));
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static string Sha256Of(ReadOnlySpan<byte> data)
    {
        return Convert.ToHexString(SHA256.HashData(data));
    }

    /// <summary>True when <paramref name="value" /> is exactly 64 hex characters.</summary>
    public static bool IsSha256(string? value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    public static bool Equal(string? a, string? b)
    {
        return a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
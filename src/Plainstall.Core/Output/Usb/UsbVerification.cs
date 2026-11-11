using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Plainstall.Core.Output.Usb;

/// <summary>Reading back what was written to the stick.</summary>
internal static class UsbVerification
{
    /// <summary>
    ///     <c>FILE_FLAG_NO_BUFFERING</c>. A file read back right after writing comes from Windows' memory cache,
    ///     which proves nothing about the stick; with this flag the read goes to the device.
    /// </summary>
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <summary>Unbuffered reads need buffers and offsets aligned to the sector size; 1 MiB is a multiple of any sector size.</summary>
    private const int ChunkBytes = 1024 * 1024;

    private const int Alignment = 4096;

    /// <summary>
    ///     SHA-256 of a file as 64 upper-case hex characters, read past the file cache. Falls back to a normal
    ///     read when the file system refuses unbuffered access; the result is then still a correct hash, only
    ///     possibly of cached data. <paramref name="readBypassedCache" /> says which happened.
    /// </summary>
    public static string Sha256Unbuffered(string path, CancellationToken cancellationToken, out bool readBypassedCache)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                NoBuffering | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            readBypassedCache = false;
            return HashBuffered(path, cancellationToken);
        }

        using (handle)
        {
            readBypassedCache = true;
            return HashUnbuffered(handle, cancellationToken);
        }
    }

    private static unsafe string HashUnbuffered(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = (byte*)NativeMemory.AlignedAlloc(ChunkBytes, Alignment);
        try
        {
            var span = new Span<byte>(buffer, ChunkBytes);
            long offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A full-size aligned read is allowed at the end of the file; it simply returns fewer bytes.
                var read = RandomAccess.Read(handle, span, offset);
                if (read <= 0) break;
                sha.AppendData(span[..read]);
                offset += read;
                if (read < ChunkBytes) break;
            }

            return Convert.ToHexString(sha.GetHashAndReset());
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    private static string HashBuffered(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes,
            FileOptions.SequentialScan);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[ChunkBytes];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>Part number and part count from the header of one <c>.swm</c> file; null when it is not a WIM header.</summary>
    /// <remarks>
    ///     Only the fixed 208-byte header is read, not the image description, because a split part may not
    ///     carry a description of its own. Offsets: signature at 0, part number at 40, part count at 42.
    /// </remarks>
    public static (int PartNumber, int TotalParts)? ReadSplitHeader(string path)
    {
        Span<byte> header = stackalloc byte[208];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
        if (stream.Length < header.Length) return null;
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual("MSWIM\0\0\0"u8)) return null;
        return (BinaryPrimitives.ReadUInt16LittleEndian(header[40..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[42..]));
    }
}
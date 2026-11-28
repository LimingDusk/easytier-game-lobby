using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameLobby;

internal sealed record UpdateBlock(int Size, string Sha256);

internal sealed record UpdateBlockManifest(int FormatVersion, string Tag, long Size, string Sha256, IReadOnlyList<UpdateBlock> Blocks)
{
    private const int Format = 1;
    private const int MinimumBlockSize = 64 * 1024;
    private const ulong BoundaryMask = 256 * 1024 - 1;
    internal const int MaximumBlockSize = 1024 * 1024;
    internal const int MaximumManifestSize = 1024 * 1024;
    private static readonly ulong[] Gear = CreateGearTable();

    internal static UpdateBlockManifest Create(Stream file, string tag)
    {
        if (!ReleaseVersion.TryParse(tag, out _))
            throw new ArgumentException("The manifest tag must be a release version.", nameof(tag));

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var blocks = ReadBlocks(file, sha256, CancellationToken.None);
        if (blocks.Count == 0) throw new InvalidDataException("The release file is empty.");
        return new(Format, tag, blocks.Sum(block => (long)block.Size), Convert.ToHexString(sha256.GetHashAndReset()), blocks);
    }

    internal static IReadOnlyList<UpdateBlock> Scan(Stream stream, CancellationToken cancellationToken = default) =>
        ReadBlocks(stream, null, cancellationToken);

    internal static UpdateBlockManifest? TryRead(byte[] json, UpdatePackage package)
    {
        if (json.Length == 0 || json.Length > MaximumManifestSize) return null;
        UpdateBlockManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateBlockManifest>(json); }
        catch (JsonException) { return null; }

        if (manifest is null || manifest.FormatVersion != Format || manifest.Tag != package.Tag ||
            manifest.Size != package.Size || manifest.Size is <= 0 or > UpdatePackage.MaximumSize ||
            !string.Equals(manifest.Sha256, package.Sha256, StringComparison.OrdinalIgnoreCase) ||
            manifest.Blocks is not { Count: > 0 } blocks || blocks.Count > manifest.Size / MinimumBlockSize + 1)
            return null;

        long length = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            int minimum = i == blocks.Count - 1 ? 1 : MinimumBlockSize;
            if (blocks[i] is not { } block || block.Size < minimum || block.Size > MaximumBlockSize ||
                !UpdatePackage.ValidDigest(block.Sha256))
                return null;
            length += block.Size;
        }

        return length == manifest.Size ? manifest : null;
    }

    private static List<UpdateBlock> ReadBlocks(Stream stream, IncrementalHash? fileHash, CancellationToken cancellationToken)
    {
        var blocks = new List<UpdateBlock>();
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[MinimumBlockSize];
        ulong rolling = 0;
        int blockSize = 0;
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = stream.Read(buffer);
            if (count == 0) break;
            total += count;
            if (total > UpdatePackage.MaximumSize) throw new InvalidDataException("The release file exceeds the update size limit.");
            fileHash?.AppendData(buffer, 0, count);

            int start = 0;
            for (int i = 0; i < count; i++)
            {
                rolling = unchecked((rolling << 1) + Gear[buffer[i]]);
                blockSize++;
                if (blockSize < MinimumBlockSize || ((rolling & BoundaryMask) != 0 && blockSize < MaximumBlockSize)) continue;

                sha256.AppendData(buffer, start, i - start + 1);
                blocks.Add(new(blockSize, Convert.ToHexString(sha256.GetHashAndReset())));
                start = i + 1;
                rolling = 0;
                blockSize = 0;
            }
            if (start < count) sha256.AppendData(buffer, start, count - start);
        }
        if (blockSize > 0) blocks.Add(new(blockSize, Convert.ToHexString(sha256.GetHashAndReset())));
        return blocks;
    }

    private static ulong[] CreateGearTable()
    {
        // A fixed SplitMix64 sequence makes content boundaries identical on every build host.
        var table = new ulong[256];
        ulong state = 0;
        for (int i = 0; i < table.Length; i++)
        {
            ulong value = unchecked(state += 0x9E3779B97F4A7C15UL);
            value = unchecked((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL);
            value = unchecked((value ^ (value >> 27)) * 0x94D049BB133111EBUL);
            table[i] = value ^ (value >> 31);
        }
        return table;
    }
}

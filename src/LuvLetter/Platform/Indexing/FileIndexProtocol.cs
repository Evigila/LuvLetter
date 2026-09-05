using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace LuvLetter.Platform.Indexing;

internal enum FileIndexMessageType : ushort
{
    Hello = 1,
    HelloAck = 2,
    ConfigureRoots = 3,
    Query = 4,
    QueryResult = 5,
    Status = 6,
    Shutdown = 7,
    Error = 8,
    Refresh = 9,
    Reconcile = 10,
}

internal readonly record struct FileIndexFrame(
    FileIndexMessageType Type,
    ulong RequestId,
    byte[] Payload);

internal readonly record struct FileIndexStatus(
    ulong IndexGeneration,
    FileIndexActivity Activity,
    FileIndexWorkStage Stage,
    byte? ProgressPercent,
    bool ProgressIsEstimated,
    ulong DiscoveredEntries)
{
    internal bool Rebuilding => Activity is FileIndexActivity.InitialBuild or FileIndexActivity.Updating;
}

internal enum FileIndexWorkStage : byte
{
    Idle = 0,
    Recovering = 1,
    Scanning = 2,
    Packing = 3,
    Compacting = 4,
    Persisting = 5,
}

internal enum FileIndexActivity : byte
{
    Ready = 0,
    InitialBuild = 1,
    Updating = 2,
    Failed = 3,
}

internal static class FileIndexProtocol
{
    internal const uint Magic = 0x58494C4C;
    internal const ushort MajorVersion = 8;
    internal const int HeaderSize = 20;
    internal const int MaximumPayloadLength = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] ConfigureRootsPayload(
        IReadOnlyList<FileIndexPartitionDescriptor> partitions,
        FileIndexMaintenanceOptions maintenance)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        writer.Write(checked((uint)partitions.Count));
        foreach (var partition in partitions)
        {
            WriteString(writer, partition.Id);
            WriteString(writer, partition.Root);
            writer.Write((uint)partition.Tier);
            writer.Write(checked((uint)partition.RefreshAgeSeconds));
            writer.Write(checked((uint)partition.AutomaticGapSeconds));
            writer.Write(checked((uint)partition.DelegatedSubtrees.Length));
            foreach (var delegated in partition.DelegatedSubtrees) WriteString(writer, delegated);
        }

        writer.Write(checked((uint)maintenance.TriggerCooldownSeconds));
        var ignored = maintenance.NormalizedIgnoreDirectories();
        writer.Write(checked((uint)ignored.Length));
        foreach (var directory in ignored)
        {
            WriteString(writer, directory);
        }

        var ignoredNames = maintenance.NormalizedIgnoreDirectoryNames();
        writer.Write(checked((uint)ignoredNames.Length));
        foreach (var name in ignoredNames)
        {
            WriteString(writer, name);
        }

        var fullIgnorePaths = maintenance.NormalizedFullIgnorePaths();
        writer.Write(checked((uint)fullIgnorePaths.Length));
        foreach (var path in fullIgnorePaths)
        {
            WriteString(writer, path);
        }

        return FinishPayload(stream);
    }

    internal static byte[] QueryPayload(ulong revision, int maximumResults, string query)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        writer.Write(revision);
        writer.Write(checked((uint)maximumResults));
        WriteString(writer, query);
        return FinishPayload(stream);
    }

    internal static async ValueTask WriteFrameAsync(
        Stream stream,
        FileIndexMessageType type,
        ulong requestId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaximumPayloadLength)
        {
            throw new InvalidDataException("The file-index payload exceeds 1 MiB.");
        }

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12, 8), requestId);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<FileIndexFrame> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2)) != MajorVersion)
        {
            throw new InvalidDataException("The file-index protocol header is incompatible.");
        }

        var rawType = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
        if (!Enum.IsDefined((FileIndexMessageType)rawType))
        {
            throw new InvalidDataException("The file-index frame type is invalid.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        if (payloadLength > MaximumPayloadLength)
        {
            throw new InvalidDataException("The file-index payload exceeds 1 MiB.");
        }

        var requestId = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12, 8));
        var payload = new byte[payloadLength];
        if (payload.Length > 0)
        {
            await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        }

        return new FileIndexFrame((FileIndexMessageType)rawType, requestId, payload);
    }

    internal static IReadOnlyList<Core.Application.FileIndexMatch> ParseQueryResult(
        byte[] payload,
        ulong expectedRevision,
        int maximumResults)
    {
        var reader = new PayloadReader(payload);
        if (reader.ReadUInt64() != expectedRevision)
        {
            throw new InvalidDataException("The indexer returned a stale editor revision.");
        }

        var count = reader.ReadUInt32();
        if (count > payload.Length / 20U)
        {
            throw new InvalidDataException("The indexer result count is invalid.");
        }

        var results = new List<Core.Application.FileIndexMatch>(
            Math.Min(checked((int)count), maximumResults));
        for (var index = 0U; index < count; index++)
        {
            var stableId = reader.ReadUInt64();
            var rawEntryKind = reader.ReadUInt32();
            if (!Enum.IsDefined((Core.Application.FileSystemEntryKind)rawEntryKind))
            {
                throw new InvalidDataException("The indexer returned an invalid file-system entry kind.");
            }

            var displayName = reader.ReadString();
            var fullPath = reader.ReadString();
            if (results.Count < maximumResults)
            {
                results.Add(new Core.Application.FileIndexMatch(
                    stableId,
                    (Core.Application.FileSystemEntryKind)rawEntryKind,
                    displayName,
                    fullPath));
            }
        }

        reader.EnsureComplete();
        return results;
    }

    internal static FileIndexStatus ParseStatus(byte[] payload)
    {
        const int expectedLength = sizeof(ulong) + (4 * sizeof(byte)) + sizeof(ulong);
        if (payload.Length != expectedLength)
        {
            throw new InvalidDataException("The file-index status payload has an invalid size.");
        }

        var generation = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0, 8));
        var activity = payload[8];
        if (activity > (byte)FileIndexActivity.Failed)
        {
            throw new InvalidDataException("The file-index activity value is invalid.");
        }

        var stage = payload[9];
        if (stage > (byte)FileIndexWorkStage.Persisting)
        {
            throw new InvalidDataException("The file-index work stage is invalid.");
        }
        var rawProgress = payload[10];
        if (rawProgress > 100 && rawProgress != byte.MaxValue)
        {
            throw new InvalidDataException("The file-index progress value is invalid.");
        }
        var flags = payload[11];
        if ((flags & ~1) != 0)
        {
            throw new InvalidDataException("The file-index status flags are invalid.");
        }
        var parsedActivity = (FileIndexActivity)activity;
        var parsedStage = (FileIndexWorkStage)stage;
        if ((flags & 1) != 0
            && (parsedStage != FileIndexWorkStage.Scanning || rawProgress == byte.MaxValue))
        {
            throw new InvalidDataException("The estimated file-index progress is inconsistent.");
        }
        if (parsedActivity == FileIndexActivity.Ready)
        {
            if (parsedStage != FileIndexWorkStage.Idle || rawProgress != 100 || flags != 0)
            {
                throw new InvalidDataException("The ready file-index status is inconsistent.");
            }
        }
        else if (parsedActivity == FileIndexActivity.Failed)
        {
            if (parsedStage != FileIndexWorkStage.Idle || rawProgress != byte.MaxValue || flags != 0)
            {
                throw new InvalidDataException("The failed file-index status is inconsistent.");
            }
        }
        else if (parsedStage == FileIndexWorkStage.Idle || rawProgress == 100)
        {
            throw new InvalidDataException("The active file-index status is inconsistent.");
        }

        var entries = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(12, 8));
        return new FileIndexStatus(
            generation,
            parsedActivity,
            parsedStage,
            rawProgress == byte.MaxValue ? null : rawProgress,
            (flags & 1) != 0,
            entries);
    }

    private static byte[] FinishPayload(MemoryStream stream)
    {
        if (stream.Length > MaximumPayloadLength)
        {
            throw new InvalidDataException("The file-index payload exceeds 1 MiB.");
        }

        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static async ValueTask ReadExactAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var count = await stream.ReadAsync(destination[read..], cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The file-index companion disconnected.");
            }

            read += count;
        }
    }

    private ref struct PayloadReader
    {
        private readonly ReadOnlySpan<byte> payload;
        private int offset;

        internal PayloadReader(ReadOnlySpan<byte> payload)
        {
            this.payload = payload;
        }

        internal uint ReadUInt32()
        {
            var value = Take(sizeof(uint));
            return BinaryPrimitives.ReadUInt32LittleEndian(value);
        }

        internal ulong ReadUInt64()
        {
            var value = Take(sizeof(ulong));
            return BinaryPrimitives.ReadUInt64LittleEndian(value);
        }

        internal string ReadString()
        {
            var length = ReadUInt32();
            if (length > int.MaxValue)
            {
                throw new InvalidDataException("The file-index string is too long.");
            }

            return StrictUtf8.GetString(Take((int)length));
        }

        internal void EnsureComplete()
        {
            if (offset != payload.Length)
            {
                throw new InvalidDataException("The file-index payload has trailing bytes.");
            }
        }

        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || payload.Length - offset < length)
            {
                throw new InvalidDataException("The file-index payload is truncated.");
            }

            var value = payload.Slice(offset, length);
            offset += length;
            return value;
        }
    }
}

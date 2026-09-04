using System.Buffers.Binary;
using System.Text;
using LuvLetter.Platform.Indexing;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestFileIndexPartitionConfiguration()
    {
        var options = new FileIndexClientOptions();
        var partitions = options.NormalizedPartitions();
        Assert.True(partitions.Count > 0);
        Assert.Equal(partitions.Count, partitions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.True(partitions.All(item => Path.IsPathFullyQualified(item.Root)
            && item.RefreshAgeSeconds is >= 60 and <= 86400
            && item.AutomaticGapSeconds is >= 1 and <= 3600));
        Assert.True(partitions.Where(item => item.Tier == FileIndexMaintenanceTier.StartupCritical)
            .All(item => item.RefreshAgeSeconds == options.Maintenance.RefreshIntervalSeconds));
        Assert.True(partitions.Where(item => item.Tier == FileIndexMaintenanceTier.Normal)
            .All(item => item.RefreshAgeSeconds == options.Maintenance.NormalPartitionRefreshIntervalSeconds));
        var profile = partitions.FirstOrDefault(item => item.Id == "filesystem:user-profile");
        if (profile is not null)
        {
            Assert.True(profile.DelegatedSubtrees.All(path => path.StartsWith(profile.Root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var parent in partitions)
        {
            var nestedRoots = partitions.Where(child => !child.Root.Equals(parent.Root, StringComparison.OrdinalIgnoreCase)
                    && child.Root.StartsWith(parent.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Select(static child => child.Root)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.SequenceEqual(nestedRoots,
                parent.DelegatedSubtrees.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                "Every nested partition must be delegated by its parent scan.");
        }

        var nestedOptions = new FileIndexClientOptions
        {
            Roots = [@"C:\Scope", @"C:\Scope\Work", @"C:\Scope\Work\Generated"],
        };
        var nestedPartitions = nestedOptions.NormalizedPartitions();
        Assert.Equal(3, nestedPartitions.Count);
        var scope = nestedPartitions.Single(item => item.Root.Equals(@"C:\Scope", StringComparison.OrdinalIgnoreCase));
        Assert.SequenceEqual([@"C:\Scope\Work", @"C:\Scope\Work\Generated"], scope.DelegatedSubtrees);
        var work = nestedPartitions.Single(item => item.Root.Equals(@"C:\Scope\Work", StringComparison.OrdinalIgnoreCase));
        Assert.SequenceEqual([@"C:\Scope\Work\Generated"], work.DelegatedSubtrees);

        var maintenance = new FileIndexMaintenanceOptions
        {
            RefreshIntervalSeconds = 420,
            NormalPartitionRefreshIntervalSeconds = 1800,
            AutomaticRebuildGapSeconds = 75,
            TriggerCooldownSeconds = 61,
            IgnoreRebuildDirectories = [@"C:\Ignored"],
            IgnoreRebuildCacheDirectories = [],
            IgnoreRebuildDirectoryNames = ["obj"],
            FullIgnorePaths = [@"C:\Secret"],
        };
        var descriptors = new[]
        {
            new FileIndexPartitionDescriptor("filesystem:test", @"C:\Data", [@"C:\Data\Desktop"],
                FileIndexMaintenanceTier.Normal, 1800, 75),
        };
        var payload = FileIndexProtocol.ConfigureRootsPayload(descriptors, maintenance);
        var reader = new FilePartitionPayloadReader(payload);
        Assert.Equal(1U, reader.UInt32());
        Assert.Equal("filesystem:test", reader.String());
        Assert.Equal(@"C:\Data", reader.String());
        Assert.Equal((uint)FileIndexMaintenanceTier.Normal, reader.UInt32());
        Assert.Equal(1800U, reader.UInt32());
        Assert.Equal(75U, reader.UInt32());
        Assert.Equal(1U, reader.UInt32());
        Assert.Equal(@"C:\Data\Desktop", reader.String());
        Assert.Equal(61U, reader.UInt32(), "Global cooldown follows all partition descriptors in LLIX v6.");
        Assert.Equal(1U, reader.UInt32());
        Assert.Equal(@"C:\Ignored", reader.String());
        Assert.Equal(1U, reader.UInt32());
        Assert.Equal("obj", reader.String());
        Assert.Equal(1U, reader.UInt32());
        Assert.Equal(@"C:\Secret", reader.String());
        Assert.True(reader.Complete);
        Assert.Equal((ushort)6, FileIndexProtocol.MajorVersion);
        return Task.CompletedTask;
    }

    private ref struct FilePartitionPayloadReader
    {
        private readonly ReadOnlySpan<byte> payload;
        private int offset;
        public FilePartitionPayloadReader(ReadOnlySpan<byte> payload)
        {
            this.payload = payload;
            offset = 0;
        }
        public bool Complete => offset == payload.Length;
        public uint UInt32()
        {
            var result = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            offset += sizeof(uint);
            return result;
        }
        public string String()
        {
            var length = checked((int)UInt32());
            var result = Encoding.UTF8.GetString(payload.Slice(offset, length));
            offset += length;
            return result;
        }
    }
}

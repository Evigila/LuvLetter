using System.Buffers.Binary;
using System.Text;
using LuvLetter.Platform.Indexing;

namespace LuvLetter.Core.Tests;

internal static partial class Program
{
    private static Task TestFileIndexPartitionConfiguration()
    {
        VerifyDefaultFileIndexRoots();
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
        Assert.Equal(61U, reader.UInt32(), "Global cooldown follows all partition descriptors in LLIX v8.");
        Assert.Equal(1U, reader.UInt32());
        Assert.Equal(@"C:\Ignored", reader.String());
        Assert.Equal(1U, reader.UInt32());
        Assert.Equal("obj", reader.String());
        Assert.Equal(1U, reader.UInt32());
        Assert.Equal(@"C:\Secret", reader.String());
        Assert.True(reader.Complete);
        Assert.Equal((ushort)8, FileIndexProtocol.MajorVersion);
        Assert.Equal((ushort)9, (ushort)FileIndexMessageType.Refresh);
        Assert.Equal((ushort)10, (ushort)FileIndexMessageType.Reconcile);
        return Task.CompletedTask;
    }

    private static void VerifyDefaultFileIndexRoots()
    {
        const string profile = @"C:\Users\TestUser";
        const string redirectedDownloads = @"D:\Users\TestUser\Downloads";
        var inferredDownloads = Path.Combine(profile, "Downloads");
        var roots = FileIndexClientOptions.CreateDefaultRoots(profile,
            [profile, "", profile, redirectedDownloads], redirectedDownloads);
        Assert.SequenceEqual([profile, redirectedDownloads], roots,
            "Default roots must use the resolved Downloads location and remove empty or duplicate entries.");
        Assert.False(roots.Contains(inferredDownloads, StringComparer.OrdinalIgnoreCase),
            "A redirected Downloads folder must not create an inferred partition under the user profile.");

        var options = new FileIndexClientOptions { Roots = roots };
        var partitions = options.NormalizedPartitions(profile, null, redirectedDownloads);
        var downloadsPartition = partitions.Single(item => item.Root == redirectedDownloads);
        Assert.Equal("filesystem:downloads", downloadsPartition.Id);
        Assert.Equal(FileIndexMaintenanceTier.StartupCritical, downloadsPartition.Tier);
        Assert.Equal(0, partitions.Single(item => item.Id == "filesystem:user-profile").DelegatedSubtrees.Length,
            "Downloads on another drive must not be delegated from the profile partition.");

        var nestedOptions = new FileIndexClientOptions
        {
            Roots = FileIndexClientOptions.CreateDefaultRoots(profile, [], inferredDownloads),
        };
        var nestedPartitions = nestedOptions.NormalizedPartitions(profile, null, inferredDownloads);
        Assert.Equal(FileIndexMaintenanceTier.StartupCritical,
            nestedPartitions.Single(item => item.Id == "filesystem:downloads").Tier);
        Assert.SequenceEqual([inferredDownloads],
            nestedPartitions.Single(item => item.Id == "filesystem:user-profile").DelegatedSubtrees,
            "Downloads inside the profile must still be delegated to its dedicated partition.");

        Assert.SequenceEqual([profile], FileIndexClientOptions.CreateDefaultRoots(profile, [], null),
            "An unresolved Downloads folder must not generate a guessed path.");
        Assert.SequenceEqual([profile], FileIndexClientOptions.CreateDefaultRoots(profile, [], ""));

        var missingRoot = Path.Combine(Path.GetTempPath(), "FileIndex.Tests", Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missingRoot));
        var explicitOptions = new FileIndexClientOptions { Roots = [missingRoot] };
        Assert.Equal(missingRoot, explicitOptions.NormalizedPartitions(profile, null, redirectedDownloads).Single().Root,
            "Explicitly configured roots must remain available for recovery when temporarily missing.");
    }

    private static Task TestFileIndexProgressProtocol()
    {
        var payload = new byte[20];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 42);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(12), 1234);
        payload[8] = (byte)FileIndexActivity.Updating;
        payload[9] = (byte)FileIndexWorkStage.Scanning;
        payload[10] = 37;
        payload[11] = 1;
        var progress = FileIndexProtocol.ParseStatus(payload);
        Assert.Equal(42UL, progress.IndexGeneration);
        Assert.Equal(1234UL, progress.DiscoveredEntries);
        Assert.Equal((byte?)37, progress.ProgressPercent);
        Assert.True(progress.Rebuilding && progress.ProgressIsEstimated);

        payload[8] = (byte)FileIndexActivity.Ready;
        payload[9] = (byte)FileIndexWorkStage.Idle;
        payload[10] = 100;
        payload[11] = 0;
        Assert.False(FileIndexProtocol.ParseStatus(payload).Rebuilding);

        payload[8] = (byte)FileIndexActivity.Failed;
        payload[10] = byte.MaxValue;
        var failed = FileIndexProtocol.ParseStatus(payload);
        Assert.Equal(FileIndexActivity.Failed, failed.Activity);
        Assert.Equal((byte?)null, failed.ProgressPercent);
        Assert.False(failed.Rebuilding);
        payload[10] = 100;
        Assert.Throws<InvalidDataException>(() => FileIndexProtocol.ParseStatus(payload));

        payload[8] = (byte)FileIndexActivity.InitialBuild;
        payload[9] = (byte)FileIndexWorkStage.Recovering;
        payload[10] = byte.MaxValue;
        Assert.True(FileIndexProtocol.ParseStatus(payload).Rebuilding);
        payload[11] = 1;
        Assert.Throws<InvalidDataException>(() => FileIndexProtocol.ParseStatus(payload));
        payload[9] = (byte)FileIndexWorkStage.Scanning;
        payload[10] = 50;
        payload[11] = 2;
        Assert.Throws<InvalidDataException>(() => FileIndexProtocol.ParseStatus(payload));
        Assert.Throws<InvalidDataException>(() => FileIndexProtocol.ParseStatus(new byte[9]));
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

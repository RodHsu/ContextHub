using Memory.Application;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class DashboardSnapshotRetentionTests
{
    [Fact]
    public void ComputeExpiration_Should_Keep_Snapshot_Until_Two_Hours_After_Stale()
    {
        var now = new DateTimeOffset(2026, 4, 15, 6, 0, 0, TimeSpan.Zero);
        var envelope = new DashboardSnapshotEnvelope<string>(
            DashboardSnapshotKeys.StatusCore,
            now,
            30,
            now.AddSeconds(30),
            string.Empty,
            "payload");

        var expiration = DashboardSnapshotRetentionPolicy.ComputeExpiration(envelope, now);

        Assert.Equal(TimeSpan.FromHours(2).Add(TimeSpan.FromSeconds(30)), expiration);
    }

    [Fact]
    public void ComputeExpiration_Should_Respect_Remaining_Grace_Period_For_Stale_Snapshot()
    {
        var now = new DateTimeOffset(2026, 4, 15, 6, 0, 0, TimeSpan.Zero);
        var envelope = new DashboardSnapshotEnvelope<string>(
            DashboardSnapshotKeys.StatusCore,
            now.AddMinutes(-45),
            30,
            now.AddMinutes(-30),
            "refresh failed",
            "payload");

        var expiration = DashboardSnapshotRetentionPolicy.ComputeExpiration(envelope, now);

        Assert.Equal(TimeSpan.FromMinutes(90), expiration);
    }

    [Fact]
    public void ComputeExpiration_Should_Clamp_To_One_Second_When_Grace_Period_Has_Passed()
    {
        var now = new DateTimeOffset(2026, 4, 15, 6, 0, 0, TimeSpan.Zero);
        var envelope = new DashboardSnapshotEnvelope<string>(
            DashboardSnapshotKeys.StatusCore,
            now.AddHours(-4),
            30,
            now.AddHours(-3),
            "refresh failed",
            "payload");

        var expiration = DashboardSnapshotRetentionPolicy.ComputeExpiration(envelope, now);

        Assert.Equal(TimeSpan.FromSeconds(1), expiration);
    }
}

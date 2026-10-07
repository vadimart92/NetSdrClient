using System.Net;
using NetSdr.Control;

namespace NetSdr.Tests.Control;

public class EscalatingRecoveryPolicyTests
{
    private static RecoveryContext Ctx(int k, int soft = 0, int hard = 0) =>
        new(k + soft + hard, k, ReconnectPhase.Connect, new IOException("x"), TimeSpan.FromSeconds(k), soft, hard);

    [Fact]
    public void Defaults_ContinueBeforeSoft_SoftAtThree()
    {
        var p = new EscalatingRecoveryPolicy();
        Assert.Equal((3, 3, 2), (p.SoftRebootAfter, p.HardRebootAfter, p.MaxRebootsPerLoss));
        Assert.Equal(RecoveryAction.Continue, p.OnAttemptFailed(Ctx(1)));
        Assert.Equal(RecoveryAction.Continue, p.OnAttemptFailed(Ctx(2)));
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Soft), p.OnAttemptFailed(Ctx(3)));
    }

    [Fact]
    public void AfterSoft_HardAtThree()
    {
        var p = new EscalatingRecoveryPolicy();
        Assert.Equal(RecoveryAction.Continue, p.OnAttemptFailed(Ctx(1, soft: 1)));
        Assert.Equal(RecoveryAction.Continue, p.OnAttemptFailed(Ctx(2, soft: 1)));
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Hard), p.OnAttemptFailed(Ctx(3, soft: 1)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(50)]
    public void AfterMaxReboots_AlwaysContinue(int k) =>
        Assert.Equal(RecoveryAction.Continue, new EscalatingRecoveryPolicy().OnAttemptFailed(Ctx(k, soft: 1, hard: 1)));

    [Fact]
    public void MaxRebootsZero_NeverReboots() =>
        Assert.Equal(RecoveryAction.Continue, new EscalatingRecoveryPolicy { MaxRebootsPerLoss = 0 }.OnAttemptFailed(Ctx(30)));

    [Fact]
    public void SoftRebootAfterOne_FirstFailureReboots() =>
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Soft), new EscalatingRecoveryPolicy { SoftRebootAfter = 1 }.OnAttemptFailed(Ctx(1)));

    [Fact]
    public void ManualHardFirst_NextIsHard() =>
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Hard),
            new EscalatingRecoveryPolicy { MaxRebootsPerLoss = 3 }.OnAttemptFailed(Ctx(3, soft: 0, hard: 1)));

    [Theory]
    [InlineData("SoftRebootAfter", 0)]
    [InlineData("HardRebootAfter", 0)]
    [InlineData("MaxRebootsPerLoss", -1)]
    public void InvalidValues_Throw(string property, int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _ = property switch
        {
            "SoftRebootAfter" => new EscalatingRecoveryPolicy { SoftRebootAfter = value },
            "HardRebootAfter" => new EscalatingRecoveryPolicy { HardRebootAfter = value },
            _ => new EscalatingRecoveryPolicy { MaxRebootsPerLoss = value },
        });
        Assert.StartsWith(property, ex.Message);
    }

    [Fact]
    public void RecoveryAction_Equality()
    {
        Assert.Equal(RecoveryAction.Reboot(RebootKind.Soft), RecoveryAction.Reboot(RebootKind.Soft));
        Assert.NotEqual(RecoveryAction.Reboot(RebootKind.Soft), RecoveryAction.Reboot(RebootKind.Hard));
        Assert.NotEqual(RecoveryAction.Reboot(RebootKind.Soft), RecoveryAction.Continue);
        Assert.True(RecoveryAction.GiveUp == RecoveryAction.GiveUp && RecoveryAction.GiveUp != RecoveryAction.Continue);
        Assert.Equal((RecoveryActionKind.Reboot, RebootKind.Hard), (RecoveryAction.Reboot(RebootKind.Hard).Kind, RecoveryAction.Reboot(RebootKind.Hard).RebootKind));
        Assert.Equal("Reboot(Soft)", RecoveryAction.Reboot(RebootKind.Soft).ToString());
    }

    [Fact]
    public void RebootContext_Fields()
    {
        var ctx = new RebootContext("host:50000", new IPEndPoint(IPAddress.Loopback, 50000), requested: true);
        Assert.Equal(("host:50000", 50000, true), (ctx.Target, ctx.LastRemoteEndPoint!.Port, ctx.Requested));
        Assert.Throws<ArgumentNullException>(() => new RebootContext(null!, null, false));
    }
}

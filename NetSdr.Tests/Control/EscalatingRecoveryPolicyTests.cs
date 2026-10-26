using System.Net;
using NetSdr.Control;

namespace NetSdr.Tests.Control;

public class EscalatingRecoveryPolicyTests
{
    private static RecoveryContext Ctx(int k, int soft = 0, int hard = 0) =>
        new(k + soft + hard, k, ReconnectPhase.Connect, new IOException("x"), TimeSpan.FromSeconds(k), soft, hard);

    [Test]
    public void Defaults_ContinueBeforeSoft_SoftAtThree()
    {
        var p = new EscalatingRecoveryPolicy();
        Assert.That((p.SoftRebootAfter, p.HardRebootAfter, p.MaxRebootsPerLoss), Is.EqualTo((3, 3, 2)));
        Assert.That(p.OnAttemptFailed(Ctx(1)), Is.EqualTo(RecoveryAction.Continue));
        Assert.That(p.OnAttemptFailed(Ctx(2)), Is.EqualTo(RecoveryAction.Continue));
        Assert.That(p.OnAttemptFailed(Ctx(3)), Is.EqualTo(RecoveryAction.Reboot(RebootKind.Soft)));
    }

    [Test]
    public void AfterSoft_HardAtThree()
    {
        var p = new EscalatingRecoveryPolicy();
        Assert.That(p.OnAttemptFailed(Ctx(1, soft: 1)), Is.EqualTo(RecoveryAction.Continue));
        Assert.That(p.OnAttemptFailed(Ctx(2, soft: 1)), Is.EqualTo(RecoveryAction.Continue));
        Assert.That(p.OnAttemptFailed(Ctx(3, soft: 1)), Is.EqualTo(RecoveryAction.Reboot(RebootKind.Hard)));
    }

    [TestCase(1)]
    [TestCase(3)]
    [TestCase(50)]
    public void AfterMaxReboots_AlwaysContinue(int k) =>
        Assert.That(new EscalatingRecoveryPolicy().OnAttemptFailed(Ctx(k, soft: 1, hard: 1)), Is.EqualTo(RecoveryAction.Continue));

    [Test]
    public void MaxRebootsZero_NeverReboots() =>
        Assert.That(new EscalatingRecoveryPolicy { MaxRebootsPerLoss = 0 }.OnAttemptFailed(Ctx(30)), Is.EqualTo(RecoveryAction.Continue));

    [Test]
    public void SoftRebootAfterOne_FirstFailureReboots() =>
        Assert.That(
            new EscalatingRecoveryPolicy { SoftRebootAfter = 1 }.OnAttemptFailed(Ctx(1)),
            Is.EqualTo(RecoveryAction.Reboot(RebootKind.Soft)));

    [Test]
    public void ManualHardFirst_NextIsHard() =>
        Assert.That(
            new EscalatingRecoveryPolicy { MaxRebootsPerLoss = 3 }.OnAttemptFailed(Ctx(3, soft: 0, hard: 1)),
            Is.EqualTo(RecoveryAction.Reboot(RebootKind.Hard)));

    [TestCase("SoftRebootAfter", 0)]
    [TestCase("HardRebootAfter", 0)]
    [TestCase("MaxRebootsPerLoss", -1)]
    public void InvalidValues_Throw(string property, int value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _ = property switch
        {
            "SoftRebootAfter" => new EscalatingRecoveryPolicy { SoftRebootAfter = value },
            "HardRebootAfter" => new EscalatingRecoveryPolicy { HardRebootAfter = value },
            _ => new EscalatingRecoveryPolicy { MaxRebootsPerLoss = value },
        });
        Assert.That(ex.Message, Does.StartWith(property));
    }

    [Test]
    public void RecoveryAction_Equality()
    {
        // Two instances made apart: equality is by value.
        RecoveryAction soft = RecoveryAction.Reboot(RebootKind.Soft);
        Assert.That(RecoveryAction.Reboot(RebootKind.Soft), Is.EqualTo(soft));
        Assert.That(RecoveryAction.Reboot(RebootKind.Hard), Is.Not.EqualTo(soft));
        Assert.That(RecoveryAction.Continue, Is.Not.EqualTo(soft));
        Assert.That(RecoveryAction.GiveUp == RecoveryAction.GiveUp && RecoveryAction.GiveUp != RecoveryAction.Continue, Is.True);
        Assert.That(
            (RecoveryAction.Reboot(RebootKind.Hard).Kind, RecoveryAction.Reboot(RebootKind.Hard).RebootKind),
            Is.EqualTo((RecoveryActionKind.Reboot, RebootKind.Hard)));
        Assert.That(RecoveryAction.Reboot(RebootKind.Soft).ToString(), Is.EqualTo("Reboot(Soft)"));
    }

    [Test]
    public void RebootContext_Fields()
    {
        var ctx = new RebootContext("host:50000", new IPEndPoint(IPAddress.Loopback, 50000), requested: true);
        Assert.That((ctx.Target, ctx.LastRemoteEndPoint!.Port, ctx.Requested), Is.EqualTo(("host:50000", 50000, true)));
        Assert.That(() => new RebootContext(null!, null, false), Throws.TypeOf<ArgumentNullException>());
    }
}

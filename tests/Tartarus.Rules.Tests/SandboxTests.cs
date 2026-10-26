using Tartarus.Vision;

namespace Tartarus.Rules.Tests;

public class SandboxTests
{
    [Fact]
    public void A_copy_keeps_everything_read()
    {
        // 2026-10-26: the copy lost the run theme, and with it Cage Fight's wider diet from every hint on the Sandbox.
        var b = EditableSandbox.Empty();
        (b.Warden, b.Days, b.Chromatic, b.Theme, b.Objective, b.SandboxEdited) = (4, 12, 3, "cagefight", true, true);
        b.Destinations = [2, 5];
        b.Instruments = ["casket"];
        b.Phase = Phases.Destination;
        b[4].Kind = CellKind.Inmate;
        b[4].Tusk = true;
        var copy = b.Clone();
        Assert.Equal((b.Warden, b.Days, b.Chromatic, b.Theme, b.Objective, b.SandboxEdited, b.Phase), (copy.Warden, copy.Days, copy.Chromatic, copy.Theme, copy.Objective, copy.SandboxEdited, copy.Phase));
        Assert.Equal(b.Destinations, copy.Destinations);
        Assert.Equal(b.Instruments, copy.Instruments);
        Assert.True(copy[4].Tusk);
        Assert.NotSame(b[4], copy[4]);
    }
}

using TrackPadLens.Native;
using TrackPadLens.Processing;
using Xunit;

namespace TrackPadLens.Tests;

public sealed class PalmRejectionTests
{
    private static readonly LogicalBounds DefaultBounds = new(0, 3000, 0, 2000);

    [Fact]
    public void Evaluate_RejectsMultiFingerContactAndLocksOutUntilAllLift()
    {
        var palm = new PalmRejection { Enabled = true, MultiFingerRejectionEnabled = true };

        var twoFingers = new[]
        {
            new TouchContact { ContactId = 1, TipSwitch = true, Confidence = true, RawX = 500, RawY = 500 },
            new TouchContact { ContactId = 2, TipSwitch = true, Confidence = true, RawX = 900, RawY = 500 }
        };

        var r1 = palm.Evaluate(twoFingers, reportedContactCount: 2, in DefaultBounds, descriptorSupportsContactArea: false);
        Assert.Equal(PalmDecision.RejectedMultiFinger, r1.Decision);

        // Lifting one finger while the second remains down stays locked out
        var oneRemaining = new[]
        {
            new TouchContact { ContactId = 2, TipSwitch = true, Confidence = true, RawX = 910, RawY = 500 }
        };
        var r2 = palm.Evaluate(oneRemaining, reportedContactCount: 1, in DefaultBounds, descriptorSupportsContactArea: false);
        Assert.Equal(PalmDecision.RejectedMultiFinger, r2.Decision);

        // All lift resets lockout cleanly
        var lifted = new[]
        {
            new TouchContact { ContactId = 2, TipSwitch = false, Confidence = true, RawX = 910, RawY = 500 }
        };
        var r3 = palm.Evaluate(lifted, reportedContactCount: 0, in DefaultBounds, descriptorSupportsContactArea: false);
        Assert.Equal(PalmDecision.NoActiveContact, r3.Decision);

        // Fresh single finger is accepted again
        var single = new[]
        {
            new TouchContact { ContactId = 3, TipSwitch = true, Confidence = true, RawX = 600, RawY = 600 }
        };
        var r4 = palm.Evaluate(single, reportedContactCount: 1, in DefaultBounds, descriptorSupportsContactArea: false);
        Assert.Equal(PalmDecision.Accepted, r4.Decision);
        Assert.Equal(3, palm.ActiveContactId);
    }

    [Fact]
    public void Evaluate_RejectsOversizedPalmOnlyWhenDescriptorSupportsContactArea()
    {
        var palm = new PalmRejection
        {
            Enabled = true,
            MaxContactWidthLogical = 250,
            MaxContactHeightLogical = 250
        };

        var wideContact = new[]
        {
            new TouchContact
            {
                ContactId = 1,
                TipSwitch = true,
                Confidence = true,
                RawX = 1000,
                RawY = 1000,
                Width = 420,
                Height = 380,
                HasWidth = true,
                HasHeight = true
            }
        };

        // When descriptor does NOT report contact area, do not reject based on bogus geometry
        var adaptiveSkip = palm.Evaluate(wideContact, 1, in DefaultBounds, descriptorSupportsContactArea: false);
        Assert.Equal(PalmDecision.Accepted, adaptiveSkip.Decision);

        palm.Reset();

        // When descriptor DOES report contact area, oversized contact is rejected
        var rejected = palm.Evaluate(wideContact, 1, in DefaultBounds, descriptorSupportsContactArea: true);
        Assert.Equal(PalmDecision.RejectedPalmArea, rejected.Decision);
    }

    [Fact]
    public void Evaluate_RejectsFirmwareLowConfidenceFlag()
    {
        var palm = new PalmRejection { Enabled = true };
        var lowConfidence = new[]
        {
            new TouchContact { ContactId = 4, TipSwitch = true, Confidence = false, RawX = 400, RawY = 400 }
        };

        var result = palm.Evaluate(lowConfidence, 1, in DefaultBounds, descriptorSupportsContactArea: false);
        Assert.Equal(PalmDecision.RejectedConfidenceFlag, result.Decision);
    }
}

using TrackPadLens.Native;

namespace TrackPadLens.Processing;

public enum PalmDecision
{
    Accepted,
    NoActiveContact,
    RejectedMultiFinger,
    RejectedConfidenceFlag,
    RejectedPalmArea,
    RejectedSecondaryContactId
}

public readonly record struct PalmFilterResult(
    PalmDecision Decision,
    TouchContact? PrimaryContact);

/// <summary>
/// Adaptive palm and multi-finger rejection engine.
/// Inspects actual HID descriptor capabilities (confidence flag, contact width/height, contact ID,
/// contact count) rather than assuming every touchpad reports contact geometry.
/// </summary>
public sealed class PalmRejection
{
    public bool Enabled { get; set; } = true;
    public bool MultiFingerRejectionEnabled { get; set; } = true;
    public int MaxContactWidthLogical { get; set; } = 320;
    public int MaxContactHeightLogical { get; set; } = 320;
    public double MaxContactAreaFraction { get; set; } = 0.08;

    private int? _trackedContactId;
    private bool _lockedOutUntilAllLift;

    public int? ActiveContactId => _trackedContactId;

    public void Reset()
    {
        _trackedContactId = null;
        _lockedOutUntilAllLift = false;
    }

    public PalmFilterResult Evaluate(
        ReadOnlySpan<TouchContact> contacts,
        int reportedContactCount,
        in LogicalBounds logicalBounds,
        bool descriptorSupportsContactArea)
    {
        // Count currently active tip contacts in this report
        int activeTips = 0;
        int firstActiveIdx = -1;
        for (int i = 0; i < contacts.Length; i++)
        {
            if (contacts[i].TipSwitch)
            {
                if (firstActiveIdx < 0) firstActiveIdx = i;
                activeTips++;
            }
        }

        int effectiveCount = Math.Max(activeTips, reportedContactCount);

        if (activeTips == 0)
        {
            // All fingers lifted: release any multi-finger/palm lockout cleanly
            _trackedContactId = null;
            _lockedOutUntilAllLift = false;
            return new PalmFilterResult(PalmDecision.NoActiveContact, null);
        }

        if (_lockedOutUntilAllLift)
        {
            return new PalmFilterResult(PalmDecision.RejectedMultiFinger, null);
        }

        if (Enabled && MultiFingerRejectionEnabled && effectiveCount > 1)
        {
            _trackedContactId = null;
            _lockedOutUntilAllLift = true;
            return new PalmFilterResult(PalmDecision.RejectedMultiFinger, null);
        }

        // Select the tracked contact if already locked on, otherwise the first active contact
        int selectedIdx = firstActiveIdx;
        if (_trackedContactId.HasValue)
        {
            bool found = false;
            for (int i = 0; i < contacts.Length; i++)
            {
                if (contacts[i].TipSwitch && contacts[i].ContactId == _trackedContactId.Value)
                {
                    selectedIdx = i;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                // Original finger lifted while another finger appeared in the same frame
                _trackedContactId = null;
                return new PalmFilterResult(PalmDecision.RejectedSecondaryContactId, null);
            }
        }

        TouchContact candidate = contacts[selectedIdx];

        if (Enabled)
        {
            // Windows Precision Touchpad Confidence usage (0x0D:0x47): 0 explicitly signals firmware palm rejection
            if (!candidate.Confidence)
            {
                _trackedContactId = null;
                _lockedOutUntilAllLift = true;
                return new PalmFilterResult(PalmDecision.RejectedConfidenceFlag, null);
            }

            // Adaptive contact-area rejection: only applied if the device actually reported width/height
            if (descriptorSupportsContactArea && (candidate.HasWidth || candidate.HasHeight))
            {
                bool tooWide = candidate.HasWidth && candidate.Width > MaxContactWidthLogical;
                bool tooTall = candidate.HasHeight && candidate.Height > MaxContactHeightLogical;

                bool areaExceeded = false;
                if (candidate.HasWidth && candidate.HasHeight)
                {
                    double padArea = Math.Max(1.0, (double)(logicalBounds.MaxX - logicalBounds.MinX) * (logicalBounds.MaxY - logicalBounds.MinY));
                    double contactArea = (double)candidate.Width * candidate.Height;
                    areaExceeded = (contactArea / padArea) > MaxContactAreaFraction;
                }

                if (tooWide || tooTall || areaExceeded)
                {
                    _trackedContactId = null;
                    _lockedOutUntilAllLift = true;
                    return new PalmFilterResult(PalmDecision.RejectedPalmArea, null);
                }
            }
        }

        _trackedContactId = candidate.ContactId;
        return new PalmFilterResult(PalmDecision.Accepted, candidate);
    }
}

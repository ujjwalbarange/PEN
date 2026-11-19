import type { LogicalBounds } from './coordinateMapper';

export interface TouchContactSample {
  contactId: number;
  tipSwitch: boolean;
  confidence: boolean;
  rawX: number;
  rawY: number;
  width?: number;
  height?: number;
}

export type PalmDecision =
  | 'Accepted'
  | 'NoActiveContact'
  | 'RejectedMultiFinger'
  | 'RejectedConfidenceFlag'
  | 'RejectedPalmArea'
  | 'RejectedSecondaryContactId';

export interface PalmFilterResult {
  decision: PalmDecision;
  primaryContact: TouchContactSample | null;
}

export class PalmRejectionEngine {
  public enabled = true;
  public multiFingerRejectionEnabled = true;
  public maxContactWidthLogical = 320;
  public maxContactHeightLogical = 320;
  public maxContactAreaFraction = 0.08;

  private trackedContactId: number | null = null;
  private lockedOutUntilAllLift = false;

  public get activeContactId(): number | null {
    return this.trackedContactId;
  }

  public reset(): void {
    this.trackedContactId = null;
    this.lockedOutUntilAllLift = false;
  }

  public evaluate(
    contacts: readonly TouchContactSample[],
    reportedContactCount: number,
    bounds: LogicalBounds,
    descriptorSupportsContactArea: boolean,
  ): PalmFilterResult {
    let activeTips = 0;
    let firstActiveIdx = -1;
    for (let i = 0; i < contacts.length; i++) {
      if (contacts[i].tipSwitch) {
        if (firstActiveIdx < 0) firstActiveIdx = i;
        activeTips++;
      }
    }

    const effectiveCount = Math.max(activeTips, reportedContactCount);
    if (activeTips === 0) {
      this.trackedContactId = null;
      this.lockedOutUntilAllLift = false;
      return { decision: 'NoActiveContact', primaryContact: null };
    }

    if (this.lockedOutUntilAllLift) {
      return { decision: 'RejectedMultiFinger', primaryContact: null };
    }

    if (this.enabled && this.multiFingerRejectionEnabled && effectiveCount > 1) {
      this.trackedContactId = null;
      this.lockedOutUntilAllLift = true;
      return { decision: 'RejectedMultiFinger', primaryContact: null };
    }

    let selectedIdx = firstActiveIdx;
    if (this.trackedContactId !== null) {
      const foundIdx = contacts.findIndex(
        (c) => c.tipSwitch && c.contactId === this.trackedContactId,
      );
      if (foundIdx < 0) {
        this.trackedContactId = null;
        return { decision: 'RejectedSecondaryContactId', primaryContact: null };
      }
      selectedIdx = foundIdx;
    }

    const candidate = contacts[selectedIdx];

    if (this.enabled) {
      if (!candidate.confidence) {
        this.trackedContactId = null;
        this.lockedOutUntilAllLift = true;
        return { decision: 'RejectedConfidenceFlag', primaryContact: null };
      }

      if (
        descriptorSupportsContactArea &&
        (candidate.width !== undefined || candidate.height !== undefined)
      ) {
        const tooWide =
          candidate.width !== undefined && candidate.width > this.maxContactWidthLogical;
        const tooTall =
          candidate.height !== undefined && candidate.height > this.maxContactHeightLogical;

        let areaExceeded = false;
        if (candidate.width !== undefined && candidate.height !== undefined) {
          const padArea = Math.max(
            1,
            (bounds.maxX - bounds.minX) * (bounds.maxY - bounds.minY),
          );
          areaExceeded = (candidate.width * candidate.height) / padArea > this.maxContactAreaFraction;
        }

        if (tooWide || tooTall || areaExceeded) {
          this.trackedContactId = null;
          this.lockedOutUntilAllLift = true;
          return { decision: 'RejectedPalmArea', primaryContact: null };
        }
      }
    }

    this.trackedContactId = candidate.contactId;
    return { decision: 'Accepted', primaryContact: candidate };
  }
}

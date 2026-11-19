/**
 * TypeScript mirror of TrackPadLens/Processing/OneEuroFilter.cs
 * Casiez, Roussel, Vogel (CHI 2012) timestamped 1-Euro Filter.
 */

export class OneEuroFilter {
  public minCutoff: number;
  public beta: number;
  public dCutoff: number;

  private initialized = false;
  private lastTimestamp = 0;
  private lastRawValue = 0;
  private lastFilteredValue = 0;
  private lastFilteredDerivative = 0;

  constructor(minCutoff = 1.8, beta = 0.025, dCutoff = 1.0) {
    this.minCutoff = Math.max(0.01, minCutoff);
    this.beta = Math.max(0.0, beta);
    this.dCutoff = Math.max(0.01, dCutoff);
  }

  public reset(): void {
    this.initialized = false;
    this.lastTimestamp = 0;
    this.lastRawValue = 0;
    this.lastFilteredValue = 0;
    this.lastFilteredDerivative = 0;
  }

  public filter(value: number, timestampSeconds: number): number {
    if (!this.initialized) {
      this.initialized = true;
      this.lastTimestamp = timestampSeconds;
      this.lastRawValue = value;
      this.lastFilteredValue = value;
      this.lastFilteredDerivative = 0;
      return value;
    }

    let dt = timestampSeconds - this.lastTimestamp;
    if (dt <= 1e-6) {
      return this.lastFilteredValue;
    }
    if (dt > 0.25) {
      dt = 0.25;
    }

    this.lastTimestamp = timestampSeconds;

    const dx = (value - this.lastFilteredValue) / dt;
    this.lastRawValue = value;

    const alphaD = OneEuroFilter.computeAlpha(this.dCutoff, dt);
    const edX = OneEuroFilter.lowPass(dx, this.lastFilteredDerivative, alphaD);
    this.lastFilteredDerivative = edX;

    const cutoff = this.minCutoff + this.beta * Math.abs(edX);
    const alpha = OneEuroFilter.computeAlpha(cutoff, dt);
    const filtered = OneEuroFilter.lowPass(value, this.lastFilteredValue, alpha);
    this.lastFilteredValue = filtered;

    return filtered;
  }

  private static computeAlpha(cutoffHz: number, dtSeconds: number): number {
    const tau = 1.0 / (2.0 * Math.PI * cutoffHz);
    return 1.0 / (1.0 + tau / dtSeconds);
  }

  private static lowPass(current: number, previous: number, alpha: number): number {
    return alpha * current + (1.0 - alpha) * previous;
  }
}

export class OneEuroFilter2D {
  private readonly fx: OneEuroFilter;
  private readonly fy: OneEuroFilter;

  constructor(minCutoff = 1.8, beta = 0.025, dCutoff = 1.0) {
    this.fx = new OneEuroFilter(minCutoff, beta, dCutoff);
    this.fy = new OneEuroFilter(minCutoff, beta, dCutoff);
  }

  public updateParameters(minCutoff: number, beta: number, dCutoff: number): void {
    this.fx.minCutoff = Math.max(0.01, minCutoff);
    this.fx.beta = Math.max(0, beta);
    this.fx.dCutoff = Math.max(0.01, dCutoff);
    this.fy.minCutoff = Math.max(0.01, minCutoff);
    this.fy.beta = Math.max(0, beta);
    this.fy.dCutoff = Math.max(0.01, dCutoff);
  }

  public reset(): void {
    this.fx.reset();
    this.fy.reset();
  }

  public filter(x: number, y: number, timestampSeconds: number): { x: number; y: number } {
    return {
      x: this.fx.filter(x, timestampSeconds),
      y: this.fy.filter(y, timestampSeconds),
    };
  }
}

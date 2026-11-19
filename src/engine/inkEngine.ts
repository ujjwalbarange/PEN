/**
 * TypeScript mirror of TrackPadLens/Rendering/InkEngine.cs and LaserTrailRenderer.cs
 */

export type DrawingTool = 'pen' | 'highlighter' | 'laser';
export type ThicknessPreset = 2 | 4 | 7;

export const PEN_COLORS = {
  red: '#FF3B30',
  blue: '#007AFF',
  highlighter: '#FFCC00',
  white: '#FFFFFF',
  black: '#000000',
} as const;

export interface InkPoint {
  x: number;
  y: number;
  t: number;
}

export interface InkStroke {
  id: number;
  color: string;
  opacity: number;
  thickness: number;
  tool: DrawingTool;
  points: InkPoint[];
  finalized: boolean;
}

interface HistoryEntry {
  type: 'add' | 'clear';
  addedStroke?: InkStroke;
  clearedStrokes?: InkStroke[];
}

export class InkEngineState {
  public static readonly MAX_UNDO_HISTORY = 128;
  public static readonly MAX_POINTS_PER_STROKE = 4096;
  public static readonly LASER_LIFETIME_SEC = 1.5;

  public activeTool: DrawingTool = 'pen';
  public activeColor: string = PEN_COLORS.red;
  public thickness: ThicknessPreset = 4;

  private strokes: InkStroke[] = [];
  private activeStroke: InkStroke | null = null;
  private undoStack: HistoryEntry[] = [];
  private laserTrail: InkPoint[] = [];
  private nextId = 1;

  public getStrokes(): readonly InkStroke[] {
    return this.strokes;
  }

  public getActiveStroke(): InkStroke | null {
    return this.activeStroke;
  }

  public getUndoDepth(): number {
    return this.undoStack.length;
  }

  public getLaserPoints(nowSec: number): readonly InkPoint[] {
    this.pruneLaser(nowSec);
    return this.laserTrail;
  }

  public beginStroke(pt: InkPoint): void {
    if (this.activeStroke) {
      this.endStroke();
    }
    const isHighlighter = this.activeTool === 'highlighter';
    this.activeStroke = {
      id: this.nextId++,
      color: isHighlighter ? PEN_COLORS.highlighter : this.activeColor,
      opacity: isHighlighter ? 0.4 : 1.0,
      thickness: isHighlighter ? this.thickness * 3.5 : this.thickness,
      tool: this.activeTool,
      points: [pt],
      finalized: false,
    };
  }

  public appendPoint(pt: InkPoint): void {
    if (!this.activeStroke) return;
    if (this.activeStroke.points.length >= InkEngineState.MAX_POINTS_PER_STROKE) return;
    this.activeStroke.points.push(pt);
  }

  public endStroke(): InkStroke | null {
    if (!this.activeStroke) return null;
    const completed = this.activeStroke;
    completed.finalized = true;
    this.activeStroke = null;
    this.strokes.push(completed);
    this.pushUndo({ type: 'add', addedStroke: completed });
    return completed;
  }

  public addLaserPoint(pt: InkPoint): void {
    this.laserTrail.push(pt);
    if (this.laserTrail.length > 256) {
      this.laserTrail.shift();
    }
  }

  public pruneLaser(nowSec: number): void {
    while (
      this.laserTrail.length > 0 &&
      nowSec - this.laserTrail[0].t >= InkEngineState.LASER_LIFETIME_SEC
    ) {
      this.laserTrail.shift();
    }
  }

  public undo(): boolean {
    this.activeStroke = null;
    const entry = this.undoStack.pop();
    if (!entry) return false;

    if (entry.type === 'add' && entry.addedStroke) {
      this.strokes = this.strokes.filter((s) => s.id !== entry.addedStroke!.id);
    } else if (entry.type === 'clear' && entry.clearedStrokes) {
      this.strokes = [...this.strokes, ...entry.clearedStrokes];
    }
    return true;
  }

  public clearAll(): boolean {
    this.activeStroke = null;
    this.laserTrail = [];
    if (this.strokes.length === 0) return false;
    const snapshot = [...this.strokes];
    this.strokes = [];
    this.pushUndo({ type: 'clear', clearedStrokes: snapshot });
    return true;
  }

  private pushUndo(entry: HistoryEntry): void {
    this.undoStack.push(entry);
    while (this.undoStack.length > InkEngineState.MAX_UNDO_HISTORY) {
      this.undoStack.shift();
    }
  }
}

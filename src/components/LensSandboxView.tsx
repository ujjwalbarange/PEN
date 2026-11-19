import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  Camera,
  ChevronLeft,
  ChevronRight,
  Crosshair,
  Lock,
  RotateCcw,
  SlidersHorizontal,
  Sparkles,
  Trash2,
  Unlock,
} from 'lucide-react';
import {
  computeLensRect,
  DEFAULT_CALIBRATION,
  mapRawToLens,
  resolveAspectRatio,
  scaleLensWidth,
  type AnchorMode,
  type CalibrationConfig,
  type LensRect,
} from '../engine/coordinateMapper';
import {
  InkEngineState,
  PEN_COLORS,
  type DrawingTool,
  type InkStroke,
  type ThicknessPreset,
} from '../engine/inkEngine';
import { OneEuroFilter2D } from '../engine/oneEuroFilter';
import { PalmRejectionEngine, type PalmDecision } from '../engine/palmRejection';

const PAD_LOGICAL_BOUNDS = { minX: 0, maxX: 3200, minY: 0, maxY: 2000 };
const PHYSICAL_WIDTH_MM = 128;
const PHYSICAL_HEIGHT_MM = 80;
const BASE_LENS_WIDTH = 360;

function drawSmoothStroke(ctx: CanvasRenderingContext2D, stroke: InkStroke) {
  const pts = stroke.points;
  if (pts.length === 0) return;

  ctx.save();
  ctx.globalAlpha = stroke.opacity;
  ctx.strokeStyle = stroke.color;
  ctx.fillStyle = stroke.color;
  ctx.lineWidth = stroke.thickness;
  ctx.lineCap = stroke.tool === 'highlighter' ? 'butt' : 'round';
  ctx.lineJoin = 'round';

  if (pts.length === 1) {
    ctx.beginPath();
    ctx.arc(pts[0].x, pts[0].y, stroke.thickness * 0.5, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
    return;
  }

  ctx.beginPath();
  ctx.moveTo(pts[0].x, pts[0].y);
  if (pts.length === 2) {
    ctx.lineTo(pts[1].x, pts[1].y);
  } else {
    for (let i = 1; i < pts.length - 1; i++) {
      const midX = (pts[i].x + pts[i + 1].x) * 0.5;
      const midY = (pts[i].y + pts[i + 1].y) * 0.5;
      ctx.quadraticCurveTo(pts[i].x, pts[i].y, midX, midY);
    }
    ctx.lineTo(pts[pts.length - 1].x, pts[pts.length - 1].y);
  }
  ctx.stroke();
  ctx.restore();
}

export const LensSandboxView: React.FC = () => {
  const stageRef = useRef<HTMLDivElement | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const padRef = useRef<HTMLDivElement | null>(null);

  const inkEngineRef = useRef(new InkEngineState());
  const filterRef = useRef(new OneEuroFilter2D(1.8, 0.025, 1.0));
  const palmEngineRef = useRef(new PalmRejectionEngine());

  const [stageSize, setStageSize] = useState({ width: 880, height: 540 });
  const [doodleMode, setDoodleMode] = useState(true);
  const [activeTool, setActiveTool] = useState<DrawingTool>('pen');
  const [activeColor, setActiveColor] = useState<string>(PEN_COLORS.red);
  const [thickness, setThickness] = useState<ThicknessPreset>(4);
  const [compactToolbar, setCompactToolbar] = useState(false);

  const [anchorMode, setAnchorMode] = useState<AnchorMode>('centered');
  const [shiftHeld, setShiftHeld] = useState(false);
  const [rightButtonHeld, setRightButtonHeld] = useState(false);
  const [manualLock, setManualLock] = useState(false);
  const anchorLocked = shiftHeld || rightButtonHeld || manualLock;

  const [lensWidth, setLensWidth] = useState(BASE_LENS_WIDTH);
  const [cursorPos, setCursorPos] = useState({ x: 440, y: 270 });
  const [scaleBadge, setScaleBadge] = useState<string | null>(null);
  const scaleTimerRef = useRef<number | null>(null);

  const [minCutoff, setMinCutoff] = useState(1.8);
  const [beta, setBeta] = useState(0.025);
  const [dCutoff, setDCutoff] = useState(1.0);
  const [calibration, setCalibration] = useState<CalibrationConfig>(DEFAULT_CALIBRATION);

  const [simulateMultiFinger, setSimulateMultiFinger] = useState(false);
  const [simulateLargePalm, setSimulateLargePalm] = useState(false);
  const [lastPalmDecision, setLastPalmDecision] = useState<PalmDecision>('NoActiveContact');
  const [hoverDot, setHoverDot] = useState<{ visible: boolean; x: number; y: number; u: number; v: number }>({
    visible: false,
    x: 0,
    y: 0,
    u: 0,
    v: 0,
  });

  const [strokeCount, setStrokeCount] = useState(0);
  const [undoDepth, setUndoDepth] = useState(0);
  const [snapshotNotice, setSnapshotNotice] = useState<string | null>(null);

  const isPadDrawingRef = useRef(false);
  const lastEmittedRef = useRef<{ x: number; y: number } | null>(null);

  const aspectRatio = resolveAspectRatio(PHYSICAL_WIDTH_MM, PHYSICAL_HEIGHT_MM, calibration);
  const activeLens: LensRect = computeLensRect(
    cursorPos.x,
    cursorPos.y,
    lensWidth,
    aspectRatio,
    anchorMode,
    0,
    0,
    { left: 0, top: 0, width: stageSize.width, height: stageSize.height },
  );
  const activeLensRef = useRef(activeLens);
  activeLensRef.current = activeLens;

  // Keep engine configuration synced
  useEffect(() => {
    filterRef.current.updateParameters(minCutoff, beta, dCutoff);
  }, [minCutoff, beta, dCutoff]);

  useEffect(() => {
    inkEngineRef.current.activeTool = activeTool;
    inkEngineRef.current.activeColor = activeColor;
    inkEngineRef.current.thickness = thickness;
  }, [activeTool, activeColor, thickness]);

  // Measure canvas stage cleanly on resize
  useEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const observer = new ResizeObserver((entries) => {
      const rect = entries[0]?.contentRect;
      if (rect && rect.width > 100 && rect.height > 100) {
        setStageSize({ width: Math.round(rect.width), height: Math.round(rect.height) });
      }
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const syncCounts = useCallback(() => {
    setStrokeCount(inkEngineRef.current.getStrokes().length);
    setUndoDepth(inkEngineRef.current.getUndoDepth());
  }, []);

  const renderCanvas = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const dpr = window.devicePixelRatio || 1;
    const targetW = Math.round(stageSize.width * dpr);
    const targetH = Math.round(stageSize.height * dpr);
    if (canvas.width !== targetW || canvas.height !== targetH) {
      canvas.width = targetW;
      canvas.height = targetH;
    }

    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, stageSize.width, stageSize.height);

    // Draw all finalized strokes
    for (const s of inkEngineRef.current.getStrokes()) {
      drawSmoothStroke(ctx, s);
    }

    // Draw active stroke
    const active = inkEngineRef.current.getActiveStroke();
    if (active) {
      drawSmoothStroke(ctx, active);
    }

    // Draw decaying 1.5s laser trail
    const nowSec = performance.now() / 1000;
    const laserPts = inkEngineRef.current.getLaserPoints(nowSec);
    if (laserPts.length > 0) {
      for (let i = 0; i < laserPts.length - 1; i++) {
        const a = laserPts[i];
        const b = laserPts[i + 1];
        if (b.t - a.t > 0.12) continue;
        const life = Math.max(0, Math.min(1, 1 - (nowSec - b.t) / InkEngineState.LASER_LIFETIME_SEC));
        if (life <= 0.01) continue;

        ctx.save();
        ctx.lineCap = 'round';
        ctx.strokeStyle = `rgba(255, 45, 45, ${(life * life * 0.45).toFixed(3)})`;
        ctx.lineWidth = 12 * life + 3;
        ctx.beginPath();
        ctx.moveTo(a.x, a.y);
        ctx.lineTo(b.x, b.y);
        ctx.stroke();

        ctx.strokeStyle = `rgba(255, 205, 205, ${life.toFixed(3)})`;
        ctx.lineWidth = 3.2 * life + 1;
        ctx.beginPath();
        ctx.moveTo(a.x, a.y);
        ctx.lineTo(b.x, b.y);
        ctx.stroke();
        ctx.restore();
      }

      const head = laserPts[laserPts.length - 1];
      const headLife = Math.max(0, Math.min(1, 1 - (nowSec - head.t) / InkEngineState.LASER_LIFETIME_SEC));
      if (headLife > 0.04) {
        ctx.save();
        ctx.fillStyle = `rgba(255, 45, 45, ${(headLife * 0.55).toFixed(3)})`;
        ctx.beginPath();
        ctx.arc(head.x, head.y, 9 * headLife, 0, Math.PI * 2);
        ctx.fill();
        ctx.fillStyle = `rgba(255, 240, 240, ${headLife.toFixed(3)})`;
        ctx.beginPath();
        ctx.arc(head.x, head.y, 3.5 * headLife, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();
      }
    }
  }, [stageSize]);

  // Only run animation frame while laser trail is non-empty (matches C# 0% idle CPU design)
  const laserRafRef = useRef<number | null>(null);
  const ensureLaserAnimation = useCallback(() => {
    if (laserRafRef.current !== null) return;
    const tick = () => {
      renderCanvas();
      const nowSec = performance.now() / 1000;
      if (inkEngineRef.current.getLaserPoints(nowSec).length > 0) {
        laserRafRef.current = requestAnimationFrame(tick);
      } else {
        laserRafRef.current = null;
      }
    };
    laserRafRef.current = requestAnimationFrame(tick);
  }, [renderCanvas]);

  useEffect(() => {
    renderCanvas();
  }, [renderCanvas, strokeCount]);

  useEffect(() => {
    return () => {
      if (laserRafRef.current !== null) cancelAnimationFrame(laserRafRef.current);
      if (scaleTimerRef.current !== null) window.clearTimeout(scaleTimerRef.current);
    };
  }, []);

  const handleUndo = useCallback(() => {
    if (inkEngineRef.current.undo()) {
      syncCounts();
      renderCanvas();
    }
  }, [renderCanvas, syncCounts]);

  const handleClear = useCallback(() => {
    if (inkEngineRef.current.clearAll()) {
      syncCounts();
      renderCanvas();
    }
  }, [renderCanvas, syncCounts]);

  const handleSaveSnapshot = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const now = new Date();
    const pad2 = (n: number) => String(n).padStart(2, '0');
    const filename = `TrackPadLens_${now.getFullYear()}${pad2(now.getMonth() + 1)}${pad2(now.getDate())}_${pad2(now.getHours())}${pad2(now.getMinutes())}${pad2(now.getSeconds())}.png`;

    // Composite clean lecture slide backdrop + ink layer (omitting cyan lens chrome)
    const exportCanvas = document.createElement('canvas');
    exportCanvas.width = stageSize.width;
    exportCanvas.height = stageSize.height;
    const ctx = exportCanvas.getContext('2d');
    if (!ctx) return;

    ctx.fillStyle = '#0F172A';
    ctx.fillRect(0, 0, exportCanvas.width, exportCanvas.height);
    ctx.strokeStyle = 'rgba(148, 163, 184, 0.14)';
    ctx.lineWidth = 1;
    for (let x = 40; x < exportCanvas.width; x += 40) {
      ctx.beginPath();
      ctx.moveTo(x, 0);
      ctx.lineTo(x, exportCanvas.height);
      ctx.stroke();
    }
    for (let y = 40; y < exportCanvas.height; y += 40) {
      ctx.beginPath();
      ctx.moveTo(0, y);
      ctx.lineTo(exportCanvas.width, y);
      ctx.stroke();
    }
    ctx.drawImage(canvas, 0, 0, exportCanvas.width, exportCanvas.height);

    const link = document.createElement('a');
    link.download = filename;
    link.href = exportCanvas.toDataURL('image/png');
    link.click();

    setSnapshotNotice(`Exported ${filename}`);
    window.setTimeout(() => setSnapshotNotice(null), 2800);
  }, [stageSize]);

  // Global keyboard shortcuts inside sandbox (Alt+D, Ctrl+Z, Ctrl+S, Esc, Shift anchor lock)
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Shift') setShiftHeld(true);
      if (e.altKey && e.key.toLowerCase() === 'd') {
        e.preventDefault();
        setDoodleMode((prev) => !prev);
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') {
        e.preventDefault();
        handleUndo();
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
        e.preventDefault();
        handleSaveSnapshot();
      } else if (e.key === 'Escape') {
        e.preventDefault();
        handleClear();
      }
    };
    const onKeyUp = (e: KeyboardEvent) => {
      if (e.key === 'Shift') setShiftHeld(false);
    };
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
    };
  }, [handleClear, handleSaveSnapshot, handleUndo]);

  const handleStageMouseMove = (e: React.MouseEvent<HTMLDivElement>) => {
    if (anchorLocked) return;
    const rect = stageRef.current?.getBoundingClientRect();
    if (!rect) return;
    setCursorPos({
      x: e.clientX - rect.left,
      y: e.clientY - rect.top,
    });
  };

  useEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      if (!doodleMode) return;
      e.preventDefault();
      const wheelDelta = e.deltaY < 0 ? 120 : -120;
      setLensWidth((prev) => {
        const next = scaleLensWidth(prev, wheelDelta, stageSize.width);
        const mult = (next / BASE_LENS_WIDTH).toFixed(1);
        setScaleBadge(`${mult}x`);
        if (scaleTimerRef.current !== null) window.clearTimeout(scaleTimerRef.current);
        scaleTimerRef.current = window.setTimeout(() => setScaleBadge(null), 500);
        return next;
      });
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [doodleMode, stageSize.width]);

  const feedSimulatedTouchpadSample = (clientX: number, clientY: number, tipSwitch: boolean) => {
    const padEl = padRef.current;
    if (!padEl) return;
    const rect = padEl.getBoundingClientRect();
    const uRaw = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    const vRaw = Math.max(0, Math.min(1, (clientY - rect.top) / rect.height));

    const rawX = Math.round(PAD_LOGICAL_BOUNDS.minX + uRaw * (PAD_LOGICAL_BOUNDS.maxX - PAD_LOGICAL_BOUNDS.minX));
    const rawY = Math.round(PAD_LOGICAL_BOUNDS.minY + vRaw * (PAD_LOGICAL_BOUNDS.maxY - PAD_LOGICAL_BOUNDS.minY));

    const contacts = simulateMultiFinger
      ? [
          {
            contactId: 1,
            tipSwitch,
            confidence: true,
            rawX,
            rawY,
            width: simulateLargePalm ? 460 : 90,
            height: simulateLargePalm ? 440 : 90,
          },
          {
            contactId: 2,
            tipSwitch,
            confidence: true,
            rawX: Math.min(3200, rawX + 350),
            rawY,
            width: 90,
            height: 90,
          },
        ]
      : [
          {
            contactId: 1,
            tipSwitch,
            confidence: true,
            rawX,
            rawY,
            width: simulateLargePalm ? 460 : 90,
            height: simulateLargePalm ? 440 : 90,
          },
        ];

    const palmRes = palmEngineRef.current.evaluate(
      contacts,
      tipSwitch ? contacts.length : 0,
      PAD_LOGICAL_BOUNDS,
      true,
    );
    setLastPalmDecision(palmRes.decision);

    if (palmRes.decision !== 'Accepted' || !palmRes.primaryContact) {
      if (inkEngineRef.current.getActiveStroke()) {
        inkEngineRef.current.endStroke();
        syncCounts();
      }
      filterRef.current.reset();
      lastEmittedRef.current = null;
      setHoverDot((prev) => (prev.visible ? { ...prev, visible: false } : prev));
      renderCanvas();
      return;
    }

    const nowSec = performance.now() / 1000;
    const mapped = mapRawToLens(
      palmRes.primaryContact.rawX,
      palmRes.primaryContact.rawY,
      PAD_LOGICAL_BOUNDS,
      activeLensRef.current,
      calibration,
    );
    const filtered = filterRef.current.filter(mapped.screenX, mapped.screenY, nowSec);
    setHoverDot({ visible: true, x: filtered.x, y: filtered.y, u: mapped.u, v: mapped.v });

    if (!doodleMode) return;

    if (activeTool === 'laser') {
      inkEngineRef.current.addLaserPoint({ x: filtered.x, y: filtered.y, t: nowSec });
      ensureLaserAnimation();
      return;
    }

    const pt = { x: filtered.x, y: filtered.y, t: nowSec };
    if (!inkEngineRef.current.getActiveStroke()) {
      inkEngineRef.current.beginStroke(pt);
      lastEmittedRef.current = { x: filtered.x, y: filtered.y };
      renderCanvas();
      return;
    }

    const prev = lastEmittedRef.current;
    if (!prev || Math.hypot(filtered.x - prev.x, filtered.y - prev.y) >= 1.25) {
      inkEngineRef.current.appendPoint(pt);
      lastEmittedRef.current = { x: filtered.x, y: filtered.y };
      renderCanvas();
    }
  };

  const handlePadPointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    e.currentTarget.setPointerCapture(e.pointerId);
    isPadDrawingRef.current = true;
    feedSimulatedTouchpadSample(e.clientX, e.clientY, true);
  };

  const handlePadPointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!isPadDrawingRef.current) return;
    feedSimulatedTouchpadSample(e.clientX, e.clientY, true);
  };

  const handlePadPointerUp = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!isPadDrawingRef.current) return;
    isPadDrawingRef.current = false;
    feedSimulatedTouchpadSample(e.clientX, e.clientY, false);
  };

  // Also allow drawing directly inside the Lens when Anchor Lock is enabled (convenient single-pointer testing)
  const stageDrawingRef = useRef(false);
  const handleStagePointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (e.button === 2) {
      setRightButtonHeld(true);
      return;
    }
    if (e.button !== 0 || !doodleMode) return;
    const rect = stageRef.current?.getBoundingClientRect();
    if (!rect) return;
    const x = e.clientX - rect.left;
    const y = e.clientY - rect.top;
    const lens = activeLensRef.current;
    if (x >= lens.left && x <= lens.left + lens.width && y >= lens.top && y <= lens.top + lens.height) {
      e.currentTarget.setPointerCapture(e.pointerId);
      stageDrawingRef.current = true;
      filterRef.current.reset();
      const nowSec = performance.now() / 1000;
      const u = (x - lens.left) / lens.width;
      const v = (y - lens.top) / lens.height;
      const filtered = filterRef.current.filter(x, y, nowSec);
      setHoverDot({ visible: true, x: filtered.x, y: filtered.y, u, v });
      if (activeTool === 'laser') {
        inkEngineRef.current.addLaserPoint({ x: filtered.x, y: filtered.y, t: nowSec });
        ensureLaserAnimation();
      } else {
        inkEngineRef.current.beginStroke({ x: filtered.x, y: filtered.y, t: nowSec });
        lastEmittedRef.current = { x: filtered.x, y: filtered.y };
        renderCanvas();
      }
    }
  };

  const handleStagePointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!stageDrawingRef.current) return;
    const rect = stageRef.current?.getBoundingClientRect();
    if (!rect) return;
    const lens = activeLensRef.current;
    const x = Math.max(lens.left, Math.min(lens.left + lens.width, e.clientX - rect.left));
    const y = Math.max(lens.top, Math.min(lens.top + lens.height, e.clientY - rect.top));
    const nowSec = performance.now() / 1000;
    const filtered = filterRef.current.filter(x, y, nowSec);
    setHoverDot({
      visible: true,
      x: filtered.x,
      y: filtered.y,
      u: (x - lens.left) / lens.width,
      v: (y - lens.top) / lens.height,
    });
    if (activeTool === 'laser') {
      inkEngineRef.current.addLaserPoint({ x: filtered.x, y: filtered.y, t: nowSec });
      ensureLaserAnimation();
    } else {
      const prev = lastEmittedRef.current;
      if (!prev || Math.hypot(filtered.x - prev.x, filtered.y - prev.y) >= 1.25) {
        inkEngineRef.current.appendPoint({ x: filtered.x, y: filtered.y, t: nowSec });
        lastEmittedRef.current = { x: filtered.x, y: filtered.y };
        renderCanvas();
      }
    }
  };

  const handleStagePointerUp = (e: React.PointerEvent<HTMLDivElement>) => {
    if (e.button === 2) {
      setRightButtonHeld(false);
      return;
    }
    if (!stageDrawingRef.current) return;
    stageDrawingRef.current = false;
    if (inkEngineRef.current.getActiveStroke()) {
      inkEngineRef.current.endStroke();
      syncCounts();
    }
    filterRef.current.reset();
    lastEmittedRef.current = null;
    setHoverDot((prev) => ({ ...prev, visible: false }));
    renderCanvas();
  };

  return (
    <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
      {/* Primary Lecture Slide & Floating Writing Lens Canvas */}
      <section className="lg:col-span-8 flex flex-col gap-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <h1 className="text-lg font-semibold text-slate-100 tracking-tight">
              Writing Lens &amp; Ink Engine Sandbox
            </h1>
            <p className="text-xs text-slate-400 mt-0.5">
              Browser algorithm verifier running the exact 1-Euro Filter, CoordinateMapper, PalmRejection, and InkEngine math from C#.
            </p>
          </div>
          <div className="flex items-center gap-3 text-xs font-mono text-slate-400 tabular-nums">
            <span>Lens {Math.round(activeLens.width)}×{Math.round(activeLens.height)} px</span>
            <span aria-hidden="true">·</span>
            <span>Ratio {aspectRatio.toFixed(2)}:1</span>
            <span aria-hidden="true">·</span>
            <span data-testid="stroke-count">{strokeCount} strokes</span>
          </div>
        </div>

        {/* Simulated Presentation Viewport */}
        <div
          ref={stageRef}
          data-testid="lens-stage"
          onMouseMove={handleStageMouseMove}
          onPointerDown={handleStagePointerDown}
          onPointerMove={handleStagePointerMove}
          onPointerUp={handleStagePointerUp}
          onContextMenu={(e) => e.preventDefault()}
          className="relative w-full h-[540px] rounded-lg border border-slate-800 bg-[#0F172A] overflow-hidden select-none cursor-crosshair"
        >
          {/* Underlying Presentation Slide Content (demonstrating transparent click-through overlay) */}
          <div
            className="absolute inset-0 pointer-events-none p-8 pb-20 flex flex-col justify-between"
            style={{
              backgroundImage:
                'linear-gradient(to right, rgba(148,163,184,0.06) 1px, transparent 1px), linear-gradient(to bottom, rgba(148,163,184,0.06) 1px, transparent 1px)',
              backgroundSize: '40px 40px',
            }}
          >
            <div className="max-w-xl">
              <p className="text-xs font-mono text-slate-500">Lecture 04 · Signal Processing &amp; Control</p>
              <h2 className="text-2xl font-semibold text-slate-200 mt-2 tracking-tight">
                Adaptive Low-Pass Filtering for High-Rate Digitizers
              </h2>
              <p className="text-sm text-slate-400 mt-2 leading-relaxed">
                Move your pointer to position the cyan Writing Lens, scroll the mouse wheel to scale while preserving physical aspect ratio, hold Shift or right-click to lock the anchor, and draw on the touchpad surface panel or directly inside the Lens.
              </p>
            </div>

            <div className="grid grid-cols-3 gap-6 max-w-2xl border-t border-slate-800/80 pt-5">
              <div>
                <p className="text-xs text-slate-500">Normalization</p>
                <p className="text-sm font-mono text-slate-300 mt-1">u = (x − min) / Δx</p>
              </div>
              <div>
                <p className="text-xs text-slate-500">Adaptive Cutoff</p>
                <p className="text-sm font-mono text-slate-300 mt-1">fc = fmin + β·|dx/dt|</p>
              </div>
              <div>
                <p className="text-xs text-slate-500">Interpolation</p>
                <p className="text-sm font-mono text-slate-300 mt-1">Midpoint Quad Bézier</p>
              </div>
            </div>
          </div>

          {/* Retained Ink + Laser Canvas */}
          <canvas
            ref={canvasRef}
            data-testid="ink-canvas"
            className="absolute inset-0 w-full h-full pointer-events-none"
          />

          {/* Floating Transparent Writing Lens (#00D2FF 1.5px + 4 Corner Alignment Markers) */}
          {doodleMode && (
            <div
              data-testid="writing-lens-frame"
              style={{
                transform: `translate3d(${activeLens.left}px, ${activeLens.top}px, 0)`,
                width: `${activeLens.width}px`,
                height: `${activeLens.height}px`,
              }}
              className={`absolute top-0 left-0 pointer-events-none border-[1.5px] ${
                anchorLocked ? 'border-dashed border-[#38ECFF]' : 'border-[#00D2FF]'
              }`}
            >
              {/* 4 Corner Alignment Markers */}
              <span className="absolute -top-[1.5px] -left-[1.5px] w-3 h-3 border-t-2 border-l-2 border-[#00D2FF]" />
              <span className="absolute -top-[1.5px] -right-[1.5px] w-3 h-3 border-t-2 border-r-2 border-[#00D2FF]" />
              <span className="absolute -bottom-[1.5px] -left-[1.5px] w-3 h-3 border-b-2 border-l-2 border-[#00D2FF]" />
              <span className="absolute -bottom-[1.5px] -right-[1.5px] w-3 h-3 border-b-2 border-r-2 border-[#00D2FF]" />

              {/* Scale Indicator (fades out after 500ms) */}
              {scaleBadge && (
                <div
                  data-testid="lens-scale-badge"
                  className="absolute top-1.5 right-1.5 px-1.5 py-0.5 rounded bg-slate-950/85 text-[#E2F8FF] text-[11px] font-mono tabular-nums"
                >
                  {scaleBadge}
                </div>
              )}
            </div>
          )}

          {/* Tiny Translucent 4px Hover Dot */}
          {hoverDot.visible && (
            <div
              style={{
                transform: `translate3d(${hoverDot.x - 2}px, ${hoverDot.y - 2}px, 0)`,
              }}
              className="absolute top-0 left-0 w-1 h-1 rounded-full bg-[#00D2FF]/80 pointer-events-none"
            />
          )}

          {/* Snapshot Notification */}
          {snapshotNotice && (
            <div className="absolute top-3 left-3 px-3 py-1.5 rounded bg-slate-950/90 border border-slate-700 text-xs font-mono text-cyan-200 pointer-events-none">
              {snapshotNotice}
            </div>
          )}

          {/* Phase 8 Minimalist Dark Translucent Pill Toolbar */}
          <div
            onPointerDown={(e) => e.stopPropagation()}
            onMouseMove={(e) => e.stopPropagation()}
            className="absolute bottom-4 left-1/2 -translate-x-1/2 flex items-center gap-1 px-2.5 py-1.5 rounded-full bg-[#0F172A]/90 border border-slate-700/80 backdrop-blur-md"
          >
            <button
              type="button"
              onClick={() => setDoodleMode((d) => !d)}
              aria-label="Toggle Doodle Mode"
              title="Toggle Doodle Mode (Alt+D)"
              className={`h-8 px-2.5 rounded-full text-xs font-medium transition-colors whitespace-nowrap shrink-0 flex items-center gap-1.5 ${
                doodleMode
                  ? 'bg-[#00D2FF]/15 text-[#00D2FF] border border-[#00D2FF]/60'
                  : 'text-slate-400 hover:bg-slate-800 border border-transparent'
              }`}
            >
              <Crosshair className="w-3.5 h-3.5" />
              <span>{doodleMode ? 'Doodle ON' : 'Doodle OFF'}</span>
            </button>

            {!compactToolbar && (
              <>
                <span className="w-px h-4 bg-slate-700 mx-1" aria-hidden="true" />

                {/* Pen Colors */}
                {[
                  { id: 'red', label: 'Red Pen', hex: PEN_COLORS.red, tool: 'pen' as DrawingTool },
                  { id: 'blue', label: 'Blue Pen', hex: PEN_COLORS.blue, tool: 'pen' as DrawingTool },
                  { id: 'highlighter', label: 'Yellow Highlighter', hex: PEN_COLORS.highlighter, tool: 'highlighter' as DrawingTool },
                  { id: 'white', label: 'White Pen', hex: PEN_COLORS.white, tool: 'pen' as DrawingTool },
                  { id: 'black', label: 'Black Pen', hex: PEN_COLORS.black, tool: 'pen' as DrawingTool },
                ].map((swatch) => {
                  const selected =
                    swatch.tool === 'highlighter'
                      ? activeTool === 'highlighter'
                      : activeTool === 'pen' && activeColor === swatch.hex;
                  return (
                    <button
                      key={swatch.id}
                      type="button"
                      aria-label={swatch.label}
                      title={swatch.label}
                      onClick={() => {
                        setActiveTool(swatch.tool);
                        setActiveColor(swatch.hex);
                      }}
                      className={`w-7 h-7 rounded-full flex items-center justify-center transition-colors ${
                        selected ? 'ring-2 ring-[#00D2FF] bg-slate-800' : 'hover:bg-slate-800'
                      }`}
                    >
                      <span
                        className="w-3.5 h-3.5 rounded-full border border-slate-500/70"
                        style={{
                          backgroundColor: swatch.hex,
                          opacity: swatch.tool === 'highlighter' ? 0.65 : 1,
                        }}
                      />
                    </button>
                  );
                })}

                <span className="w-px h-4 bg-slate-700 mx-1" aria-hidden="true" />

                {/* Stroke Thickness */}
                {([2, 4, 7] as ThicknessPreset[]).map((sz) => (
                  <button
                    key={sz}
                    type="button"
                    aria-label={`Stroke Thickness ${sz}px`}
                    title={`Stroke Thickness ${sz}px`}
                    onClick={() => setThickness(sz)}
                    className={`w-7 h-7 rounded-full flex items-center justify-center transition-colors ${
                      thickness === sz ? 'ring-2 ring-[#00D2FF] bg-slate-800' : 'hover:bg-slate-800'
                    }`}
                  >
                    <span
                      className="rounded-full bg-slate-200"
                      style={{ width: `${sz + 3}px`, height: `${sz + 3}px` }}
                    />
                  </button>
                ))}

                <span className="w-px h-4 bg-slate-700 mx-1" aria-hidden="true" />

                {/* Laser Pointer */}
                <button
                  type="button"
                  aria-label="Laser Pointer"
                  title="Laser Pointer (1.5s fade)"
                  onClick={() => setActiveTool('laser')}
                  className={`w-8 h-8 rounded-full flex items-center justify-center transition-colors ${
                    activeTool === 'laser'
                      ? 'ring-2 ring-[#00D2FF] bg-red-500/20 text-red-400'
                      : 'text-red-400 hover:bg-slate-800'
                  }`}
                >
                  <Sparkles className="w-4 h-4" />
                </button>

                {/* Undo */}
                <button
                  type="button"
                  aria-label="Undo Stroke"
                  title="Undo (Ctrl+Z)"
                  onClick={handleUndo}
                  disabled={undoDepth === 0}
                  className="w-8 h-8 rounded-full flex items-center justify-center text-slate-200 hover:bg-slate-800 disabled:opacity-35 transition-colors"
                >
                  <RotateCcw className="w-4 h-4" />
                </button>

                {/* Clear */}
                <button
                  type="button"
                  aria-label="Clear All Ink"
                  title="Clear All Ink (Esc, reversible with Ctrl+Z)"
                  onClick={handleClear}
                  className="w-8 h-8 rounded-full flex items-center justify-center text-slate-200 hover:bg-slate-800 transition-colors"
                >
                  <Trash2 className="w-4 h-4" />
                </button>

                {/* Save Snapshot */}
                <button
                  type="button"
                  aria-label="Save Snapshot"
                  title="Save Snapshot (Ctrl+S)"
                  onClick={handleSaveSnapshot}
                  className="w-8 h-8 rounded-full flex items-center justify-center text-slate-200 hover:bg-slate-800 transition-colors"
                >
                  <Camera className="w-4 h-4" />
                </button>
              </>
            )}

            {/* Compact Toggle */}
            <button
              type="button"
              aria-label="Toggle Compact Toolbar"
              title="Compact Mode"
              onClick={() => setCompactToolbar((c) => !c)}
              className="w-7 h-7 rounded-full flex items-center justify-center text-slate-400 hover:bg-slate-800 transition-colors"
            >
              {compactToolbar ? <ChevronRight className="w-4 h-4" /> : <ChevronLeft className="w-4 h-4" />}
            </button>
          </div>
        </div>

        {/* Concise shortcut strip */}
        <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-slate-400">
          <div className="flex flex-wrap items-center gap-3">
            <span>Alt+D Toggle Doodle</span>
            <span aria-hidden="true">·</span>
            <span>Wheel Resize Lens</span>
            <span aria-hidden="true">·</span>
            <span>Shift / Right-Click Anchor Lock</span>
            <span aria-hidden="true">·</span>
            <span>Ctrl+Z Undo</span>
            <span aria-hidden="true">·</span>
            <span>Esc Reversible Clear</span>
          </div>
          <button
            type="button"
            onClick={() => setManualLock((l) => !l)}
            className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded border text-xs font-medium transition-colors whitespace-nowrap shrink-0 ${
              anchorLocked
                ? 'border-[#00D2FF]/60 bg-[#00D2FF]/10 text-[#00D2FF]'
                : 'border-slate-700 bg-slate-900 text-slate-300 hover:border-slate-600'
            }`}
          >
            {anchorLocked ? <Lock className="w-3.5 h-3.5" /> : <Unlock className="w-3.5 h-3.5" />}
            <span>{anchorLocked ? 'Anchor Locked' : 'Lock Lens Anchor'}</span>
          </button>
        </div>
      </section>

      {/* Right Column: Simulated Absolute Precision Touchpad Surface & Parameter Controls */}
      <aside className="lg:col-span-4 flex flex-col gap-5">
        {/* Absolute Touchpad Input Pad */}
        <div className="rounded-lg border border-slate-800 bg-[#111827] p-4">
          <div className="flex items-center justify-between mb-2">
            <h2 className="text-sm font-semibold text-slate-100">Absolute Touchpad Surface</h2>
            <span className="text-xs font-mono text-slate-400 tabular-nums">
              {PHYSICAL_WIDTH_MM}×{PHYSICAL_HEIGHT_MM} mm
            </span>
          </div>
          <p className="text-xs text-slate-400 mb-3">
            Drag inside this pad to feed absolute logical coordinates (0..3200 × 0..2000) into the active Lens through PalmRejection and the 1-Euro Filter.
          </p>

          <div
            ref={padRef}
            data-testid="touchpad-pad"
            onPointerDown={handlePadPointerDown}
            onPointerMove={handlePadPointerMove}
            onPointerUp={handlePadPointerUp}
            className="relative w-full h-40 rounded border border-slate-700 bg-[#0B0F17] cursor-crosshair touch-none overflow-hidden"
          >
            {/* Active calibrated area outline */}
            <div
              style={{
                left: `${calibration.activeLeft * 100}%`,
                top: `${calibration.activeTop * 100}%`,
                width: `${(calibration.activeRight - calibration.activeLeft) * 100}%`,
                height: `${(calibration.activeBottom - calibration.activeTop) * 100}%`,
              }}
              className="absolute border border-[#00D2FF]/50 bg-[#00D2FF]/[0.04] pointer-events-none"
            />
            <div className="absolute inset-0 flex items-center justify-center pointer-events-none">
              <span className="text-xs font-mono text-slate-500">
                {hoverDot.visible
                  ? `u=${hoverDot.u.toFixed(3)}  v=${hoverDot.v.toFixed(3)}`
                  : 'Drag here to write inside Lens'}
              </span>
            </div>
          </div>

          <div className="mt-3 flex items-center justify-between text-xs font-mono text-slate-400 tabular-nums">
            <span>Palm Filter: <strong className="text-slate-200 font-medium">{lastPalmDecision}</strong></span>
            <span>Undo Depth: {undoDepth}</span>
          </div>
        </div>

        {/* 1-Euro Filter, Anchor & Palm Rejection Controls */}
        <div className="rounded-lg border border-slate-800 bg-[#111827] p-4 flex flex-col gap-4">
          <div className="flex items-center gap-2 text-sm font-semibold text-slate-100">
            <SlidersHorizontal className="w-4 h-4 text-[#00D2FF]" />
            <span>Pipeline Controls</span>
          </div>

          {/* 1-Euro sliders */}
          <div className="flex flex-col gap-2.5 text-xs">
            <label className="flex flex-col gap-1">
              <span className="flex justify-between text-slate-300">
                <span>1-Euro Min Cutoff (Hz)</span>
                <span className="font-mono text-slate-400">{minCutoff.toFixed(2)}</span>
              </span>
              <input
                type="range"
                min={0.2}
                max={8.0}
                step={0.1}
                value={minCutoff}
                onChange={(e) => setMinCutoff(Number(e.target.value))}
                className="accent-[#00D2FF]"
              />
            </label>

            <label className="flex flex-col gap-1">
              <span className="flex justify-between text-slate-300">
                <span>1-Euro Beta (Speed Slope)</span>
                <span className="font-mono text-slate-400">{beta.toFixed(3)}</span>
              </span>
              <input
                type="range"
                min={0}
                max={0.2}
                step={0.005}
                value={beta}
                onChange={(e) => setBeta(Number(e.target.value))}
                className="accent-[#00D2FF]"
              />
            </label>

            <label className="flex flex-col gap-1">
              <span className="flex justify-between text-slate-300">
                <span>Derivative Cutoff (Hz)</span>
                <span className="font-mono text-slate-400">{dCutoff.toFixed(2)}</span>
              </span>
              <input
                type="range"
                min={0.2}
                max={5.0}
                step={0.1}
                value={dCutoff}
                onChange={(e) => setDCutoff(Number(e.target.value))}
                className="accent-[#00D2FF]"
              />
            </label>
          </div>

          <div className="border-t border-slate-800 pt-3 flex flex-col gap-2.5 text-xs">
            <div className="flex items-center justify-between">
              <span className="text-slate-300">Anchor Alignment</span>
              <div className="inline-flex rounded bg-slate-900 p-0.5 border border-slate-800">
                <button
                  type="button"
                  onClick={() => setAnchorMode('centered')}
                  className={`px-2.5 py-1 rounded text-xs font-medium transition-colors whitespace-nowrap shrink-0 ${
                    anchorMode === 'centered' ? 'bg-slate-700 text-white' : 'text-slate-400 hover:text-slate-200'
                  }`}
                >
                  Centered
                </button>
                <button
                  type="button"
                  onClick={() => setAnchorMode('top-left')}
                  className={`px-2.5 py-1 rounded text-xs font-medium transition-colors whitespace-nowrap shrink-0 ${
                    anchorMode === 'top-left' ? 'bg-slate-700 text-white' : 'text-slate-400 hover:text-slate-200'
                  }`}
                >
                  Top-Left
                </button>
              </div>
            </div>

            <label className="flex items-center justify-between gap-2 text-slate-300 cursor-pointer">
              <span>Simulate 2-Finger Gesture (Palm Lockout)</span>
              <input
                type="checkbox"
                checked={simulateMultiFinger}
                onChange={(e) => setSimulateMultiFinger(e.target.checked)}
                className="accent-[#00D2FF]"
              />
            </label>

            <label className="flex items-center justify-between gap-2 text-slate-300 cursor-pointer">
              <span>Simulate Oversized Palm Contact</span>
              <input
                type="checkbox"
                checked={simulateLargePalm}
                onChange={(e) => setSimulateLargePalm(e.target.checked)}
                className="accent-[#00D2FF]"
              />
            </label>

            <div className="flex items-center gap-4 pt-1">
              <label className="flex items-center gap-1.5 text-slate-300 cursor-pointer">
                <input
                  type="checkbox"
                  checked={calibration.invertX}
                  onChange={(e) => setCalibration((c) => ({ ...c, invertX: e.target.checked }))}
                  className="accent-[#00D2FF]"
                />
                <span>Invert X</span>
              </label>
              <label className="flex items-center gap-1.5 text-slate-300 cursor-pointer">
                <input
                  type="checkbox"
                  checked={calibration.invertY}
                  onChange={(e) => setCalibration((c) => ({ ...c, invertY: e.target.checked }))}
                  className="accent-[#00D2FF]"
                />
                <span>Invert Y</span>
              </label>
            </div>
          </div>
        </div>
      </aside>
    </div>
  );
};

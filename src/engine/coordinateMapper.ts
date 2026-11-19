/**
 * TypeScript mirror of TrackPadLens/Processing/CoordinateMapper.cs and CalibrationManager.cs
 */

export type AnchorMode = 'centered' | 'top-left';

export interface LogicalBounds {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
}

export interface LensRect {
  left: number;
  top: number;
  width: number;
  height: number;
}

export interface CalibrationConfig {
  activeLeft: number;
  activeTop: number;
  activeRight: number;
  activeBottom: number;
  usePhysicalAspectRatio: boolean;
  manualAspectRatio: number;
  invertX: boolean;
  invertY: boolean;
}

export const DEFAULT_CALIBRATION: CalibrationConfig = {
  activeLeft: 0,
  activeTop: 0,
  activeRight: 1,
  activeBottom: 1,
  usePhysicalAspectRatio: true,
  manualAspectRatio: 1.6,
  invertX: false,
  invertY: false,
};

export function clamp(val: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, val));
}

export function normalizeRawCoordinates(
  rawX: number,
  rawY: number,
  bounds: LogicalBounds,
  calibration?: CalibrationConfig,
): { u: number; v: number } {
  const spanX = bounds.maxX - bounds.minX;
  const spanY = bounds.maxY - bounds.minY;
  if (spanX <= 0 || spanY <= 0) {
    return { u: 0, v: 0 };
  }

  let u = (rawX - bounds.minX) / spanX;
  let v = (rawY - bounds.minY) / spanY;

  if (calibration) {
    const activeSpanX = Math.max(0.05, calibration.activeRight - calibration.activeLeft);
    const activeSpanY = Math.max(0.05, calibration.activeBottom - calibration.activeTop);
    u = (u - calibration.activeLeft) / activeSpanX;
    v = (v - calibration.activeTop) / activeSpanY;

    if (calibration.invertX) u = 1 - u;
    if (calibration.invertY) v = 1 - v;
  }

  return {
    u: clamp(u, 0, 1),
    v: clamp(v, 0, 1),
  };
}

export function mapRawToLens(
  rawX: number,
  rawY: number,
  bounds: LogicalBounds,
  lens: LensRect,
  calibration?: CalibrationConfig,
): { screenX: number; screenY: number; u: number; v: number } {
  const { u, v } = normalizeRawCoordinates(rawX, rawY, bounds, calibration);
  return {
    screenX: lens.left + u * lens.width,
    screenY: lens.top + v * lens.height,
    u,
    v,
  };
}

export function physicalToDip(
  physicalX: number,
  physicalY: number,
  dpiScaleX: number,
  dpiScaleY: number,
): { x: number; y: number } {
  const sx = dpiScaleX > 0.01 ? dpiScaleX : 1;
  const sy = dpiScaleY > 0.01 ? dpiScaleY : 1;
  return { x: physicalX / sx, y: physicalY / sy };
}

export function dipToPhysical(
  dipX: number,
  dipY: number,
  dpiScaleX: number,
  dpiScaleY: number,
): { x: number; y: number } {
  const sx = dpiScaleX > 0.01 ? dpiScaleX : 1;
  const sy = dpiScaleY > 0.01 ? dpiScaleY : 1;
  return { x: Math.round(dipX * sx), y: Math.round(dipY * sy) };
}

export function resolveAspectRatio(
  physicalWidthMm: number | null,
  physicalHeightMm: number | null,
  calibration: CalibrationConfig,
): number {
  const base =
    calibration.usePhysicalAspectRatio &&
    physicalWidthMm !== null &&
    physicalHeightMm !== null &&
    physicalWidthMm > 0 &&
    physicalHeightMm > 0
      ? physicalWidthMm / physicalHeightMm
      : calibration.manualAspectRatio;

  const activeW = Math.max(0.15, calibration.activeRight - calibration.activeLeft);
  const activeH = Math.max(0.15, calibration.activeBottom - calibration.activeTop);
  return clamp(base * (activeW / activeH), 0.4, 4.0);
}

export function scaleLensWidth(
  currentWidth: number,
  wheelDelta: number,
  usableScreenWidth: number,
): number {
  const maxWidth = Math.max(100, usableScreenWidth * 0.9);
  const notches = wheelDelta / 120;
  const factor = Math.pow(1.08, notches);
  return clamp(currentWidth * factor, 100, maxWidth);
}

export function computeLensRect(
  cursorX: number,
  cursorY: number,
  lensWidth: number,
  aspectRatio: number,
  anchorMode: AnchorMode,
  offsetX: number,
  offsetY: number,
  viewport: LensRect,
): LensRect {
  const safeAspect = clamp(aspectRatio, 0.4, 4.0);
  let width = clamp(lensWidth, 100, Math.max(100, viewport.width * 0.9));
  let height = width / safeAspect;

  if (height > viewport.height * 0.9) {
    height = Math.max(60, viewport.height * 0.9);
    width = height * safeAspect;
  }

  let left = anchorMode === 'centered' ? cursorX - width * 0.5 + offsetX : cursorX + offsetX;
  let top = anchorMode === 'centered' ? cursorY - height * 0.5 + offsetY : cursorY + offsetY;

  left = clamp(left, viewport.left, Math.max(viewport.left, viewport.left + viewport.width - width));
  top = clamp(top, viewport.top, Math.max(viewport.top, viewport.top + viewport.height - height));

  return { left, top, width, height };
}

import test from 'node:test';
import assert from 'node:assert/strict';
import { OneEuroFilter } from './oneEuroFilter';
import {
  computeLensRect,
  DEFAULT_CALIBRATION,
  dipToPhysical,
  mapRawToLens,
  normalizeRawCoordinates,
  physicalToDip,
  resolveAspectRatio,
  scaleLensWidth,
} from './coordinateMapper';
import { PalmRejectionEngine } from './palmRejection';
import { InkEngineState, PEN_COLORS } from './inkEngine';
import {
  calculatePhysicalSpanMm,
  classifyHidDevicePath,
  decodeHidUnitExponent,
} from './hidDescriptorParser';
import { buildZipArchive } from './zipBuilder';

test('HID unit exponent & physical dimension decoding', () => {
  assert.equal(decodeHidUnitExponent(0x0e), -2);
  assert.equal(decodeHidUnitExponent(0x0d), -3);
  assert.equal(decodeHidUnitExponent(0x02), 2);

  // 1200 * 10^-2 cm = 120 mm
  const siMm = calculatePhysicalSpanMm(0, 1200, 0x11, 0x0e);
  assert.ok(siMm !== null && Math.abs(siMm - 120.0) < 1e-4);

  // 4000 * 10^-3 in = 101.6 mm
  const enMm = calculatePhysicalSpanMm(0, 4000, 0x13, 0x0d);
  assert.ok(enMm !== null && Math.abs(enMm - 101.6) < 1e-4);

  // Degenerate or unknown unit returns null
  assert.equal(calculatePhysicalSpanMm(0, 0, 0x11, 0x0e), null);
  assert.equal(calculatePhysicalSpanMm(0, 1000, 0x00, 0x00), null);
});

test('HID device path classification separates Precision Touchpad, USB, and Bluetooth mice', () => {
  assert.equal(
    classifyHidDevicePath('\\\\?\\HID#VEN_ELAN&DEV_1200', true),
    'BuiltInPrecisionTouchpad',
  );
  assert.equal(
    classifyHidDevicePath('\\\\?\\HID#{00001812-0000-1000-8000-00805F9B34FB}', false),
    'ExternalBluetoothMouse',
  );
  assert.equal(
    classifyHidDevicePath('\\\\?\\HID#VID_046D&PID_C077&MI_00', false),
    'ExternalUsbMouse',
  );
  assert.equal(
    classifyHidDevicePath('\\\\?\\HID#ACPI_ELAN_TOUCH', false),
    'OtherPointingDevice',
  );
});

test('Coordinate normalization, active crop, axis inversion, DPI, and negative multi-monitor bounds', () => {
  const bounds = { minX: 100, maxX: 1100, minY: 200, maxY: 1000 };
  const clampedLow = normalizeRawCoordinates(-200, -100, bounds);
  assert.deepEqual(clampedLow, { u: 0, v: 0 });

  const clampedHigh = normalizeRawCoordinates(9999, 9999, bounds);
  assert.deepEqual(clampedHigh, { u: 1, v: 1 });

  const lens = { left: 300, top: 150, width: 400, height: 250 };
  const mapped = mapRawToLens(600, 600, bounds, lens);
  assert.equal(mapped.screenX, 500);
  assert.equal(mapped.screenY, 275);

  // Active crop + Invert X
  const cropped = normalizeRawCoordinates(200, 500, { minX: 0, maxX: 1000, minY: 0, maxY: 1000 }, {
    ...DEFAULT_CALIBRATION,
    activeLeft: 0.2,
    activeRight: 0.8,
    activeTop: 0.1,
    activeBottom: 0.9,
    invertX: true,
  });
  assert.ok(Math.abs(cropped.u - 1.0) < 1e-6);
  assert.ok(Math.abs(cropped.v - 0.5) < 1e-6);

  // DPI + negative secondary monitor coordinates
  const dip = physicalToDip(-1920, -300, 1.5, 1.5);
  assert.deepEqual(dip, { x: -1280, y: -200 });
  assert.deepEqual(dipToPhysical(dip.x, dip.y, 1.5, 1.5), { x: -1920, y: -300 });

  // Lens clamping inside negative virtual desktop
  const rect = computeLensRect(-1910, 500, 400, 1.6, 'centered', 0, 0, {
    left: -1920,
    top: 0,
    width: 3840,
    height: 1080,
  });
  assert.equal(rect.left, -1920);
  assert.equal(rect.width, 400);
  assert.equal(rect.height, 250);
});

test('Aspect ratio & wheel scaling preserve 100px..90% bounds', () => {
  const ratio = resolveAspectRatio(128, 80, DEFAULT_CALIBRATION);
  assert.ok(Math.abs(ratio - 1.6) < 1e-6);

  assert.equal(scaleLensWidth(105, -240, 1920), 100);
  assert.equal(scaleLensWidth(1700, +600, 1920), 1728);
});

test('1-Euro filter suppresses jitter and tracks rapid movement without NaN on duplicate timestamps', () => {
  const filter = new OneEuroFilter(1.0, 0.01, 1.0);
  assert.equal(filter.filter(125.5, 10.0), 125.5);

  let maxDev = 0;
  let t = 0;
  const jitterFilter = new OneEuroFilter(1.0, 0.01, 1.0);
  for (let i = 0; i < 80; i++) {
    const noisy = 500 + (i % 2 === 0 ? 4 : -4);
    const out = jitterFilter.filter(noisy, t);
    if (i >= 40) maxDev = Math.max(maxDev, Math.abs(out - 500));
    t += 1 / 120;
  }
  assert.ok(maxDev < 1.0, `Expected maxDev < 1.0, got ${maxDev}`);

  // Duplicate timestamp guard
  const dupFilter = new OneEuroFilter();
  const a = dupFilter.filter(100, 1.0);
  const b = dupFilter.filter(250, 1.0);
  assert.equal(a, b);
});

test('Adaptive palm & multi-finger rejection enforces lockout until full lift', () => {
  const palm = new PalmRejectionEngine();
  const bounds = { minX: 0, maxX: 3000, minY: 0, maxY: 2000 };

  // Two fingers trigger lockout
  const r1 = palm.evaluate(
    [
      { contactId: 1, tipSwitch: true, confidence: true, rawX: 500, rawY: 500 },
      { contactId: 2, tipSwitch: true, confidence: true, rawX: 900, rawY: 500 },
    ],
    2,
    bounds,
    false,
  );
  assert.equal(r1.decision, 'RejectedMultiFinger');

  // Remaining finger stays locked out
  const r2 = palm.evaluate(
    [{ contactId: 2, tipSwitch: true, confidence: true, rawX: 905, rawY: 500 }],
    1,
    bounds,
    false,
  );
  assert.equal(r2.decision, 'RejectedMultiFinger');

  // Full lift resets lockout
  const r3 = palm.evaluate(
    [{ contactId: 2, tipSwitch: false, confidence: true, rawX: 905, rawY: 500 }],
    0,
    bounds,
    false,
  );
  assert.equal(r3.decision, 'NoActiveContact');

  // Oversized contact skipped when descriptor lacks contact area, rejected when supported
  const bigContact = [
    { contactId: 5, tipSwitch: true, confidence: true, rawX: 1000, rawY: 1000, width: 500, height: 400 },
  ];
  assert.equal(palm.evaluate(bigContact, 1, bounds, false).decision, 'Accepted');
  palm.reset();
  assert.equal(palm.evaluate(bigContact, 1, bounds, true).decision, 'RejectedPalmArea');
});

test('InkEngine maintains bounded undo history, reversible Clear All, highlighter opacity, and 1.5s laser decay', () => {
  const ink = new InkEngineState();
  ink.activeTool = 'highlighter';
  ink.beginStroke({ x: 10, y: 10, t: 1.0 });
  const active = ink.getActiveStroke();
  assert.ok(active !== null);
  assert.equal(active.opacity, 0.4);
  assert.equal(active.color, PEN_COLORS.highlighter);
  ink.endStroke();

  ink.activeTool = 'pen';
  for (let i = 0; i < InkEngineState.MAX_UNDO_HISTORY + 10; i++) {
    ink.beginStroke({ x: i, y: i, t: 2 + i * 0.01 });
    ink.endStroke();
  }
  assert.equal(ink.getUndoDepth(), InkEngineState.MAX_UNDO_HISTORY);

  const countBefore = ink.getStrokes().length;
  assert.equal(ink.clearAll(), true);
  assert.equal(ink.getStrokes().length, 0);
  assert.equal(ink.undo(), true);
  assert.equal(ink.getStrokes().length, countBefore);

  ink.addLaserPoint({ x: 50, y: 50, t: 10.0 });
  ink.addLaserPoint({ x: 60, y: 60, t: 11.0 });
  assert.equal(ink.getLaserPoints(11.6).length, 1);
  assert.equal(ink.getLaserPoints(13.0).length, 0);
});

test('PKZIP archive builder writes valid Local Header, Central Directory, and EOCD signatures', () => {
  const zip = buildZipArchive([
    { name: 'TrackPadLens/README.md', content: Buffer.from('# TrackPadLens\n', 'utf8') },
    { name: 'TrackPadLens/build.ps1', content: Buffer.from('dotnet build -c Debug\n', 'utf8') },
  ]);

  assert.equal(zip.readUInt32LE(0), 0x04034b50); // PK\x03\x04
  // EOCD is the last 22 bytes when comment length is 0
  assert.equal(zip.readUInt32LE(zip.length - 22), 0x06054b50); // PK\x05\x06
  assert.equal(zip.readUInt16LE(zip.length - 12), 2); // 2 entries
});

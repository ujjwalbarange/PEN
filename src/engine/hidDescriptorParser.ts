/**
 * TypeScript mirror of TrackPadLens/Hardware/DeviceCapabilities.cs (HidUnitConverter)
 * and TrackPadLens/Native/DeviceEnumerator.cs path classification.
 */

export function decodeHidUnitExponent(rawUnitExp: number): number {
  const nibble = rawUnitExp & 0x0f;
  return nibble >= 8 ? nibble - 16 : nibble;
}

export function calculatePhysicalSpanMm(
  physicalMin: number,
  physicalMax: number,
  hidUnit: number,
  hidUnitExp: number,
): number | null {
  if (physicalMax <= physicalMin) return null;

  const system = hidUnit & 0x0f;
  const lengthNibble = (hidUnit >> 4) & 0x0f;
  if (lengthNibble !== 1) return null;

  const exp = decodeHidUnitExponent(hidUnitExp);
  const span = (physicalMax - physicalMin) * Math.pow(10, exp);

  if (system === 0x1) return span * 10.0; // SI Linear cm -> mm
  if (system === 0x3) return span * 25.4; // English Linear inch -> mm
  return null;
}

export type DeviceCategory =
  | 'BuiltInPrecisionTouchpad'
  | 'ExternalUsbMouse'
  | 'ExternalBluetoothMouse'
  | 'OtherPointingDevice';

export function classifyHidDevicePath(
  devicePath: string,
  isDigitizerTouchpad: boolean,
): DeviceCategory {
  if (isDigitizerTouchpad) return 'BuiltInPrecisionTouchpad';
  const upper = devicePath.toUpperCase();
  if (upper.includes('BTHENUM') || upper.includes('BTHLE') || upper.includes('{00001812')) {
    return 'ExternalBluetoothMouse';
  }
  if (
    upper.includes('ACPI') ||
    upper.includes('I2C') ||
    upper.includes('SYNA') ||
    upper.includes('ELAN') ||
    upper.includes('MSFT0001') ||
    upper.includes('DLL0')
  ) {
    return 'OtherPointingDevice';
  }
  if (upper.includes('USB') || upper.includes('HID#VID_')) {
    return 'ExternalUsbMouse';
  }
  return 'OtherPointingDevice';
}

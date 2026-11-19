import React, { useState } from 'react';
import { Check, Copy, Download, Terminal } from 'lucide-react';
import { calculatePhysicalSpanMm, decodeHidUnitExponent } from '../engine/hidDescriptorParser';

interface CommandItem {
  id: string;
  label: string;
  description: string;
  command: string;
}

const BUILD_COMMANDS: CommandItem[] = [
  {
    id: 'debug-script',
    label: 'Debug Build & Root Staging (Recommended for Debugging)',
    description: 'Compiles x64 Debug with full .pdb symbols and stages a launchable TrackPadLens.exe + TrackPadLens.pdb directly in TrackPadLens/.',
    command: '.\\build.ps1 -Mode Debug',
  },
  {
    id: 'debug-cmd',
    label: 'One-Click Command Prompt Debug Build',
    description: 'Batch wrapper around build.ps1 -Mode Debug (safe: never overwrites root executable on failure).',
    command: 'build-debug.cmd',
  },
  {
    id: 'dotnet-debug',
    label: 'Direct .NET CLI Debug Build',
    description: 'Uses the MSBuild CopyExecutableToRoot post-build target to stage TrackPadLens.exe and TrackPadLens.pdb to root.',
    command: 'dotnet build TrackPadLens.csproj -c Debug -p:Platform=x64 -r win-x64 --no-self-contained',
  },
  {
    id: 'dotnet-release',
    label: 'Release Build (Framework-Dependent)',
    description: 'Fast optimized Release build requiring .NET 8 Desktop Runtime on the target PC.',
    command: 'dotnet build TrackPadLens.csproj -c Release -p:Platform=x64 -r win-x64 --no-self-contained',
  },
  {
    id: 'dotnet-publish',
    label: 'Standalone Self-Contained Single-File Publish (win-x64)',
    description: 'Produces standalone publish\\TrackPadLens.exe and copies it to the project root.',
    command: 'dotnet publish TrackPadLens.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish',
  },
  {
    id: 'dotnet-test',
    label: 'Run xUnit Automated Test Suite',
    description: 'Executes HID descriptor, coordinate mapping, DPI, 1-Euro Filter, palm rejection, hotkey, and config tests.',
    command: 'dotnet test Tests\\TrackPadLens.Tests.csproj -c Release',
  },
];

export const BuildAndHidGuideView: React.FC = () => {
  const [copiedId, setCopiedId] = useState<string | null>(null);

  // Interactive USB HID 1.11 Unit & Physical Dimension Calculator
  const [physMin, setPhysMin] = useState(0);
  const [physMax, setPhysMax] = useState(1280);
  const [hidUnitHex, setHidUnitHex] = useState('0x11'); // SI Linear cm
  const [hidExpHex, setHidExpHex] = useState('0x0E'); // -2

  const parsedUnit = Number.parseInt(hidUnitHex, 16) || 0;
  const parsedExp = Number.parseInt(hidExpHex, 16) || 0;
  const decodedExp = decodeHidUnitExponent(parsedExp);
  const physicalMm = calculatePhysicalSpanMm(physMin, physMax, parsedUnit, parsedExp);

  const copyCommand = async (item: CommandItem) => {
    try {
      await navigator.clipboard.writeText(item.command);
      setCopiedId(item.id);
      window.setTimeout(() => setCopiedId(null), 1800);
    } catch {
      // Ignore clipboard failures in restricted environments
    }
  };

  return (
    <div className="flex flex-col gap-6">
      {/* Honest Environment & Build Note */}
      <section className="rounded-lg border border-slate-800 bg-[#111827] p-5 flex flex-wrap items-center justify-between gap-4">
        <div className="max-w-3xl">
          <h1 className="text-lg font-semibold text-slate-100 tracking-tight">
            Building TrackPadLens.exe for Debugging on Windows 10 / 11 (x64)
          </h1>
          <p className="text-xs text-slate-300 mt-1.5 leading-relaxed">
            This web workbench runs inside a Linux x86_64 sandbox without the Windows .NET 8 Desktop SDK or WPF/WinForms targeting packs, so a native Windows PE binary (<code className="text-cyan-300">TrackPadLens.exe</code>) cannot be compiled inside this browser container. Download the complete source archive below on a Windows 10/11 machine with the .NET 8 SDK installed and run <code className="text-cyan-300">build-debug.cmd</code> to generate <code className="text-cyan-300">TrackPadLens.exe</code> + <code className="text-cyan-300">TrackPadLens.pdb</code> directly in the project root.
          </p>
        </div>
        <a
          href="/api/project/download-zip"
          download="TrackPadLens-Windows-Source.zip"
          className="inline-flex items-center gap-2 px-4 py-2 rounded-md bg-[#00D2FF] text-[#0B0F17] text-xs font-semibold hover:bg-[#38DCFF] transition-colors whitespace-nowrap shrink-0"
        >
          <Download className="w-4 h-4" />
          <span>Download Complete Windows Source (.zip)</span>
        </a>
      </section>

      {/* Copyable Build Commands */}
      <section className="rounded-lg border border-slate-800 bg-[#111827] overflow-hidden">
        <div className="px-5 py-3.5 border-b border-slate-800 flex items-center gap-2">
          <Terminal className="w-4 h-4 text-[#00D2FF]" />
          <h2 className="text-sm font-semibold text-slate-100">Windows Build, Debug &amp; Test Commands</h2>
        </div>

        <div className="divide-y divide-slate-800/80">
          {BUILD_COMMANDS.map((item) => (
            <div key={item.id} className="px-5 py-3.5 flex flex-col gap-2">
              <div className="flex items-center justify-between gap-3">
                <div>
                  <p className="text-sm font-medium text-slate-100">{item.label}</p>
                  <p className="text-xs text-slate-400">{item.description}</p>
                </div>
                <button
                  type="button"
                  onClick={() => copyCommand(item)}
                  className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded border border-slate-700 text-xs text-slate-300 hover:bg-slate-800 transition-colors whitespace-nowrap shrink-0"
                >
                  {copiedId === item.id ? (
                    <Check className="w-3.5 h-3.5 text-emerald-400" />
                  ) : (
                    <Copy className="w-3.5 h-3.5" />
                  )}
                  <span>{copiedId === item.id ? 'Copied' : 'Copy'}</span>
                </button>
              </div>
              <pre className="rounded bg-[#0B0F17] border border-slate-800 px-3 py-2 text-xs font-mono text-cyan-200 whitespace-pre-wrap break-words">
                {item.command}
              </pre>
            </div>
          ))}
        </div>
      </section>

      {/* Honest Hardware Modes & Interactive USB HID 1.11 Unit Verifier */}
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6 items-start">
        <section className="lg:col-span-7 rounded-lg border border-slate-800 bg-[#111827] overflow-hidden">
          <div className="px-5 py-3.5 border-b border-slate-800">
            <h2 className="text-sm font-semibold text-slate-100">
              Hardware Compatibility Modes &amp; Windows Cursor Isolation Architecture
            </h2>
          </div>

          <div className="overflow-x-auto">
            <table className="w-full text-left text-xs">
              <thead className="border-b border-slate-800 text-slate-400">
                <tr>
                  <th className="py-2.5 px-4 font-medium">Mode</th>
                  <th className="py-2.5 px-4 font-medium">Verified Condition</th>
                  <th className="py-2.5 px-4 font-medium">Runtime Behavior</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-800/70 text-slate-300">
                <tr>
                  <td className="py-3 px-4 font-mono text-[#00D2FF] whitespace-nowrap">Mode A</td>
                  <td className="py-3 px-4">
                    Precision Touchpad exposes absolute HID reports (0x0D:0x05) and an optional signed KMDF HID filter driver implements <code className="text-slate-200">ICursorIsolationProvider</code>.
                  </td>
                  <td className="py-3 px-4">
                    Full native absolute coordinates with per-device touchpad cursor suppression.
                  </td>
                </tr>
                <tr>
                  <td className="py-3 px-4 font-mono text-slate-100 whitespace-nowrap">Mode B</td>
                  <td className="py-3 px-4">
                    Standard user mode (no kernel driver). Precision Touchpad exposes absolute X/Y HID reports via <code className="text-slate-200">RegisterRawInputDevices</code>.
                  </td>
                  <td className="py-3 px-4">
                    Native absolute coordinates drive ink; hold Shift or Right-Click (Anchor Lock) to freeze the Lens. External mouse input is never blocked or altered.
                  </td>
                </tr>
                <tr>
                  <td className="py-3 px-4 font-mono text-amber-400 whitespace-nowrap">Mode C</td>
                  <td className="py-3 px-4">
                    No Digitizer/TouchPad (0x0D:0x05) with non-degenerate absolute X/Y logical bounds detected.
                  </td>
                  <td className="py-3 px-4">
                    Disables touchpad drawing and displays the exact HID descriptor diagnostic instead of fabricating synthetic input.
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <div className="px-5 py-3.5 border-t border-slate-800 text-xs text-slate-400 leading-relaxed">
            <strong className="text-slate-200 font-medium">Why WH_MOUSE_LL cannot isolate devices:</strong>{' '}
            Low-level mouse hooks do not provide the originating Raw Input <code className="text-slate-300">hDevice</code> handle, and <code className="text-slate-300">LLMHF_INJECTED</code> only marks software-injected events rather than distinguishing a built-in touchpad from a USB/Bluetooth mouse. TrackPadLens keeps <code className="text-slate-300">MouseHookManager</code> strictly passive and isolates optional kernel filter-driver support behind <code className="text-slate-300">ICursorIsolationProvider</code>.
          </div>
        </section>

        {/* Interactive USB HID 1.11 Unit Converter */}
        <section className="lg:col-span-5 rounded-lg border border-slate-800 bg-[#111827] p-5 flex flex-col gap-4">
          <div>
            <h2 className="text-sm font-semibold text-slate-100">
              USB HID 1.11 Physical Dimension Decoder
            </h2>
            <p className="text-xs text-slate-400 mt-0.5">
              Verifies <code className="text-slate-300">HidUnitConverter.TryCalculatePhysicalSpanMm</code> used to compute physical touchpad aspect ratios without guessing from logical bounds.
            </p>
          </div>

          <div className="grid grid-cols-2 gap-3 text-xs">
            <label className="flex flex-col gap-1">
              <span className="text-slate-400">PhysicalMin</span>
              <input
                type="number"
                value={physMin}
                onChange={(e) => setPhysMin(Number(e.target.value))}
                className="px-2.5 py-1.5 rounded bg-[#0B0F17] border border-slate-800 font-mono text-slate-200"
              />
            </label>
            <label className="flex flex-col gap-1">
              <span className="text-slate-400">PhysicalMax</span>
              <input
                type="number"
                value={physMax}
                onChange={(e) => setPhysMax(Number(e.target.value))}
                className="px-2.5 py-1.5 rounded bg-[#0B0F17] border border-slate-800 font-mono text-slate-200"
              />
            </label>
            <label className="flex flex-col gap-1">
              <span className="text-slate-400">HID Unit (Hex)</span>
              <select
                value={hidUnitHex}
                onChange={(e) => setHidUnitHex(e.target.value)}
                className="px-2.5 py-1.5 rounded bg-[#0B0F17] border border-slate-800 font-mono text-slate-200"
              >
                <option value="0x11">0x11 (SI Linear · cm)</option>
                <option value="0x13">0x13 (English Linear · in)</option>
                <option value="0x00">0x00 (None / Unspecified)</option>
              </select>
            </label>
            <label className="flex flex-col gap-1">
              <span className="text-slate-400">UnitExp Nibble</span>
              <select
                value={hidExpHex}
                onChange={(e) => setHidExpHex(e.target.value)}
                className="px-2.5 py-1.5 rounded bg-[#0B0F17] border border-slate-800 font-mono text-slate-200"
              >
                <option value="0x0F">0x0F (10^-1)</option>
                <option value="0x0E">0x0E (10^-2)</option>
                <option value="0x0D">0x0D (10^-3)</option>
                <option value="0x00">0x00 (10^0)</option>
              </select>
            </label>
          </div>

          <div className="rounded bg-[#0B0F17] border border-slate-800 p-3.5 flex flex-col gap-1.5 text-xs font-mono tabular-nums">
            <div className="flex justify-between">
              <span className="text-slate-400">Decoded Exponent</span>
              <span className="text-slate-200">10^{decodedExp}</span>
            </div>
            <div className="flex justify-between">
              <span className="text-slate-400">Computed Physical Span</span>
              <span className="text-[#00D2FF]">
                {physicalMm !== null ? `${physicalMm.toFixed(2)} mm` : 'Unavailable (fallback to manual calibration)'}
              </span>
            </div>
          </div>
        </section>
      </div>
    </div>
  );
};

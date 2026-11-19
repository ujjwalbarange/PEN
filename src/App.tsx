import { useState } from 'react';
import { Download } from 'lucide-react';
import { LensSandboxView } from './components/LensSandboxView';
import { SolutionExplorerView } from './components/SolutionExplorerView';
import { BuildAndHidGuideView } from './components/BuildAndHidGuideView';

type ActiveTab = 'sandbox' | 'solution' | 'build';

export default function App() {
  const [activeTab, setActiveTab] = useState<ActiveTab>('sandbox');

  return (
    <div className="min-h-screen bg-[#0B0F17] text-slate-100 flex flex-col">
      {/* Strict 3-Zone Top Bar Contract */}
      <header className="border-b border-slate-800/90 bg-[#0B0F17]/95 backdrop-blur sticky top-0 z-30">
        <div className="max-w-[1400px] mx-auto px-6 h-16 flex items-center justify-between gap-8">
          {/* Zone 1: Single text element wordmark */}
          <button
            type="button"
            onClick={() => setActiveTab('sandbox')}
            className="font-display text-lg font-bold tracking-tight text-slate-50 whitespace-nowrap shrink-0 cursor-pointer"
          >
            TrackPadLens
          </button>

          {/* Zone 2: Clean single-line navigation links */}
          <nav aria-label="Primary sections" className="flex items-center gap-7 text-sm font-medium">
            {[
              { id: 'sandbox' as ActiveTab, label: 'Writing Lens Sandbox' },
              { id: 'solution' as ActiveTab, label: 'C# Solution Explorer' },
              { id: 'build' as ActiveTab, label: 'Debug Build & HID Spec' },
            ].map((item) => {
              const active = activeTab === item.id;
              return (
                <button
                  key={item.id}
                  type="button"
                  onClick={() => setActiveTab(item.id)}
                  className={`py-1 transition-colors whitespace-nowrap shrink-0 border-b-2 ${
                    active
                      ? 'text-slate-50 border-[#00D2FF]'
                      : 'text-slate-400 border-transparent hover:text-slate-200'
                  }`}
                >
                  {item.label}
                </button>
              );
            })}
          </nav>

          {/* Zone 3: Single primary action */}
          <div className="flex items-center shrink-0">
            <a
              href="/api/project/download-zip"
              download="TrackPadLens-Windows-Source.zip"
              className="inline-flex items-center gap-2 px-4 py-2 rounded-md bg-[#00D2FF] text-[#0B0F17] text-xs font-semibold hover:bg-[#38DCFF] transition-colors whitespace-nowrap shrink-0"
            >
              <Download className="w-3.5 h-3.5" />
              <span>Download Windows Source</span>
            </a>
          </div>
        </div>
      </header>

      {/* Main Content Area */}
      <main className="flex-1 max-w-[1400px] w-full mx-auto px-6 py-6">
        {activeTab === 'sandbox' && <LensSandboxView />}
        {activeTab === 'solution' && <SolutionExplorerView />}
        {activeTab === 'build' && <BuildAndHidGuideView />}
      </main>
    </div>
  );
}

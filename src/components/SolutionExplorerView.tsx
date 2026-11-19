import React, { useEffect, useMemo, useState } from 'react';
import { Check, Copy, Download, FileCode2, FolderOpen, Search } from 'lucide-react';

interface ProjectFileMeta {
  path: string;
  size: number;
  lines: number;
  category: string;
}

interface ProjectIndexResponse {
  rootName: string;
  hostPlatform: string;
  dotnetAvailable: boolean;
  fileCount: number;
  totalBytes: number;
  totalLines: number;
  files: ProjectFileMeta[];
}

export const SolutionExplorerView: React.FC = () => {
  const [index, setIndex] = useState<ProjectIndexResponse | null>(null);
  const [loadingIndex, setLoadingIndex] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [searchQuery, setSearchQuery] = useState('');
  const [selectedCategory, setSelectedCategory] = useState<string>('All');
  const [selectedPath, setSelectedPath] = useState<string>('TrackPadLens.csproj');
  const [fileContent, setFileContent] = useState<string>('');
  const [loadingFile, setLoadingFile] = useState(false);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    let active = true;
    setLoadingIndex(true);
    fetch('/api/project/files')
      .then((res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json();
      })
      .then((data: ProjectIndexResponse) => {
        if (!active) return;
        setIndex(data);
        setLoadingIndex(false);
      })
      .catch((err) => {
        if (!active) return;
        setError(err instanceof Error ? err.message : 'Unable to load project files.');
        setLoadingIndex(false);
      });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    if (!selectedPath) return;
    let active = true;
    setLoadingFile(true);
    fetch(`/api/project/file?path=${encodeURIComponent(selectedPath)}`)
      .then((res) => {
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json();
      })
      .then((data: { content: string }) => {
        if (!active) return;
        setFileContent(data.content);
        setLoadingFile(false);
      })
      .catch(() => {
        if (!active) return;
        setFileContent('// Failed to load selected source file.');
        setLoadingFile(false);
      });
    return () => {
      active = false;
    };
  }, [selectedPath]);

  const categories = useMemo(() => {
    if (!index) return ['All'];
    const set = new Set(index.files.map((f) => f.category));
    return ['All', ...Array.from(set).sort()];
  }, [index]);

  const filteredFiles = useMemo(() => {
    if (!index) return [];
    const q = searchQuery.trim().toLowerCase();
    return index.files.filter((f) => {
      if (selectedCategory !== 'All' && f.category !== selectedCategory) return false;
      if (q && !f.path.toLowerCase().includes(q)) return false;
      return true;
    });
  }, [index, searchQuery, selectedCategory]);

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(fileContent);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1800);
    } catch {
      // Clipboard may be restricted in headless contexts
    }
  };

  const selectedMeta = index?.files.find((f) => f.path === selectedPath);
  const codeLines = useMemo(() => fileContent.split(/\r?\n/), [fileContent]);

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-lg font-semibold text-slate-100 tracking-tight">
            Windows C# / .NET 8 Solution Explorer
          </h1>
          <p className="text-xs text-slate-400 mt-0.5">
            Live read-only view of the generated <code className="text-slate-300">TrackPadLens/</code> Visual Studio solution, Win32/HID interop layer, WPF views, build scripts, and xUnit tests.
          </p>
        </div>

        {index && (
          <div className="flex items-center gap-3 text-xs font-mono text-slate-400 tabular-nums">
            <span>{index.fileCount} files</span>
            <span aria-hidden="true">·</span>
            <span>{index.totalLines.toLocaleString()} lines</span>
            <span aria-hidden="true">·</span>
            <span>{(index.totalBytes / 1024).toFixed(1)} KB</span>
          </div>
        )}
      </div>

      {error && (
        <div className="rounded-lg border border-red-900/60 bg-red-950/30 p-4 text-sm text-red-200">
          Could not read project tree: {error}
        </div>
      )}

      <div className="grid grid-cols-1 lg:grid-cols-12 gap-5 items-stretch">
        {/* File Tree Sidebar */}
        <div className="lg:col-span-4 rounded-lg border border-slate-800 bg-[#111827] flex flex-col overflow-hidden max-h-[640px]">
          <div className="p-3 border-b border-slate-800 flex flex-col gap-2.5">
            <div className="relative">
              <Search className="w-3.5 h-3.5 text-slate-500 absolute left-2.5 top-1/2 -translate-y-1/2" />
              <input
                type="search"
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                placeholder="Filter C#, XAML, scripts..."
                aria-label="Filter solution files"
                className="w-full pl-8 pr-3 py-1.5 rounded bg-[#0B0F17] border border-slate-800 text-xs text-slate-200 placeholder:text-slate-500 focus:outline-none focus:border-[#00D2FF]"
              />
            </div>

            <div className="flex flex-wrap gap-1">
              {categories.map((cat) => (
                <button
                  key={cat}
                  type="button"
                  onClick={() => setSelectedCategory(cat)}
                  className={`px-2 py-1 rounded text-[11px] font-medium transition-colors whitespace-nowrap shrink-0 ${
                    selectedCategory === cat
                      ? 'bg-[#00D2FF]/15 text-[#00D2FF]'
                      : 'text-slate-400 hover:bg-slate-800/80 hover:text-slate-200'
                  }`}
                >
                  {cat}
                </button>
              ))}
            </div>
          </div>

          <div className="overflow-y-auto flex-1 divide-y divide-slate-800/60">
            {loadingIndex ? (
              <div className="p-4 text-xs text-slate-500">Scanning TrackPadLens/ directory...</div>
            ) : filteredFiles.length === 0 ? (
              <div className="p-4 text-xs text-slate-500">No files match "{searchQuery}".</div>
            ) : (
              filteredFiles.map((file) => {
                const isSelected = file.path === selectedPath;
                return (
                  <button
                    key={file.path}
                    type="button"
                    onClick={() => setSelectedPath(file.path)}
                    className={`w-full text-left px-3 py-2 flex items-center justify-between gap-2 text-xs transition-colors ${
                      isSelected
                        ? 'bg-[#00D2FF]/10 text-[#00D2FF]'
                        : 'text-slate-300 hover:bg-slate-800/60'
                    }`}
                  >
                    <span className="flex items-center gap-2 min-w-0">
                      <FileCode2 className="w-3.5 h-3.5 shrink-0 opacity-75" />
                      <span className="font-mono truncate">{file.path}</span>
                    </span>
                    <span className="font-mono text-[11px] text-slate-500 shrink-0 tabular-nums">
                      {file.lines}L
                    </span>
                  </button>
                );
              })
            )}
          </div>
        </div>

        {/* Source Viewer */}
        <div className="lg:col-span-8 rounded-lg border border-slate-800 bg-[#0D131F] flex flex-col overflow-hidden max-h-[640px]">
          <div className="px-4 py-2.5 border-b border-slate-800 bg-[#111827] flex items-center justify-between gap-3">
            <div className="flex items-center gap-2 min-w-0">
              <FolderOpen className="w-4 h-4 text-[#00D2FF] shrink-0" />
              <span className="font-mono text-xs text-slate-200 truncate">
                TrackPadLens/{selectedPath}
              </span>
              {selectedMeta && (
                <span className="text-[11px] font-mono text-slate-500 shrink-0 tabular-nums">
                  · {selectedMeta.lines} lines · {(selectedMeta.size / 1024).toFixed(1)} KB
                </span>
              )}
            </div>

            <div className="flex items-center gap-2 shrink-0">
              <button
                type="button"
                onClick={handleCopy}
                className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded border border-slate-700 text-xs text-slate-300 hover:bg-slate-800 transition-colors whitespace-nowrap shrink-0"
              >
                {copied ? <Check className="w-3.5 h-3.5 text-emerald-400" /> : <Copy className="w-3.5 h-3.5" />}
                <span>{copied ? 'Copied' : 'Copy'}</span>
              </button>
              <a
                href="/api/project/download-zip"
                download="TrackPadLens-Windows-Source.zip"
                className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded bg-[#00D2FF] text-[#0B0F17] text-xs font-semibold hover:bg-[#38DCFF] transition-colors whitespace-nowrap shrink-0"
              >
                <Download className="w-3.5 h-3.5" />
                <span>Download ZIP</span>
              </a>
            </div>
          </div>

          <div className="overflow-auto flex-1 p-4 font-mono text-[12px] leading-5">
            {loadingFile ? (
              <div className="text-slate-500">Loading source...</div>
            ) : (
              <table className="w-full border-collapse">
                <tbody>
                  {codeLines.map((line, idx) => (
                    <tr key={idx} className="hover:bg-slate-800/35">
                      <td className="pr-4 text-right text-slate-600 select-none w-10 align-top tabular-nums">
                        {idx + 1}
                      </td>
                      <td className="text-slate-200 whitespace-pre">{line}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        </div>
      </div>
    </div>
  );
};

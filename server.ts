import express from 'express';
import fs from 'node:fs';
import path from 'node:path';
import { createServer as createViteServer } from 'vite';
import { buildZipArchive, type ZipEntryInput } from './src/engine/zipBuilder';

const PROJECT_ROOT = path.resolve(process.cwd(), 'TrackPadLens');
const PORT = Number(process.env.PORT) || 3000;

interface ProjectFileMeta {
  path: string;
  size: number;
  lines: number;
  category: string;
}

function walkProjectFiles(dir: string, baseDir: string, out: ProjectFileMeta[] = []): ProjectFileMeta[] {
  if (!fs.existsSync(dir)) return out;
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.name === 'bin' || entry.name === 'obj' || entry.name === '.vs') continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walkProjectFiles(full, baseDir, out);
    } else if (entry.isFile()) {
      const rel = path.relative(baseDir, full).replace(/\\/g, '/');
      const raw = fs.readFileSync(full);
      const text = raw.toString('utf8');
      const lines = text.length === 0 ? 0 : text.split(/\r?\n/).length;
      const topFolder = rel.includes('/') ? rel.split('/')[0] : 'Root';
      out.push({
        path: rel,
        size: raw.length,
        lines,
        category: topFolder,
      });
    }
  }
  return out.sort((a, b) => a.path.localeCompare(b.path));
}

function safeResolveProjectFile(relPath: string): string | null {
  const cleaned = relPath.replace(/\\/g, '/').replace(/^\/+/, '');
  const resolved = path.resolve(PROJECT_ROOT, cleaned);
  if (!resolved.startsWith(PROJECT_ROOT + path.sep)) {
    return null;
  }
  if (!fs.existsSync(resolved) || !fs.statSync(resolved).isFile()) {
    return null;
  }
  return resolved;
}

async function startServer() {
  const app = express();

  app.get('/api/project/files', (_req, res) => {
    try {
      const files = walkProjectFiles(PROJECT_ROOT, PROJECT_ROOT);
      const totalBytes = files.reduce((acc, f) => acc + f.size, 0);
      const totalLines = files.reduce((acc, f) => acc + f.lines, 0);
      res.json({
        rootName: 'TrackPadLens',
        hostPlatform: `${process.platform} (${process.arch})`,
        dotnetAvailable: false,
        fileCount: files.length,
        totalBytes,
        totalLines,
        files,
      });
    } catch (err) {
      res.status(500).json({ error: err instanceof Error ? err.message : 'Failed to list TrackPadLens files.' });
    }
  });

  app.get('/api/project/file', (req, res) => {
    const relPath = typeof req.query.path === 'string' ? req.query.path : '';
    const resolved = safeResolveProjectFile(relPath);
    if (!resolved) {
      res.status(404).json({ error: 'Requested TrackPadLens file was not found.' });
      return;
    }
    try {
      const content = fs.readFileSync(resolved, 'utf8');
      res.json({ path: relPath.replace(/\\/g, '/'), content });
    } catch (err) {
      res.status(500).json({ error: err instanceof Error ? err.message : 'Failed to read file.' });
    }
  });

  app.get('/api/project/download-zip', (_req, res) => {
    try {
      const files = walkProjectFiles(PROJECT_ROOT, PROJECT_ROOT);
      const entries: ZipEntryInput[] = files.map((meta) => {
        const full = path.join(PROJECT_ROOT, meta.path);
        return {
          name: `TrackPadLens/${meta.path}`,
          content: fs.readFileSync(full),
        };
      });
      const workflowPath = path.resolve(process.cwd(), '.github/workflows/build-windows-exe.yml');
      if (fs.existsSync(workflowPath)) {
        entries.push({
          name: '.github/workflows/build-windows-exe.yml',
          content: fs.readFileSync(workflowPath),
        });
      }
      const archive = buildZipArchive(entries);
      res.setHeader('Content-Type', 'application/zip');
      res.setHeader('Content-Disposition', 'attachment; filename="TrackPadLens-Windows-Source.zip"');
      res.setHeader('Content-Length', String(archive.length));
      res.send(archive);
    } catch (err) {
      res.status(500).json({ error: err instanceof Error ? err.message : 'Failed to package ZIP archive.' });
    }
  });

  const isProd = process.env.NODE_ENV === 'production';
  if (!isProd) {
    const vite = await createViteServer({
      server: { middlewareMode: true },
      appType: 'spa',
    });
    app.use(vite.middlewares);
  } else {
    const distDir = path.resolve(process.cwd(), 'dist');
    app.use(express.static(distDir));
    app.get('*', (_req, res) => {
      res.sendFile(path.join(distDir, 'index.html'));
    });
  }

  app.listen(PORT, '0.0.0.0', () => {
    console.log(`TrackPadLens Workbench listening on http://0.0.0.0:${PORT}`);
  });
}

startServer();

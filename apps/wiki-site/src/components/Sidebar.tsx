import { useEffect, useMemo, useState } from 'react';
import { Link, useLocation } from 'react-router';

interface ManifestEntry {
  path: string;   // route, e.g. "/search-pipeline"
  title: string;
  section: string;
}

interface Props {
  mobileOpen: boolean;
  onCloseMobile: () => void;
}

export default function Sidebar({ mobileOpen, onCloseMobile }: Props) {
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);
  const [query, setQuery] = useState('');
  const location = useLocation();

  useEffect(() => {
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then((data: ManifestEntry[]) => setManifest(data))
      .catch(() => {});
  }, []);

  // Group by section, preserving first-seen order
  const sections = useMemo(() => {
    const q = query.trim().toLowerCase();
    const filtered = q
      ? manifest.filter((e) => e.title.toLowerCase().includes(q) || e.section.toLowerCase().includes(q))
      : manifest;
    const map = new Map<string, ManifestEntry[]>();
    for (const e of filtered) {
      if (!map.has(e.section)) map.set(e.section, []);
      map.get(e.section)!.push(e);
    }
    return [...map.entries()];
  }, [manifest, query]);

  return (
    <aside
      id="site-sidebar"
      className={`${
        mobileOpen ? 'translate-x-0' : '-translate-x-full'
      } md:translate-x-0 fixed md:static z-40 top-0 left-0 h-full w-72 shrink-0 flex flex-col
      bg-[#111c36] border-r border-sky-400/10 transition-transform duration-200 ease-out`}
    >
      {/* Brand */}
      <div className="flex items-center justify-between gap-2 px-5 h-14 border-b border-sky-400/10">
        <Link to="/" onClick={onCloseMobile} className="flex items-center gap-2.5 text-white font-semibold tracking-tight">
          <span className="inline-flex items-center justify-center w-7 h-7 rounded-md bg-sky-500/20 text-sky-300">د</span>
          <span>
            Daleel <span className="text-sky-400">Wiki</span>
          </span>
        </Link>
        <button
          type="button"
          onClick={onCloseMobile}
          aria-label="Close navigation"
          className="md:hidden inline-flex items-center justify-center w-8 h-8 rounded-md text-zinc-400 hover:text-white hover:bg-white/5"
        >
          <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      </div>

      {/* Search filter */}
      <div className="px-4 pt-4">
        <input
          type="text"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Filter pages…"
          className="w-full px-3 py-2 text-[13px] rounded-lg bg-[#0b1324] border border-sky-400/10 text-slate-200 placeholder:text-slate-500 focus:outline-none focus:border-sky-400/40"
        />
      </div>

      {/* Nav */}
      <nav className="flex-1 overflow-y-auto px-3 py-4">
        <ul className="space-y-6">
          {sections.map(([section, entries]) => (
            <li key={section}>
              <div className="px-2 mb-1.5 text-[11px] font-semibold uppercase tracking-widest text-slate-500">
                {section}
              </div>
              <ul className="space-y-0.5">
                {entries.map((entry) => {
                  const active = location.pathname === entry.path;
                  return (
                    <li key={entry.path}>
                      <Link
                        to={entry.path}
                        onClick={onCloseMobile}
                        className={`block px-2.5 py-1.5 text-[13.5px] rounded-md transition-colors ${
                          active
                            ? 'bg-sky-500/15 text-sky-200 font-medium'
                            : 'text-slate-400 hover:text-slate-100 hover:bg-white/5'
                        }`}
                      >
                        {entry.title}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </li>
          ))}
          {sections.length === 0 && (
            <li className="px-2 text-[13px] text-slate-500">No pages match "{query}".</li>
          )}
        </ul>
      </nav>

      <div className="px-5 py-3 border-t border-sky-400/10 text-[11px] text-slate-500">
        Daleel — AI shopping search
      </div>
    </aside>
  );
}

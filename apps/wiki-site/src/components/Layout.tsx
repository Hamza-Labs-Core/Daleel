import { useCallback, useEffect, useState } from 'react';
import { Outlet, useLocation } from 'react-router';
import Sidebar from './Sidebar';

export default function Layout() {
  const [mobileNavOpen, setMobileNavOpen] = useState(false);
  const location = useLocation();

  const closeNav = useCallback(() => setMobileNavOpen(false), []);
  const openNav = useCallback(() => setMobileNavOpen(true), []);

  // Close drawer on route change
  useEffect(() => {
    setMobileNavOpen(false);
  }, [location.pathname]);

  // Close on Escape; lock body scroll when open
  useEffect(() => {
    if (!mobileNavOpen) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setMobileNavOpen(false);
    };
    document.addEventListener('keydown', onKey);
    const prevOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prevOverflow;
    };
  }, [mobileNavOpen]);

  return (
    <div className="flex h-screen bg-[#0f1729] text-slate-300">
      <Sidebar mobileOpen={mobileNavOpen} onCloseMobile={closeNav} />

      {mobileNavOpen && (
        <button
          type="button"
          aria-label="Close navigation overlay"
          onClick={closeNav}
          className="md:hidden fixed inset-0 z-30 bg-black/60 backdrop-blur-sm"
        />
      )}

      <div className="flex-1 flex flex-col min-w-0">
        <header className="md:hidden sticky top-0 z-20 flex items-center justify-between gap-3 px-4 h-12 bg-[#0f1729]/85 backdrop-blur-xl border-b border-sky-400/10">
          <button
            type="button"
            aria-label="Open navigation menu"
            aria-expanded={mobileNavOpen}
            aria-controls="site-sidebar"
            onClick={openNav}
            className="inline-flex items-center justify-center w-9 h-9 -ml-2 rounded-md text-zinc-300 hover:text-white hover:bg-white/5 transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-sky-400/60"
          >
            <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2} aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="M4 6h16M4 12h16M4 18h16" />
            </svg>
          </button>
          <div className="flex items-center gap-2 text-white font-semibold text-[14px] tracking-tight">
            <span className="inline-flex items-center justify-center w-6 h-6 rounded-md bg-sky-500/20 text-sky-300 text-[13px]">د</span>
            Daleel Wiki
          </div>
          <div className="w-9" aria-hidden="true" />
        </header>

        <main className="flex-1 overflow-y-auto">
          <div className="max-w-5xl mx-auto px-4 sm:px-6 md:px-8 py-6 md:py-10">
            <Outlet />
          </div>
        </main>
      </div>
    </div>
  );
}

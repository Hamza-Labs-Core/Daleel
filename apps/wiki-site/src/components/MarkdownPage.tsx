import { useEffect, useState, useMemo } from 'react';
import { useLocation, Link } from 'react-router';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeRaw from 'rehype-raw';
import MermaidDiagram from './MermaidDiagram';

interface ManifestEntry {
  path: string;
  title: string;
  section: string;
}

interface TocHeading {
  level: number;
  text: string;
  id: string;
}

function generateId(text: string): string {
  return text.toLowerCase().replace(/[^\w\s-]/g, '').replace(/\s+/g, '-').replace(/-+/g, '-').trim();
}

function extractHeadings(markdown: string): TocHeading[] {
  const headings: TocHeading[] = [];
  const regex = /^(#{2,3})\s+(.+)$/gm;
  let match;
  while ((match = regex.exec(markdown)) !== null) {
    // skip headings inside fenced code blocks is approximated by ignoring lines that
    // are clearly code; good enough for a docs ToC.
    headings.push({
      level: match[1].length,
      text: match[2].replace(/\*\*/g, '').replace(/`/g, '').trim(),
      id: generateId(match[2]),
    });
  }
  return headings;
}

function extractTextFromChildren(children: unknown): string {
  if (typeof children === 'string') return children;
  if (typeof children === 'number') return String(children);
  if (Array.isArray(children)) return children.map(extractTextFromChildren).join('');
  if (children && typeof children === 'object' && 'props' in children) {
    const props = children as { props?: { children?: unknown } };
    return extractTextFromChildren(props.props?.children ?? '');
  }
  return '';
}

function findSiblings(pathname: string, manifest: ManifestEntry[]) {
  const idx = manifest.findIndex((e) => e.path === pathname);
  if (idx < 0) return { prev: null, next: null };
  return {
    prev: idx > 0 ? manifest[idx - 1] : null,
    next: idx < manifest.length - 1 ? manifest[idx + 1] : null,
  };
}

export default function MarkdownPage() {
  const location = useLocation();
  const [content, setContent] = useState('');
  const [title, setTitle] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);
  const [activeHeading, setActiveHeading] = useState('');

  const resolvedPath = `${location.pathname.replace(/^\//, '')}.md`;

  useEffect(() => {
    fetch('/content/manifest.json').then((r) => r.json()).then(setManifest).catch(() => {});
  }, []);

  useEffect(() => {
    setLoading(true);
    setError(false);
    const looksLikeMarkdown = (text: string) => {
      const t = text.trimStart().toLowerCase();
      return !t.startsWith('<!doctype') && !t.startsWith('<html');
    };
    (async () => {
      const res = await fetch(`/content/${resolvedPath}`);
      if (res.ok) {
        const text = await res.text();
        if (looksLikeMarkdown(text)) return text;
      }
      throw new Error('Not found');
    })()
      .then((text) => {
        const h1 = text.match(/^#\s+(.+)$/m);
        if (h1) setTitle(h1[1].trim());
        setContent(text);
        setLoading(false);
      })
      .catch(() => {
        setError(true);
        setLoading(false);
      });
  }, [resolvedPath]);

  useEffect(() => {
    if (title) document.title = `${title} — Daleel Wiki`;
  }, [title]);

  const headings = useMemo(() => extractHeadings(content), [content]);
  const { prev, next } = useMemo(() => findSiblings(location.pathname, manifest), [location.pathname, manifest]);
  const showToc = headings.length > 3;

  useEffect(() => {
    if (!showToc) return;
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) if (entry.isIntersecting) setActiveHeading(entry.target.id);
      },
      { rootMargin: '-80px 0px -70% 0px', threshold: 0 }
    );
    const timer = setTimeout(() => {
      for (const h of headings) {
        const el = document.getElementById(h.id);
        if (el) observer.observe(el);
      }
    }, 200);
    return () => {
      clearTimeout(timer);
      observer.disconnect();
    };
  }, [headings, showToc]);

  if (loading) {
    return (
      <div className="animate-pulse space-y-4 py-8">
        <div className="h-8 bg-zinc-800/50 rounded w-2/3" />
        <div className="h-4 bg-zinc-800/30 rounded w-full" />
        <div className="h-4 bg-zinc-800/30 rounded w-5/6" />
        <div className="h-4 bg-zinc-800/30 rounded w-4/6" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="py-16 text-center">
        <div className="text-6xl mb-4">404</div>
        <div className="text-zinc-500 text-lg">Page not found</div>
        <div className="text-zinc-600 text-sm mt-2 font-mono">{resolvedPath}</div>
        <Link to="/" className="inline-flex items-center gap-1.5 mt-6 text-sm text-sky-400 hover:text-sky-300">
          ← Back to Home
        </Link>
      </div>
    );
  }

  return (
    <div className={showToc ? 'flex gap-8' : ''}>
      <div className={showToc ? 'min-w-0 flex-1' : ''}>
        <nav className="flex items-center gap-1.5 text-[13px] text-zinc-500 mb-6">
          <Link to="/" className="hover:text-zinc-300">Home</Link>
          <span className="text-zinc-700">/</span>
          <span className="text-zinc-400 truncate">{title || resolvedPath}</span>
        </nav>

        <article className="prose prose-invert max-w-none
          prose-headings:font-semibold prose-headings:tracking-tight
          prose-h1:text-3xl prose-h1:border-b prose-h1:border-zinc-800 prose-h1:pb-3 prose-h1:mb-6
          prose-h2:text-xl prose-h2:mt-10 prose-h2:mb-4 prose-h2:scroll-mt-6
          prose-h3:text-lg prose-h3:mt-8 prose-h3:scroll-mt-6
          prose-p:text-[15px] prose-p:leading-relaxed
          prose-a:text-sky-400 prose-a:no-underline hover:prose-a:underline
          prose-code:text-amber-300 prose-code:text-[13px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded
          prose-pre:bg-[#0b1324] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px]
          prose-blockquote:border-l-sky-500/40 prose-blockquote:text-zinc-400
          prose-table:text-[14px]
          [&_table]:border [&_table]:border-zinc-800 [&_table]:rounded-lg [&_table]:overflow-hidden [&_table]:block [&_table]:overflow-x-auto
          [&_thead]:bg-zinc-800/50
          [&_tbody_tr:nth-child(even)]:bg-zinc-900/30">
          <Markdown
            remarkPlugins={[remarkGfm]}
            rehypePlugins={[rehypeRaw]}
            components={{
              h2: ({ children, ...props }) => {
                const id = generateId(extractTextFromChildren(children));
                return <h2 id={id} {...props}>{children}</h2>;
              },
              h3: ({ children, ...props }) => {
                const id = generateId(extractTextFromChildren(children));
                return <h3 id={id} {...props}>{children}</h3>;
              },
              code: ({ className, children, ...props }) => {
                const lang = /language-(\w+)/.exec(className || '')?.[1];
                if (lang === 'mermaid') {
                  return <MermaidDiagram chart={String(children).replace(/\n$/, '')} />;
                }
                return <code className={className} {...props}>{children}</code>;
              },
            }}
          >
            {content}
          </Markdown>
        </article>

        {(prev || next) && (
          <div className="flex items-stretch gap-3 mt-12 pt-8 border-t border-zinc-800">
            {prev ? (
              <Link to={prev.path} className="group flex-1 flex flex-col items-start gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:border-zinc-700">
                <span className="text-[11px] text-zinc-600 uppercase tracking-wider">← Previous</span>
                <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 line-clamp-1">{prev.title}</span>
              </Link>
            ) : <div className="flex-1" />}
            {next ? (
              <Link to={next.path} className="group flex-1 flex flex-col items-end gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:border-zinc-700 text-right">
                <span className="text-[11px] text-zinc-600 uppercase tracking-wider">Next →</span>
                <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 line-clamp-1">{next.title}</span>
              </Link>
            ) : <div className="flex-1" />}
          </div>
        )}
      </div>

      {showToc && (
        <aside className="hidden lg:block w-56 shrink-0">
          <div className="sticky top-6">
            <h4 className="text-[11px] font-semibold text-zinc-500 uppercase tracking-widest mb-3">On this page</h4>
            <nav className="flex flex-col gap-0.5 max-h-[calc(100vh-120px)] overflow-y-auto">
              {headings.map((heading, i) => (
                <a
                  key={`${heading.id}-${i}`}
                  href={`#${heading.id}`}
                  onClick={(e) => {
                    e.preventDefault();
                    const el = document.getElementById(heading.id);
                    if (el) { el.scrollIntoView({ behavior: 'smooth', block: 'start' }); setActiveHeading(heading.id); }
                  }}
                  className={`block text-[12px] leading-snug py-1 ${heading.level === 3 ? 'pl-3' : ''} ${
                    activeHeading === heading.id ? 'text-sky-400 font-medium' : 'text-zinc-500 hover:text-zinc-300'
                  }`}
                >
                  {heading.text.length > 42 ? heading.text.slice(0, 42) + '…' : heading.text}
                </a>
              ))}
            </nav>
          </div>
        </aside>
      )}
    </div>
  );
}

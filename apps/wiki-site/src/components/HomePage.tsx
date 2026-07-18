import { Link } from 'react-router';

const CARDS: Array<{ to: string; title: string; blurb: string; tag: string }> = [
  { to: '/overview', title: 'Overview & Architecture', tag: 'Start here', blurb: 'The projects, the stack, hosting, and how a query becomes a grid of products.' },
  { to: '/search-pipeline', title: 'Search Pipeline', tag: 'Pipeline', blurb: 'The Elsa workflow: discover → scrape → extract → enrich, and the search object.' },
  { to: '/enrichment-queue', title: 'Enrichment Work Queue', tag: 'Pipeline', blurb: 'The per-unit lease/retry/dead-ledger drain that fills price, image, and stock.' },
  { to: '/elsa-workflows', title: 'Elsa Workflows', tag: 'Pipeline', blurb: 'The 11-step SearchWorkflow and the six per-entity sub-workflows that fan out from it.' },
  { to: '/providers-scraping', title: 'Providers & Scraping', tag: 'Data', blurb: 'SerpAPI, Context.dev, and the Cloudflare Browser fallback chain.' },
  { to: '/llm-agents', title: 'LLM & Agents', tag: 'Data', blurb: 'OpenRouter/Kimi, session_id sticky routing, the crawlers, prompt sanitization.' },
  { to: '/images', title: 'Product Images', tag: 'Data', blurb: 'Sourcing from scraped pages, galleries, and the two vision screens that gate display.' },
  { to: '/moderation-halal', title: 'Halal Moderation', tag: 'Trust', blurb: 'The whitelist → keyword → LLM → vision layers, rules, and the auto-reviewer.' },
  { to: '/security', title: 'Security', tag: 'Trust', blurb: 'Prompt-injection sanitization, no user keys, worker tokens, CSP, SSRF.' },
  { to: '/data-storage', title: 'Data & Storage', tag: 'Platform', blurb: 'R2 entity documents + a Postgres index; event stores; migrations.' },
  { to: '/cloudflare-workers', title: 'Cloudflare Workers', tag: 'Platform', blurb: 'The edge scrape/extract/classify workers and the poll-drain.' },
  { to: '/frontend-ui', title: 'Frontend & UI', tag: 'Platform', blurb: 'Blazor Server, the product grid, facets, cards, and the review signal.' },
  { to: '/deployment-qa', title: 'Deployment & QA', tag: 'Ops', blurb: 'QA vs prod topology, the deploy workflows, and the integration-branch flow.' },
  { to: '/observability-admin', title: 'Observability & Admin', tag: 'Ops', blurb: 'The /admin surfaces, event stores, metering, and the dead-unit ledger.' },
];

export default function HomePage() {
  return (
    <div className="pb-12">
      <div className="mb-10">
        <div className="inline-flex items-center gap-2 mb-4 px-3 py-1 rounded-full bg-sky-500/10 border border-sky-400/20 text-sky-300 text-[12px] font-medium">
          Developer documentation
        </div>
        <h1 className="text-4xl font-bold tracking-tight text-white mb-4">Daleel Wiki</h1>
        <p className="text-slate-400 text-[16px] leading-relaxed max-w-2xl">
          Daleel (دليل — "guide") is an AI product & price-comparison search app for MENA markets
          (default: Jordan). A .NET 8 <strong className="text-slate-200">Blazor Server</strong> app runs an
          LLM-driven pipeline (Elsa workflows) that <strong className="text-slate-200">discovers, scrapes,
          extracts, and enriches</strong> product data from local stores and brand sites, then presents a
          live, filterable grid. This wiki documents every subsystem — pipeline, data, trust, platform, and ops.
        </p>
      </div>

      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {CARDS.map((c) => (
          <Link
            key={c.to}
            to={c.to}
            className="group block p-5 rounded-2xl bg-[#111c36] border border-sky-400/10 hover:border-sky-400/30 hover:bg-[#13203d] transition-all"
          >
            <div className="text-[11px] font-semibold uppercase tracking-wider text-sky-400/80 mb-2">{c.tag}</div>
            <div className="text-[15px] font-semibold text-slate-100 group-hover:text-white mb-1.5">{c.title}</div>
            <div className="text-[13px] text-slate-400 leading-relaxed">{c.blurb}</div>
          </Link>
        ))}
      </div>

      <div className="mt-10 p-5 rounded-2xl bg-[#0b1324] border border-sky-400/10 text-[13.5px] text-slate-400">
        <span className="text-slate-200 font-medium">New here?</span> Read{' '}
        <Link to="/overview" className="text-sky-400 hover:underline">Overview &amp; Architecture</Link>, then follow the pipeline:{' '}
        <Link to="/search-pipeline" className="text-sky-400 hover:underline">Search Pipeline</Link> →{' '}
        <Link to="/enrichment-queue" className="text-sky-400 hover:underline">Enrichment Queue</Link> →{' '}
        <Link to="/images" className="text-sky-400 hover:underline">Images</Link>.
      </div>
    </div>
  );
}

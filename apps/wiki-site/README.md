# Daleel Wiki Site

Developer documentation for **Daleel**, served as a Vite + React SPA on Cloudflare Workers.
Content is plain markdown under `public/content/`, listed in `public/content/manifest.json`
(the sidebar + prev/next navigation) and rendered by `src/components/MarkdownPage.tsx`
(react-markdown + remark-gfm; ```mermaid fenced blocks render as diagrams).

## Develop
```bash
pnpm install
pnpm dev          # http://localhost:5173
```

## Build & deploy
```bash
pnpm build        # -> dist/
pnpm deploy       # wrangler deploy --config wrangler.jsonc
```

## Add a page
1. Write `public/content/<slug>.md` (first `# H1` becomes the title).
2. Add an entry to `public/content/manifest.json` (`path: "/<slug>"`, `title`, `section`).
That's it — the route `/<slug>` renders it and the sidebar picks it up.

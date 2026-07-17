export default {
  async fetch(request: Request, env: { ASSETS: { fetch: typeof fetch } }): Promise<Response> {
    const url = new URL(request.url);
    const assetResponse = await env.ASSETS.fetch(request);
    if (assetResponse.status !== 404) return assetResponse;
    // SPA fallback — serve index.html for client-side routes
    const indexRequest = new Request(new URL('/', url.origin), request);
    return env.ASSETS.fetch(indexRequest);
  },
};

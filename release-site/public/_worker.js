const zipPath = /^\/downloads\/(CodexQuotaTray-v\d+\.\d+\.\d+-win-x64\.zip)$/;

export default {
  async fetch(request, env) {
    if (!["GET", "HEAD"].includes(request.method)) return new Response(null, { status: 405 });
    const path = new URL(request.url).pathname;
    if (path === "/" || path === "/index.html") return env.ASSETS.fetch(request);
    const match = zipPath.exec(path);
    if (path !== "/latest.json" && !match) return new Response(null, { status: 404 });
    const key = match ? "downloads/" + match[1] : "latest.json";
    const object = request.method === "HEAD" ? await env.RELEASES.head(key) : await env.RELEASES.get(key);
    if (!object) return new Response(null, { status: 404 });
    const headers = new Headers({
      "Content-Type": match ? "application/zip" : "application/json; charset=utf-8",
      "Content-Length": String(object.size),
      "ETag": object.httpEtag,
      "Cache-Control": match ? "public, max-age=31536000, immutable" : "no-store",
      "X-Content-Type-Options": "nosniff"
    });
    if (match) headers.set("Content-Disposition", 'attachment; filename="' + match[1] + '"');
    return new Response(object.body ?? null, { headers });
  }
};

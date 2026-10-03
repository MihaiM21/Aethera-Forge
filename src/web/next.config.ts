import type { NextConfig } from "next";

const isDev = process.env.NODE_ENV === "development";
const apiOrigin = process.env.AETHERA_API_ORIGIN ?? "http://localhost:5080";

/**
 * Routes that have a `[id]` detail page, exported once as `<root>/_.html`.
 * Keep in sync with docs/architecture/0005-web-routing.md.
 */
const DETAIL_ROOTS = ["projects"];

const nextConfig: NextConfig = {
  // Static export: the .NET API serves the generated `out/` directory.
  // Production builds only: `next dev` with `output: export` rejects the
  // detail-route rewrite below (it validates the original URL's params against
  // generateStaticParams) and warns that rewrites do not work with export.
  output: isDev ? undefined : "export",
  images: { unoptimized: true },
  // `/login` -> `login.html` (not `login/index.html`); see docs/architecture/0005-web-routing.md.
  trailingSlash: false,
  // Dev only (`next build` runs with NODE_ENV=production, so the export stays
  // clean): proxy API + SignalR to the .NET API so the browser stays
  // same-origin, and mimic the API's `/projects/{id}` -> `projects/_.html` rule.
  ...(isDev && {
    async rewrites() {
      return [
        { source: "/api/:path*", destination: `${apiOrigin}/api/:path*` },
        { source: "/hubs/:path*", destination: `${apiOrigin}/hubs/:path*` },
        ...DETAIL_ROOTS.map((root) => ({
          source: `/${root}/:id`,
          destination: `/${root}/_`,
        })),
      ];
    },
  }),
};

export default nextConfig;

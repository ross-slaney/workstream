import { publicOrigin } from "@/lib/site";

/** /sitemap.xml: the public pages, for search engines. The docs' reader is server only, so it is loaded here. */
export async function GET(request: Request) {
  const { sitemapXml } = await import("@/lib/sitemap");
  return new Response(sitemapXml(publicOrigin(request)), {
    headers: { "Content-Type": "application/xml; charset=utf-8", "Cache-Control": "public, max-age=3600" },
  });
}

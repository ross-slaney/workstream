import { publicOrigin } from "@/lib/site";

/** /robots.txt: where the sitemap is, and what crawlers should leave alone. */
export async function GET(request: Request) {
  const { robotsTxt } = await import("@/lib/sitemap");
  return new Response(robotsTxt(publicOrigin(request)), {
    headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "public, max-age=3600" },
  });
}

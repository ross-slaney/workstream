/*
 * The website's own address, as the visitor's browser or a crawler knows it. The web server sits behind a
 * proxy in production, so the forwarded host and scheme win over the request's own.
 */
export function publicOrigin(request: Request): string {
  const url = new URL(request.url);
  const first = (value: string | null) => value?.split(",")[0]?.trim() || undefined;
  const host = first(request.headers.get("x-forwarded-host")) ?? url.host;
  const proto = first(request.headers.get("x-forwarded-proto")) ?? url.protocol.replace(":", "");
  return `${proto}://${host}`;
}

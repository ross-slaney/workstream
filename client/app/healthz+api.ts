/** The web container's liveness probe (infra/web.bicep) and the deploy's first check. */
export function GET() {
  return Response.json({ status: "ok", revision: process.env.SOURCE_COMMIT_SHA ?? "local" });
}

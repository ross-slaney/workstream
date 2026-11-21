import { translator } from "@/lib/i18n";

const t = translator("common");

/** The server answered, but not with success. `message` is the server's own words where it gave any. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly code?: string,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** There is no usable session. Nothing but signing in again fixes this. */
export class AuthError extends Error {
  constructor(message = t("errors.signIn")) {
    super(message);
    this.name = "AuthError";
  }
}

/** The request never reached the server. */
export function isOfflineError(error: unknown) {
  if (error instanceof AuthError || error instanceof ApiError) return false;
  if (error instanceof TypeError) return true;
  const message = error instanceof Error ? error.message.toLowerCase() : "";
  return message.includes("network") || message.includes("failed to fetch");
}

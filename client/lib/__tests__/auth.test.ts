import { describe, expect, it } from "vitest";
import { providerReturn, type PendingSignIn } from "../auth";

/*
 * What /auth/callback exchanges when the browser comes back from GitHub: only a code that answers the sign-in
 * this tab began.
 */

const began: PendingSignIn = { state: "state-1", codeVerifier: "verifier-1", returnTo: "/home" };

describe("a provider's return", () => {
  it("is exchanged with the verifier the sign-in kept, and goes on to where it was headed", () => {
    expect(providerReturn({ code: "code-1", state: "state-1" }, { ...began, returnTo: "/t/team-1" })).toEqual({ code: "code-1", codeVerifier: "verifier-1", returnTo: "/t/team-1" });
  });

  it("is refused when its state is not the one the sign-in began with", () => {
    expect(providerReturn({ code: "code-1", state: "someone-elses" }, began)).toBeNull();
    expect(providerReturn({ code: "code-1" }, began)).toBeNull();
  });

  it("is refused when it brings no code", () => {
    expect(providerReturn({ state: "state-1" }, began)).toBeNull();
    expect(providerReturn({ code: "", state: "state-1" }, began)).toBeNull();
  });

  it("is refused when this tab remembers no sign-in", () => {
    expect(providerReturn({ code: "code-1", state: "state-1" }, null)).toBeNull();
  });

  it("is refused for the command line's page, which keeps where it was and no verifier", () => {
    expect(providerReturn({ code: "code-1", state: "state-1" }, { state: null, codeVerifier: null, returnTo: "/device?request=req_1" })).toBeNull();
    expect(providerReturn({ code: "code-1" }, { state: null, codeVerifier: null, returnTo: "/device?request=req_1" })).toBeNull();
  });
});

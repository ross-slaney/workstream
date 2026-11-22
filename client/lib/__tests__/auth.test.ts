import { describe, expect, it } from "vitest";
import { providerReturn, teamChoiceFrom, type PendingSignIn } from "../auth";

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

describe("back from GitHub on several teams", () => {
  const asked = { request: "req-1", view: "organization", pendingToken: "pending/1" };

  it("reads the request again with SqlOS's question and its token, for the sign-in this tab began", () => {
    expect(teamChoiceFrom(asked, began)).toBe("?request=req-1&view=organization&pendingToken=pending%2F1");
  });

  it("is a sign-in that did not work otherwise", () => {
    expect(teamChoiceFrom({ ...asked, view: "login" }, began)).toBeNull();
    expect(teamChoiceFrom({ ...asked, pendingToken: undefined }, began)).toBeNull();
    expect(teamChoiceFrom(asked, null)).toBeNull();
    // The command line's page keeps no verifier: its own page finishes there.
    expect(teamChoiceFrom(asked, { state: null, codeVerifier: null, returnTo: "/device?request=d" })).toBeNull();
  });
});

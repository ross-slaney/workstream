import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { SignInForm } from "../parts";
import type { SignInScreenProps } from "../props";

/*
 * "Continue with GitHub" is on the form only where SqlOS offers GitHub, which it does only where the API has
 * the GitHub OAuth client's id and secret (api/Workstream.cs SeedGitHubSignIn).
 */

const nothing = () => {};
const form: SignInScreenProps = {
  mode: "login", method: "code", busy: false, invitation: null, email: "", name: "", password: "", code: "", error: null, providers: [],
  onProvider: nothing, onEmailChange: nothing, onNameChange: nothing, onPasswordChange: nothing, onCodeChange: nothing,
  onSubmit: nothing, onResend: nothing, onRestart: nothing, onSwitchMode: nothing, homeHref: "/",
};
const github = { id: "oidc_1", name: "GitHub", kind: "github" };
const draw = (props: Partial<SignInScreenProps>) => renderToStaticMarkup(<SignInForm {...form} {...props} />);

describe("the sign-in form", () => {
  it("shows no provider button when SqlOS offers none", () => {
    const page = draw({});
    expect(page).toContain("Email me a code");
    expect(page).not.toContain("Continue with");
    expect(page).not.toContain(">or<");
  });

  it("shows Continue with GitHub under the emailed code when SqlOS offers GitHub", () => {
    const page = draw({ providers: [github] });
    expect(page).toContain("Continue with GitHub");
    expect(page).toContain(">or<");
    expect(page.indexOf("Email me a code")).toBeLessThan(page.indexOf("Continue with GitHub"));
  });

  it("shows it when creating an account and beside a password too", () => {
    expect(draw({ mode: "signup", providers: [github] })).toContain("Continue with GitHub");
    expect(draw({ method: "password", providers: [github] })).toContain("Continue with GitHub");
  });

  it("holds the button still while the form is waiting on the server", () => {
    const held = /<button[^>]*disabled=""[^>]*>(?:(?!<\/button>).)*Continue with GitHub/;
    expect(draw({ providers: [github], busy: true })).toMatch(held);
    expect(draw({ providers: [github] })).not.toMatch(held);
  });

  it("says in the product's words when GitHub did not sign the person in", () => {
    const page = draw({ providers: [github], error: "Signing in with GitHub did not work. Try again, or sign in with your email." });
    expect(page).toContain('role="alert"');
    expect(page).toContain("Signing in with GitHub did not work.");
  });
});

import type { SignInScreenProps } from "@/src/auth/sign-in-screen";

/**
 * Which step of approving the command line is on screen: `opening` while SqlOS reads the request, `signIn`
 * when the browser has to sign in first, `approve` to allow it or not, then `approved` or `denied`.
 * `unusable` is a link SqlOS no longer knows: expired or used.
 */
export type DeviceScreenMode = "opening" | "signIn" | "approve" | "approved" | "denied" | "unusable";

/** Approving the Workstream command line's sign-in on someone's computer, as them. */
export type DeviceScreenProps = {
  mode: DeviceScreenMode;
  /** The sign-in form, for `signIn`: the same steps as the sign-in page. */
  signIn: SignInScreenProps;
  /** Who the command line will act as, once signed in. */
  email: string | null;
  /** The code the terminal printed, to compare. */
  code: string | null;
  /** Waiting on the server: the buttons show it. */
  busy: boolean;
  /** Why allowing or not did not go through, in words to show. */
  error: string | null;
  onApprove: () => void;
  onDeny: () => void;
  homeHref: string;
};

function required(name: string, value: string | undefined) {
  const text = value?.trim();
  if (!text) throw new Error(`${name} is required`);
  return text;
}

// Expo only inlines EXPO_PUBLIC values that are spelled out, so each is read by its full name.
export function getApiUrl() {
  return required("EXPO_PUBLIC_API_URL", process.env.EXPO_PUBLIC_API_URL).replace(/\/$/, "");
}

export function getAuthServerUrl() {
  return `${getApiUrl()}/sqlos/auth`;
}

export function getSqlOsClientId() {
  return required("EXPO_PUBLIC_SQLOS_CLIENT_ID", process.env.EXPO_PUBLIC_SQLOS_CLIENT_ID);
}

/** Where a provider's redirect lands, and the redirect URI the app's client is registered with (api/Workstream.cs). */
export function getRedirectUri() {
  return `${window.location.origin}/auth/callback`;
}

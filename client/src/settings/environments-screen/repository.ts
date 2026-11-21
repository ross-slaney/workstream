/** Whether two addresses name the same repository: GitHub's own, and one typed with `.git` or a slash at its end. */
export function sameRepository(a: string, b: string) {
  const plain = (url: string) => url.trim().replace(/\/+$/, "").replace(/\.git$/, "").toLowerCase();
  return plain(a) !== "" && plain(a) === plain(b);
}

import { Link } from "expo-router";
import { useT } from "@/lib/i18n";

export default function NotFoundRoute() {
  const t = useT("common");
  return (
    <main className="flex h-full flex-col items-center justify-center gap-3 bg-background px-6 text-center text-foreground">
      <h1 className="text-xl font-semibold">{t("notFound.title")}</h1>
      <p className="text-sm text-muted-foreground">{t("errors.notFound")}</p>
      <Link href="/" className="text-sm font-medium text-primary underline-offset-4 hover:underline">
        {t("notFound.home")}
      </Link>
    </main>
  );
}

import { useTranslation } from "react-i18next";
import type { RuntimeFontProgress as Progress } from "./RuntimeFontLoader";

export function RuntimeFontProgress({ progress }: { progress: Progress | null }) {
  const { t, i18n } = useTranslation();
  const downloading = progress?.phase === "downloading";
  const percent = downloading ? new Intl.NumberFormat(i18n.language, { style: "percent", maximumFractionDigits: 0 }).format(progress.receivedBytes / progress.totalBytes) : null;
  const label = progress?.phase === "verifying" ? t("runtimeUi.fontVerifying")
    : progress?.phase === "decoding" ? t("runtimeUi.fontDecoding") : t("consoleExtra.fontLoading");
  return <div className="runtime-font-progress" role="status">
    <span>{label}{percent && ` ${percent}`}</span>
    <progress aria-label={label} max={progress?.totalBytes ?? 1} value={downloading ? progress.receivedBytes : undefined}/>
  </div>;
}

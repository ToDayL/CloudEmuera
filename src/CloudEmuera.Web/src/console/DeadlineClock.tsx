import { useEffect, useRef, useState } from "react";
import i18n from "../i18n";

export function DeadlineClock({ deadlineUnixMilliseconds, serverTimeOffsetMilliseconds, onExpired }: { deadlineUnixMilliseconds: number; serverTimeOffsetMilliseconds: number; onExpired?: () => void }) {
  const [remaining, setRemaining] = useState(() => deadlineUnixMilliseconds > 0 ? deadlineUnixMilliseconds - (Date.now() + serverTimeOffsetMilliseconds) : Number.POSITIVE_INFINITY);
  const expirationReported = useRef(false);
  useEffect(() => {
    expirationReported.current = false;
    if (deadlineUnixMilliseconds <= 0) {
      setRemaining(Number.POSITIVE_INFINITY);
      return;
    }
    const monotonicStart = performance.now();
    const serverStart = Date.now() + serverTimeOffsetMilliseconds;
    const update = () => {
      const next = deadlineUnixMilliseconds - (serverStart + (performance.now() - monotonicStart));
      setRemaining(next);
      if (next <= 0 && !expirationReported.current) {
        expirationReported.current = true;
        onExpired?.();
      }
    };
    update();
    const timer = window.setInterval(update, 250);
    return () => window.clearInterval(timer);
  }, [deadlineUnixMilliseconds, onExpired, serverTimeOffsetMilliseconds]);
  if (deadlineUnixMilliseconds <= 0) return null;
  if (remaining <= 0) return <span className="deadline-clock expired" role="status">{i18n.t("renderer.expired")}</span>;
  const seconds = Math.ceil(remaining / 1_000);
  return <span className="deadline-clock" role="timer" aria-label={i18n.t("renderer.remaining", { seconds })}>{Math.floor(seconds / 60).toString().padStart(2, "0")}:{(seconds % 60).toString().padStart(2, "0")}</span>;
}

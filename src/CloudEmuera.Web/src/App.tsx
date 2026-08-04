import { useEffect, useState } from "react";

interface BuildInfo {
  product: string;
  version: string;
  runtime: string;
}

export function App() {
  const [build, setBuild] = useState<BuildInfo | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    void fetch("/api/v1/version", { signal: controller.signal })
      .then((response) => {
        if (!response.ok) {
          throw new Error(`API returned ${response.status}`);
        }

        return response.json() as Promise<BuildInfo>;
      })
      .then(setBuild)
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === "AbortError") {
          return;
        }

        console.error("Unable to load build information", error);
      });

    return () => controller.abort();
  }, []);

  return (
    <main className="shell">
      <section className="hero" aria-labelledby="page-title">
        <p className="eyebrow">Remote Emuera runtime</p>
        <h1 id="page-title">CloudEmuera</h1>
        <p className="summary">
          开发环境已就绪。游戏库、Session 控制台与存档管理将在后续阶段实现。
        </p>
        <dl className="status">
          <div>
            <dt>API</dt>
            <dd>{build ? "已连接" : "正在连接…"}</dd>
          </div>
          <div>
            <dt>版本</dt>
            <dd>{build?.version ?? "development"}</dd>
          </div>
          <div>
            <dt>Runtime</dt>
            <dd>{build?.runtime ?? ".NET 10"}</dd>
          </div>
        </dl>
      </section>
    </main>
  );
}


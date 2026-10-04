import { sha256 } from "hash-wasm";
import { sha256Fallback } from "./RuntimeFontSha256";

export async function hashFontInWorker(bytes: ArrayBuffer): Promise<{ digest: string; implementation: "wasm" | "javascript" }> {
  const data = new Uint8Array(bytes);
  try {
    return { digest: await sha256(data), implementation: "wasm" };
  } catch {
    // Older browsers or an additional proxy CSP may disallow WASM. Retain
    // full verification in the Worker instead of accepting unchecked bytes.
    return { digest: sha256Fallback(data), implementation: "javascript" };
  }
}

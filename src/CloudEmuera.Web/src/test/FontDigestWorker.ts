import { sha256Fallback } from "../console/RuntimeFontSha256";

/** jsdom has no real Workers; preserve transfer semantics for loader tests. */
export class TestFontDigestWorker {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: (() => void) | null = null;
  onmessageerror: (() => void) | null = null;
  postMessage(bytes: ArrayBuffer, transfer: ArrayBuffer[]) {
    const transferred = structuredClone(bytes, { transfer });
    const digest = sha256Fallback(new Uint8Array(transferred));
    const result = structuredClone({ bytes: transferred, digest }, { transfer: [transferred] });
    // Node structuredClone returns Node-realm buffers; a real message arrives
    // with the receiving window's ArrayBuffer prototype.
    const received = new ArrayBuffer(result.bytes.byteLength);
    new Uint8Array(received).set(new Uint8Array(result.bytes));
    queueMicrotask(() => this.onmessage?.({ data: { bytes: received, digest } } as MessageEvent));
  }
  terminate() {}
}

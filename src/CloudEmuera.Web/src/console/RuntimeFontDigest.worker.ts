import { hashFontInWorker } from "./RuntimeFontWorkerHash";

// Transfer the same buffer back for FontFace decoding; avoid copying complete
// CJK faces. No periodic timers: the computation never runs on the UI thread.
self.onmessage = async (event: MessageEvent<ArrayBuffer>) => {
  const bytes = event.data;
  const result = await hashFontInWorker(bytes);
  self.postMessage({ bytes, ...result }, { transfer: [bytes] });
};

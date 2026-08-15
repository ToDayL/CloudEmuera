import { describe, expect, it, vi } from "vitest";
import { AssetResolver } from "./AssetResolver";
import { MediaController } from "./media";
import type { MediaChannel } from "../realtime/protocol";

class FakeAudio {
  static instances: FakeAudio[] = [];
  src = "";
  preload = "";
  loop = false;
  volume = 1;
  paused = true;
  currentTime = 0;
  readonly play = vi.fn(async () => { this.paused = false; });
  readonly pause = vi.fn(() => { this.paused = true; });
  readonly load = vi.fn();
  onerror: (() => void) | null = null;

  constructor() { FakeAudio.instances.push(this); }
  removeAttribute(name: string): void { if (name === "src") this.src = ""; }
}

const assetId = "sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const assets = new AssetResolver("s1", {
  schemaVersion: 1,
  assets: [{ assetId, mediaType: "audio/ogg", byteLength: 4, contentDigest: "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }],
  fonts: [],
  fontDiagnostics: [],
});

function channel(revision: number, playbackState: MediaChannel["playbackState"], startPolicy: MediaChannel["startPolicy"] = "immediate"): MediaChannel {
  return { channel: "music", assetId, playbackState, loop: true, volume: 0.5, revision, startPolicy };
}

describe("MediaController", () => {
  it("ignores stale revisions and stops the current channel deterministically", () => {
    vi.stubGlobal("Audio", FakeAudio);
    FakeAudio.instances = [];
    const controller = new MediaController();
    controller.apply(channel(2, "playing"), assets);
    const audio = FakeAudio.instances[0];
    expect(audio.play).toHaveBeenCalledTimes(1);
    controller.apply(channel(1, "stopped"), assets);
    expect(audio.pause).not.toHaveBeenCalled();
    controller.apply(channel(3, "stopped"), assets);
    expect(audio.pause).toHaveBeenCalledTimes(1);
    expect(audio.currentTime).toBe(0);
    controller.dispose();
    vi.unstubAllGlobals();
  });

  it("defers gesture-gated playback and disposes every element on stream end", async () => {
    vi.stubGlobal("Audio", FakeAudio);
    FakeAudio.instances = [];
    const controller = new MediaController();
    controller.apply(channel(1, "requested", "onUserGesture"), assets);
    const audio = FakeAudio.instances[0];
    expect(audio.play).not.toHaveBeenCalled();
    await controller.enable();
    expect(audio.play).toHaveBeenCalledTimes(1);
    controller.dispose();
    expect(audio.pause).toHaveBeenCalledTimes(1);
    expect(audio.load).toHaveBeenCalledTimes(1);
    expect(audio.src).toBe("");
    vi.unstubAllGlobals();
  });

  it("reports decode failures and unauthorized assets", () => {
    vi.stubGlobal("Audio", FakeAudio);
    FakeAudio.instances = [];
    const onError = vi.fn();
    const controller = new MediaController(onError);
    controller.apply(channel(1, "playing"), assets);
    FakeAudio.instances[0].onerror?.();
    expect(onError).toHaveBeenCalledWith("音频资源加载或解码失败，已停止该媒体频道。");

    controller.apply({ ...channel(2, "playing"), assetId: "sha256-missing" }, assets);
    expect(onError).toHaveBeenCalledWith("音频资源未通过 Session manifest 授权，已停止该媒体频道。");
    controller.dispose();
    vi.unstubAllGlobals();
  });

  it("stops and removes channels omitted by an authoritative media snapshot", () => {
    vi.stubGlobal("Audio", FakeAudio);
    FakeAudio.instances = [];
    const controller = new MediaController();
    controller.sync([channel(1, "playing")], assets);
    const audio = FakeAudio.instances[0];
    controller.sync([], assets);
    expect(audio.pause).toHaveBeenCalledTimes(1);
    expect(audio.load).toHaveBeenCalledTimes(1);
    expect(audio.src).toBe("");
    controller.dispose();
    vi.unstubAllGlobals();
  });

  it("does not reuse an old source when a channel loses its asset", () => {
    vi.stubGlobal("Audio", FakeAudio);
    FakeAudio.instances = [];
    const onError = vi.fn();
    const controller = new MediaController(onError);
    controller.apply(channel(1, "playing"), assets);
    const audio = FakeAudio.instances[0];
    controller.apply({ ...channel(2, "playing"), assetId: null }, assets);
    expect(audio.pause).toHaveBeenCalledTimes(1);
    expect(audio.src).toBe("");
    expect(onError).toHaveBeenCalledWith("音频频道缺少已授权资源，已停止该媒体频道。");
    controller.dispose();
    vi.unstubAllGlobals();
  });
});

import type { AssetResolver } from "./AssetResolver";
import type { MediaChannel } from "../realtime/protocol";

/** Browser media is best-effort; revision ordering prevents stale stop/play races. */
export class MediaController {
  private readonly elements = new Map<string, HTMLAudioElement>();
  private readonly revisions = new Map<string, number>();
  private userGestureEnabled = false;

  constructor(private readonly onError?: (message: string) => void) {}

  /** Apply the complete authoritative channel set and stop channels omitted by a snapshot. */
  sync(channels: readonly MediaChannel[], assets: AssetResolver): void {
    const activeChannels = new Set(channels.map(channel => channel.channel));
    for (const [channel, element] of this.elements) {
      if (activeChannels.has(channel)) continue;
      element.pause();
      element.currentTime = 0;
      element.removeAttribute("src");
      element.load();
      this.elements.delete(channel);
      this.revisions.delete(channel);
    }
    for (const channel of channels) this.apply(channel, assets);
  }

  apply(channel: MediaChannel, assets: AssetResolver): void {
    if ((this.revisions.get(channel.channel) ?? -1) > channel.revision) return;
    this.revisions.set(channel.channel, channel.revision);
    let element = this.elements.get(channel.channel);
    if (!element) {
      element = new Audio();
      element.preload = "none";
      element.onerror = () => this.onError?.("音频资源加载或解码失败，已停止该媒体频道。");
      this.elements.set(channel.channel, element);
    }
    element.loop = channel.loop;
    element.volume = channel.volume;
    if (!channel.assetId) {
      element.pause();
      element.currentTime = 0;
      element.removeAttribute("src");
      if (channel.playbackState !== "stopped") this.onError?.("音频频道缺少已授权资源，已停止该媒体频道。");
      return;
    }
    const url = assets.url(channel.assetId);
    if (!url) {
      element.pause();
      element.removeAttribute("src");
      this.onError?.("音频资源路径引用无效，已停止该媒体频道。");
      return;
    }
    if (element.src !== new URL(url, window.location.origin).href) element.src = url;
    if (channel.playbackState === "stopped") {
      element.pause();
      element.currentTime = 0;
    } else if ((channel.playbackState === "playing" || channel.playbackState === "requested") && (channel.startPolicy === "immediate" || this.userGestureEnabled)) {
      void element.play().catch(() => { /* autoplay requires a later user gesture */ });
    }
  }

  async enable(): Promise<void> {
    this.userGestureEnabled = true;
    await Promise.all([...this.elements.values()].map(element => element.paused ? element.play().catch(() => undefined) : Promise.resolve()));
  }

  reset(): void { this.dispose(); this.userGestureEnabled = false; }

  dispose(): void {
    for (const element of this.elements.values()) { element.pause(); element.removeAttribute("src"); element.load(); }
    this.elements.clear();
    this.revisions.clear();
  }
}

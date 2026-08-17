import { useCallback, useEffect, useState } from "react";
import type { Prompt } from "../realtime/protocol";
import { DeadlineClock } from "./DeadlineClock";
import type { ConsoleInputEvent } from "./ScrollbackRenderer";

export function PromptController({ prompt, disabled, pending, serverTimeOffsetMilliseconds, onInput }: { prompt: Prompt; disabled?: boolean; pending?: boolean; serverTimeOffsetMilliseconds: number; onInput: (event: ConsoleInputEvent) => void }) {
  const [value, setValue] = useState(prompt.defaultValue ?? "");
  const [deadlineExpired, setDeadlineExpired] = useState(() => prompt.deadlineUnixMilliseconds > 0 && prompt.deadlineUnixMilliseconds <= Date.now() + serverTimeOffsetMilliseconds);
  useEffect(() => setValue(prompt.defaultValue ?? ""), [prompt.promptId, prompt.defaultValue]);
  useEffect(() => setDeadlineExpired(prompt.deadlineUnixMilliseconds > 0 && prompt.deadlineUnixMilliseconds <= Date.now() + serverTimeOffsetMilliseconds), [prompt.promptId, prompt.deadlineUnixMilliseconds, serverTimeOffsetMilliseconds]);
  const markDeadlineExpired = useCallback(() => setDeadlineExpired(true), []);
  const controlsDisabled = Boolean(disabled || pending || deadlineExpired);
  const sourceAllowed = (source: "keyboard" | "button" | "pointer") => prompt.allowedSources.includes(source);
  const submit = (source: ConsoleInputEvent["source"], nextValue = value, metadata: Pick<ConsoleInputEvent, "pointer" | "key"> = {}) => {
    const sourceName = source === "KEYBOARD" ? "keyboard" : source === "BUTTON" ? "button" : "pointer";
    if (controlsDisabled || !sourceAllowed(sourceName) || prompt.inputType === "waitOnly") return;
    onInput({ value: nextValue, source, ...metadata });
  };
  const constrainValue = (nextValue: string): string => {
    const limited = prompt.constraints.maxLength ? nextValue.slice(0, prompt.constraints.maxLength) : nextValue;
    return prompt.oneInput ? Array.from(limited)[0] ?? "" : limited;
  };
  const integerInput = prompt.inputType === "integer" || prompt.inputType === "integerButton";
  const textInput = ["integer", "text", "anyValue", "integerButton", "textButton"].includes(prompt.inputType);
  const enterOnly = prompt.inputType === "enterKey";
  const onKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    const key = { keyCode: event.keyCode || event.which, control: event.ctrlKey, alt: event.altKey, shift: event.shiftKey };
    if (prompt.inputType === "anyKey" || prompt.inputType === "primitivePointerKey") {
      event.preventDefault();
      submit("KEYBOARD", constrainValue(event.key), { key });
    } else if (prompt.inputType === "enterKey" && event.key === "Enter") {
      event.preventDefault();
      submit("KEYBOARD", constrainValue(value), { key });
    }
  };
  const onAnyKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    if (event.nativeEvent.isComposing) return;
    event.preventDefault();
    const key = { keyCode: event.keyCode || event.which, control: event.ctrlKey, alt: event.altKey, shift: event.shiftKey };
    submit("KEYBOARD", constrainValue(event.key), { key });
  };
  const requiresText = !["waitOnly", "anyKey", "primitivePointerKey"].includes(prompt.inputType);
  const promptLabel = prompt.systemInput ? "游戏运行时输入" : "游戏输入提示";
  const enterButtonDisabled = controlsDisabled || (!sourceAllowed("keyboard") && !sourceAllowed("button"));
  const enterButtonLabel = pending ? "发送中…" : deadlineExpired ? "等待游戏确认…" : "↵";
  const submitWithEnter = () => {
    if (sourceAllowed("button")) submit("BUTTON", constrainValue(value));
    else submit("KEYBOARD", constrainValue(value), { key: { keyCode: 13, control: false, alt: false, shift: false } });
  };
  return <section className="prompt-controller" aria-label={promptLabel}>
    <div className="prompt-heading"><p>{prompt.promptText ?? (prompt.systemInput ? "运行时菜单输入" : "等待输入")}</p><div className="prompt-heading-actions"><DeadlineClock deadlineUnixMilliseconds={prompt.deadlineUnixMilliseconds} serverTimeOffsetMilliseconds={serverTimeOffsetMilliseconds} onExpired={markDeadlineExpired} />{enterOnly && <button className="prompt-enter-button" type="button" aria-label="按回车继续" title="按回车继续" onClick={submitWithEnter} disabled={enterButtonDisabled}>{enterButtonLabel}</button>}</div></div>
    {requiresText && textInput && <form onSubmit={event => { event.preventDefault(); submitWithEnter(); }}>
      <input autoFocus type={integerInput ? "number" : "text"} value={value} onChange={event => setValue(constrainValue(event.target.value))} onKeyDown={onKeyDown} disabled={controlsDisabled} maxLength={prompt.constraints.maxLength ?? undefined} min={integerInput ? prompt.constraints.minimum ?? undefined : undefined} max={integerInput ? prompt.constraints.maximum ?? undefined : undefined} step={integerInput ? 1 : undefined} inputMode={integerInput ? "numeric" : "text"} aria-label="游戏输入" />
      <button className="prompt-enter-button" type="submit" aria-label={pending ? "发送中…" : deadlineExpired ? "等待游戏确认…" : "发送"} title="按回车继续" disabled={enterButtonDisabled}>{enterButtonLabel}</button>
    </form>}
    {prompt.inputType === "anyKey" && <button className="secondary-button prompt-any-key" type="button" autoFocus onKeyDown={onAnyKeyDown} onClick={event => event.preventDefault()} disabled={controlsDisabled || !sourceAllowed("keyboard")}>{pending ? "发送中…" : deadlineExpired ? "等待游戏确认…" : "按任意键继续"}</button>}
    {prompt.inputType === "primitivePointerKey" && <button className="secondary-button prompt-any-key" type="button" autoFocus onKeyDown={onAnyKeyDown} onClick={event => event.preventDefault()} disabled={controlsDisabled || !sourceAllowed("keyboard")}>{pending ? "发送中…" : deadlineExpired ? "等待游戏确认…" : "按键或触摸画布交互区域"}</button>}
    {prompt.inputType === "waitOnly" && <p className="input-hint" role="status" aria-live="polite">等待游戏继续，不需要浏览器输入。</p>}
    {prompt.timeoutMessage && <small className="prompt-timeout-message">{prompt.timeoutMessage}</small>}
  </section>;
}

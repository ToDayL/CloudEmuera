import { forwardRef, useCallback, useEffect, useImperativeHandle, useState } from "react";
import type { Prompt } from "../realtime/protocol";
import { DeadlineClock } from "./DeadlineClock";
import type { ConsoleInputEvent } from "./ScrollbackRenderer";

export interface PromptControllerHandle {
  submitBlankEnter: () => void;
}

interface PromptControllerProps {
  prompt?: Prompt | null;
  disabled?: boolean;
  pending?: boolean;
  serverTimeOffsetMilliseconds: number;
  onInput: (event: ConsoleInputEvent) => void;
}

export const PromptController = forwardRef<PromptControllerHandle, PromptControllerProps>(function PromptController({ prompt, disabled, pending, serverTimeOffsetMilliseconds, onInput }, ref) {
  const [value, setValue] = useState(prompt?.defaultValue ?? "");
  useEffect(() => setValue(prompt?.defaultValue ?? ""), [prompt?.promptId, prompt?.defaultValue]);

  // Prompt snapshots are presentational. The Worker decides whether a current
  // input slot accepts the attempt when it arrives.
  const controlsDisabled = Boolean(disabled || pending);
  const submit = (source: ConsoleInputEvent["source"], nextValue = value, metadata: Pick<ConsoleInputEvent, "pointer" | "key"> = {}) => {
    if (controlsDisabled) return;
    onInput({ value: nextValue, source, ...metadata });
  };
  const constrainValue = (nextValue: string): string => {
    if (!prompt) return nextValue;
    const limited = prompt.constraints.maxLength ? nextValue.slice(0, prompt.constraints.maxLength) : nextValue;
    return prompt.oneInput ? Array.from(limited)[0] ?? "" : limited;
  };
  const integerInput = prompt ? prompt.inputType === "integer" || prompt.inputType === "integerButton" : false;
  const showInputForm = Boolean(!prompt || !["anyKey", "primitivePointerKey"].includes(prompt.inputType));
  const enterOnly = prompt?.inputType === "enterKey";
  const onKeyDown = (event: React.KeyboardEvent<HTMLInputElement>) => {
    const key = { keyCode: event.keyCode || event.which, control: event.ctrlKey, alt: event.altKey, shift: event.shiftKey };
    if (!prompt && event.key === "Enter") {
      event.preventDefault();
      submit("KEYBOARD", value, { key });
    } else if (prompt?.inputType === "anyKey" || prompt?.inputType === "primitivePointerKey") {
      event.preventDefault();
      submit("KEYBOARD", constrainValue(event.key), { key });
    } else if (prompt?.inputType === "enterKey" && event.key === "Enter") {
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
  const submitBlankEnter = useCallback(() => {
    if (!showInputForm || value.length !== 0) return;
    submit("KEYBOARD", "", { key: { keyCode: 13, control: false, alt: false, shift: false } });
  }, [showInputForm, submit, value]);
  useImperativeHandle(ref, () => ({ submitBlankEnter }), [submitBlankEnter]);

  const promptLabel = prompt?.systemInput ? "游戏运行时输入" : "游戏输入提示";
  const enterButtonDisabled = controlsDisabled;
  const enterButtonClass = `prompt-enter-button ${controlsDisabled ? (pending ? "is-pending" : "is-waiting") : "is-ready"}`;
  const submitWithEnter = () => {
    const nextValue = constrainValue(value);
    submit("BUTTON", nextValue);
  };
  const deadline = prompt && <span className="sr-only"><DeadlineClock deadlineUnixMilliseconds={prompt.deadlineUnixMilliseconds} serverTimeOffsetMilliseconds={serverTimeOffsetMilliseconds} /></span>;
  const visiblePromptText = prompt?.promptText?.trim();
  return <section className="prompt-controller" aria-label={promptLabel}>
    {deadline}
    {visiblePromptText && <div className="prompt-heading"><p>{visiblePromptText}</p></div>}
    {showInputForm && <form onSubmit={event => { event.preventDefault(); submitWithEnter(); }}>
      <input autoFocus={prompt?.inputType !== "waitOnly"} type={integerInput ? "number" : "text"} value={value} onChange={event => setValue(constrainValue(event.target.value))} onKeyDown={onKeyDown} disabled={controlsDisabled} maxLength={prompt?.constraints.maxLength ?? undefined} min={integerInput ? prompt?.constraints.minimum ?? undefined : undefined} max={integerInput ? prompt?.constraints.maximum ?? undefined : undefined} step={integerInput ? 1 : undefined} inputMode={integerInput ? "numeric" : "text"} aria-label="游戏输入" />
      <button className={enterButtonClass} type="submit" aria-label={enterOnly ? "按回车继续" : "发送"} title={enterOnly ? "按回车继续" : "按回车提交"} disabled={enterButtonDisabled}>↵</button>
    </form>}
    {prompt?.inputType === "anyKey" && <button className="secondary-button prompt-any-key" type="button" autoFocus onKeyDown={onAnyKeyDown} onClick={event => event.preventDefault()} disabled={controlsDisabled}>按任意键继续</button>}
    {prompt?.inputType === "primitivePointerKey" && <button className="secondary-button prompt-any-key" type="button" autoFocus onKeyDown={onAnyKeyDown} onClick={event => event.preventDefault()} disabled={controlsDisabled}>按键或触摸画布交互区域</button>}
    {prompt?.timeoutMessage && <small className="prompt-timeout-message">{prompt.timeoutMessage}</small>}
  </section>;
});

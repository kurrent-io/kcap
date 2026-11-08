// The part of Claude Code's mod API this plugin uses, for `tsc` alone: the engine's own declarations are not ours to
// ship. `claude plugin test` runs the real engine, so this drifting from it shows there.

interface AbortSignal {
  readonly aborted: boolean
}

declare var AbortController: {
  prototype: { readonly signal: AbortSignal; abort(reason?: unknown): void }
  new (): { readonly signal: AbortSignal; abort(reason?: unknown): void }
}

declare var URL: {
  new (url: string, base?: string): { readonly pathname: string }
}

declare module 'claude-code' {
  export type Timer = { cancel: () => void }

  export type ApiContentBlock = { type: 'text'; text: string } | { type: string; [key: string]: unknown }

  export type PromptOrigin = { kind: 'composer' } | { kind: 'plugin'; name: string; asUser?: true } | { kind: 'unclassified' }

  export type SessionAppendDoor =
    | 'prompt'
    | 'command'
    | 'response'
    | 'tool-result'
    | 'tool-message'
    | 'delivery'
    | 'attachment'
    | 'hook-context'
    | 'note'
    | 'compaction'
    | 'notice'
    | 'queue'

  export type SessionAppendMessage = {
    type: 'user' | 'assistant' | 'attachment' | 'system'
    name?: string
    role?: 'user' | 'assistant'
    isMeta?: true
    content: ApiContentBlock[]
  }

  export type SessionAppendInput = {
    message: SessionAppendMessage
    door: SessionAppendDoor
    origin: PromptOrigin | { kind: 'model'; model: string } | { kind: 'tool'; tool: string }
    uuid: string
    agentId?: string
  }

  export type SessionAppendResult =
    | { message: SessionAppendMessage; uuid: string; deny?: undefined }
    | { deny: string; message?: undefined; uuid?: undefined }

  export type SessionStartInput = { cwd: string; surface: 'terminal' | 'desktop' | 'mobile' | 'vscode' | null; isInteractive: boolean }

  export type SessionEndReason = 'clear' | 'resume' | 'logout' | 'prompt_input_exit' | 'other'

  export type SessionEndInput = { reason: SessionEndReason; sessionId: string; resume: { id: string } }

  export type TurnStartInput = { text: string; turnId: string; agentId?: string }

  export type TurnCompleteReason = 'answer' | 'aborted' | 'refusal' | 'error'

  export type TurnCompleteInput = {
    answer: string
    durationMs: number
    isAborted: boolean
    turnId: string
    agentId?: string
    reason: TurnCompleteReason
  }

  export type ClassicSessionStartInput = {
    hook_event_name: 'SessionStart'
    session_id: string
    transcript_path: string
    cwd: string
    source: 'startup' | 'resume' | 'clear' | 'compact' | 'fork'
  }

  export type ClassicResult = { block?: boolean; preventContinuation?: boolean; stopReason?: string }

  export type PromptSubmitInput = { text: string; origin: PromptOrigin; wait: boolean; turnId?: string }

  export type PromptSubmitResult =
    | { text: string; context?: readonly string[]; origin?: PromptOrigin; drop?: undefined }
    | { drop: string; text?: undefined; context?: undefined; origin?: undefined }

  export type ProcessRunInit = { cwd?: string; env?: Record<string, string>; stdin?: string; timeoutMs?: number }

  export type ProcessRunResult = {
    exitCode: number
    stdout: string
    stderr: string
    isStdoutTruncated: boolean
    isStderrTruncated: boolean
  }

  export type HttpInit = { method?: string; headers?: Record<string, string>; body?: string; socketPath?: string }

  export type HttpResponse = { status: number; ok: boolean; headers: Record<string, string>; text: string }

  export type UiLogSink = 'transcript' | 'debug'

  type ValueOrDeny<V> = { value: V; deny?: undefined } | { deny: string; value?: undefined }

  /** Each event's input and result; a call on `$` is answered `{ value }` or `{ deny }`. */
  export type Events = {
    'session.start': { input: SessionStartInput; result: { cwd: string } }
    'session.end': { input: SessionEndInput; result: { sessionId: string } }
    'session.append': { input: SessionAppendInput; result: SessionAppendResult }
    'turn.start': { input: TurnStartInput; result: { turnId: string } }
    'turn.complete': { input: TurnCompleteInput; result: { text: string } }
    'prompt.submit': { input: PromptSubmitInput; result: PromptSubmitResult }
    'classic.SessionStart': { input: ClassicSessionStartInput; result: ClassicResult }
    'session.id': { input: Record<never, never>; result: ValueOrDeny<string> }
    'store.get': { input: { key: string }; result: ValueOrDeny<unknown> }
    'store.set': { input: { key: string; value: unknown }; result: ValueOrDeny<void> }
    'store.delete': { input: { key: string }; result: ValueOrDeny<void> }
    'store.keys': { input: Record<never, never>; result: ValueOrDeny<string[]> }
    'process.run': { input: { argv: readonly string[]; init?: ProcessRunInit }; result: ValueOrDeny<ProcessRunResult> }
    'http.fetch': { input: { url: string; init?: HttpInit }; result: ValueOrDeny<HttpResponse> }
    'ui.log': { input: { text: string; to: UiLogSink }; result: ValueOrDeny<void> }
  }

  export type EventName = keyof Events

  export interface EngineInterface {
    process: { run: (argv: readonly string[], init?: ProcessRunInit) => Promise<ProcessRunResult> }
    store: {
      get: (key: string) => Promise<unknown>
      set: (key: string, value: unknown) => Promise<void>
      delete: (key: string) => Promise<void>
      keys: () => Promise<string[]>
    }
    clock: {
      now: () => Promise<number>
      sleep: (ms: number, options?: { signal?: AbortSignal }) => Promise<void>
      every: (ms: number, fn: () => void) => Timer
    }
    http: { fetch: (url: string, init?: HttpInit) => Promise<HttpResponse> }
    ui: { log: (text: string, options?: { to?: UiLogSink }) => void }
    prompt: { submit: (input: { text: string; asUser?: true }) => Promise<PromptSubmitResult> }
    session: { id: () => Promise<string> }
  }

  export type Next<N extends EventName> = {
    (e: Events[N]['input']): Promise<Events[N]['result']>
    readonly called: boolean
    readonly signal: AbortSignal
    readonly budget: { readonly ms: number; readonly remainingMs: number }
  }

  export type Hook<N extends EventName> = (
    $: EngineInterface,
    e: Events[N]['input'],
    next: Next<N>,
  ) => Events[N]['result'] | Promise<Events[N]['result']>

  /** A partial of the event's input at any depth. */
  export type Matcher<I> = I extends object ? { readonly [K in keyof I]?: Matcher<I[K]> } : I | RegExp

  export type Registration<N extends EventName> = { catch: (handler: Hook<N>) => void }

  export type On = {
    <N extends EventName>(event: N, hook: Hook<N>): Registration<N>
    <N extends EventName>(event: N, matcher: Matcher<Events[N]['input']>, hook: Hook<N>): Registration<N>
  }

  export type Register = (on: On, options: Record<string, unknown>) => unknown
}

declare module 'claude-code/testing' {
  import type {
    ClassicResult,
    ClassicSessionStartInput,
    On,
    SessionAppendInput,
    SessionAppendResult,
    SessionEndInput,
    SessionStartInput,
    TurnCompleteInput,
    TurnStartInput,
  } from 'claude-code'

  /** The engine's own `$` in a test: each call runs the event's chain over every plugin. */
  export type Engine = {
    session: {
      start: (e: SessionStartInput) => Promise<{ cwd: string }>
      end: (e: SessionEndInput) => Promise<{ sessionId: string }>
      append: (e: SessionAppendInput) => Promise<SessionAppendResult>
    }
    turn: {
      start: (e: TurnStartInput) => Promise<{ turnId: string }>
      complete: (e: TurnCompleteInput) => Promise<{ text: string }>
    }
    classic: {
      SessionStart: (
        e: Omit<ClassicSessionStartInput, 'hook_event_name' | 'session_id' | 'transcript_path' | 'cwd'> &
          Partial<Pick<ClassicSessionStartInput, 'session_id' | 'transcript_path' | 'cwd'>>,
      ) => Promise<ClassicResult>
    }
  }

  export type MockClock = {
    now: () => number
    advance: (ms: number) => Promise<void>
    settle: () => Promise<void>
  }

  export const mock: {
    clock: (on: On, options?: { now?: number }) => MockClock
    session: (on: On) => { appended: () => readonly SessionAppendInput[] }
  }

  export type AsymmetricMatcher = { readonly text: string }

  export type Matchers = {
    toBe: (expected: unknown) => void
    toEqual: (expected: unknown) => void
    toMatchObject: (expected: object) => void
    toHaveLength: (length: number) => void
    toBeUndefined: () => void
    toBeGreaterThan: (bound: number | bigint) => void
  }

  export const expect: ((received: unknown, message?: string) => Matchers) & {
    stringContaining: (text: string) => AsymmetricMatcher
    objectContaining: (shape: object) => AsymmetricMatcher
  }

  export const test: (name: string, body: ($: Engine, on: On) => unknown) => void
}

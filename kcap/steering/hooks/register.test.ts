import type { On, SessionAppendInput, TurnCompleteReason } from 'claude-code'
import type { Engine } from 'claude-code/testing'
import { expect, mock, test } from 'claude-code/testing'

const NAME = 'kcap-steering'
const NOTICE = 'Remote message from kcap'
const PID = '4242'
const STARTED = 'Sat Nov  8 08:00:00 2026'
const KEY = `${PID}:Sat Nov 8 08:00:00 2026`
const ENTRY = `process:${KEY}`
const SOCKET = '/tmp/kcap-501/steering.sock'
const SESSION = '0e6f4f0a-1b1c-4c1d-8e1f-000000000001'
const NEW_SESSION = '0e6f4f0a-1b1c-4c1d-8e1f-000000000002'
const AGENT = 'mod-00000000000000a1'
const NEW_AGENT = 'mod-00000000000000a2'
const FIRST = '9b0c7d3e-0000-4000-8000-000000000001'
const SECOND = '9b0c7d3e-0000-4000-8000-000000000002'
const ROW_1 = 'c3d4e5f6-0000-4000-8000-000000000001'
const ROW_2 = 'c3d4e5f6-0000-4000-8000-000000000002'
const ROW_3 = 'c3d4e5f6-0000-4000-8000-000000000003'

type Body = Record<string, unknown>
type Reply = { status: number; body?: unknown } | 'refused' | 'missing'
type Submitted = { text: string } | { drop: string } | 'refused'

/** The engine and the daemon beneath the mod: a store in memory, Claude's process, `kcap`, and the steering socket. */
function world(on: On, options: { socket?: string; alive?: Record<string, string>; store?: Record<string, unknown> } = {}) {
  const clock = mock.clock(on, { now: 1_000_000 })
  const store = new Map<string, unknown>(Object.entries(options.store ?? {}))
  const alive: Record<string, string> = { [PID]: STARTED, ...options.alive }
  const calls: { route: string; body: Body; socketPath: string | undefined }[] = []
  const commands: string[][] = []
  const submits: { text: string; origin: unknown }[] = []
  const logs: { text: string; to: string }[] = []
  const handovers: { dispatchId: string; text: string }[] = []
  let refusedWrites = 0

  const w = {
    clock,
    store,
    commands,
    submits,
    logs,
    handovers,
    alive,
    pid: PID,
    socket: options.socket ?? SOCKET,
    session: SESSION,
    down: false,
    storeFails: false,
    replies: {} as Record<string, (body: Body) => Reply | Promise<Reply>>,
    /** The daemon's usual answer to `route`, given only once `ms` have passed on the mock clock. */
    late: (ms: number, route: string) => async (body: Body) => {
      await clock.sleep(ms)
      return daemon(route, body)
    },
    submit: (text: string): Submitted => ({ text }),
    sent: (route: string) => calls.filter(c => c.route === route).map(c => c.body),
    routes: () => calls.map(c => c.route),
    sockets: () => [...new Set(calls.map(c => c.socketPath))],
    entry: () => store.get(ENTRY) as Record<string, any> | undefined,
    refusedWrites: () => refusedWrites,
  }

  const daemon = (route: string, body: Body): Reply => {
    switch (route) {
      case '/attach':
        return { status: 200, body: { agentId: body.sessionId === NEW_SESSION ? NEW_AGENT : AGENT, pollIntervalMs: 1000 } }
      case '/next': {
        const next = body.idle === true && body.outstanding === undefined ? handovers.shift() : undefined
        return next ? { status: 200, body: next } : { status: 204 }
      }
      default:
        return { status: 200, body: {} }
    }
  }

  on('store.get', (_, e) => ({ value: store.get(e.key) }))
  on('store.keys', () => ({ value: [...store.keys()] }))
  on('store.set', (_, e) => {
    if (w.storeFails) {
      refusedWrites++
      throw new Error('the store refused the write')
    }
    store.set(e.key, JSON.parse(JSON.stringify(e.value)))
    return { value: undefined }
  })
  on('store.delete', (_, e) => {
    store.delete(e.key)
    return { value: undefined }
  })

  on('process.run', (_, e) => {
    commands.push([...e.argv])
    const [command, ...args] = e.argv
    const out = (stdout: string) => ({ value: { exitCode: 0, stdout, stderr: '', isStdoutTruncated: false, isStderrTruncated: false } })

    if (command === 'sh') return out(`${w.pid}\n`)
    if (command === 'kcap') return out(w.socket ? `${w.socket}\n` : '')
    if (command === 'ps') {
      const pids = (args[args.indexOf('-p') + 1] ?? '').split(',')
      return out(pids.filter(p => alive[p]).map(p => `${p.padStart(5)} ${alive[p]}\n`).join(''))
    }

    throw new Error(`unexpected command: ${e.argv.join(' ')}`)
  })

  on('http.fetch', async (_, e) => {
    const route = new URL(e.url).pathname
    const body = JSON.parse(e.init?.body ?? '{}') as Body
    calls.push({ route, body, socketPath: e.init?.socketPath })

    const reply = w.down ? 'refused' : await (w.replies[route] ?? (b => daemon(route, b)))(body)
    if (reply === 'refused') throw new Error('connect ECONNREFUSED')
    if (reply === 'missing') throw new Error(`connect ENOENT ${e.init?.socketPath}`)

    const text = reply.body === undefined ? '' : JSON.stringify(reply.body)
    return { value: { status: reply.status, ok: reply.status < 300, headers: {}, text } }
  })

  on('prompt.submit', (_, e) => {
    submits.push({ text: e.text, origin: e.origin })
    const answer = w.submit(e.text)
    if (answer === 'refused') throw new Error('the engine refused the prompt')
    return answer
  })

  on('ui.log', (_, e) => {
    logs.push({ text: e.text, to: e.to })
    return { value: undefined }
  })

  on('session.id', () => ({ value: w.session }))
  on('session.start', (_, e) => ({ cwd: e.cwd }))
  on('session.end', (_, e) => ({ sessionId: e.sessionId }))
  on('turn.start', (_, e) => ({ turnId: e.turnId }))
  on('turn.complete', (_, e) => ({ text: e.answer }))
  on('classic.SessionStart', () => ({}))

  return w
}

type World = ReturnType<typeof world>

async function begin($: Engine, w: World) {
  await $.session.start({ cwd: '/work', surface: 'terminal', isInteractive: true })
  await w.clock.settle()
}

const own: SessionAppendInput['origin'] = { kind: 'plugin', name: NAME, asUser: true }

function row($: Engine, text: string, uuid: string, origin: SessionAppendInput['origin'] = own) {
  return $.session.append({ message: { type: 'user', role: 'user', content: [{ type: 'text', text }] }, door: 'prompt', origin, uuid })
}

function turnStart($: Engine, turnId: string) {
  return $.turn.start({ text: '', turnId })
}

function turnEnd($: Engine, turnId: string, reason: Exclude<TurnCompleteReason, 'refusal'> = 'answer') {
  return $.turn.complete({ answer: '', durationMs: 1, isAborted: reason === 'aborted', turnId, reason })
}

test('It_submits_one_dispatch_at_a_time_from_a_timer', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'first' }, { dispatchId: SECOND, text: 'second' })

  await begin($, w)
  expect(w.submits).toEqual([])

  await w.clock.advance(1000)
  expect(w.submits).toEqual([{ text: 'first', origin: own }])

  await w.clock.advance(3000)
  expect(w.submits.map(s => s.text)).toEqual(['first'])
  expect(w.sent('/next').slice(-3).map(b => b.outstanding)).toEqual([FIRST, FIRST, FIRST])

  await row($, 'first', ROW_1)
  await w.clock.advance(1000)
  expect(w.submits.map(s => s.text)).toEqual(['first', 'second'])
  expect(w.sockets()).toEqual([SOCKET])
})

test('It_links_a_row_only_when_its_text_matches', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'hello' })
  await begin($, w)
  await w.clock.advance(1000)

  await row($, 'hello there', ROW_1)
  await row($, 'hello', ROW_2, { kind: 'composer' })
  await w.clock.advance(1000)
  expect(w.sent('/link')).toEqual([])

  await row($, 'hello', ROW_3)
  await w.clock.advance(1000)
  expect(w.sent('/link')).toEqual([{ agentId: AGENT, dispatchId: FIRST, sessionId: SESSION, rowUuid: ROW_3, processKey: KEY }])
  expect(w.sent('/next').at(-1)?.outstanding).toBeUndefined()
})

// A hook beneath can store other words than the mod proposed, as a redaction does, so only the stored row says whether
// the dispatch landed.
test('A_row_is_matched_as_it_was_stored', async ($, on) => {
  const w = world(on)
  const stored: Record<string, string> = { hello: 'hello, redacted', 'hello there': 'hello' }
  on('session.append', (_, e, next) => {
    const text = e.message.content.map(b => ('text' in b ? String(b.text) : '')).join('')
    return next({ ...e, message: { ...e.message, content: [{ type: 'text', text: stored[text] ?? text }] } })
  })
  w.handovers.push({ dispatchId: FIRST, text: 'hello' })
  await begin($, w)
  await w.clock.advance(1000)

  await row($, 'hello', ROW_1)
  await w.clock.advance(1000)
  expect(w.sent('/link')).toEqual([])

  await row($, 'hello there', ROW_2)
  await w.clock.advance(1000)
  expect(w.sent('/link').map(b => b.rowUuid)).toEqual([ROW_2])
})

// A refused append stores nothing, so it links nothing, and the submit's own rejection still reports the dispatch.
test('A_refused_append_links_nothing', async ($, on) => {
  const w = world(on)
  on('session.append', () => ({ deny: 'refused beneath' }))
  w.handovers.push({ dispatchId: FIRST, text: 'refused' })
  await begin($, w)
  await w.clock.advance(1000)

  await $.session.append({ message: { type: 'user', role: 'user', content: [{ type: 'text', text: 'refused' }] }, door: 'note', origin: own, uuid: ROW_1 })
  await w.clock.advance(1000)

  expect(w.sent('/link')).toEqual([])
  expect(w.entry()?.held).toMatchObject({ dispatchId: FIRST, phase: 'submitted' })
})

test('A_remote_message_shows_a_notice_at_the_terminal', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'from the phone' })
  await begin($, w)
  expect(w.logs.filter(l => l.to === 'transcript')).toEqual([])

  await w.clock.advance(1000)
  expect(w.logs.filter(l => l.to === 'transcript')).toEqual([{ text: NOTICE, to: 'transcript' }])
  expect(w.submits.map(s => s.text)).toEqual(['from the phone'])
})

test('Entries_are_keyed_by_process_and_stale_keys_are_removed', async ($, on) => {
  const live = 'process:5555:Sat Nov 8 07:00:00 2026'
  const exited = 'process:6666:Sat Nov 8 06:00:00 2026'
  const reused = 'process:7777:Fri Nov 7 06:00:00 2026'
  const w = world(on, {
    alive: { '5555': 'Sat Nov  8 07:00:00 2026', '7777': 'Sat Nov  8 07:30:00 2026' },
    store: { [live]: { reports: [] }, [exited]: { reports: [] }, [reused]: { reports: [] }, unrelated: 1 },
  })
  w.handovers.push({ dispatchId: FIRST, text: 'mine' })

  await begin($, w)
  await w.clock.advance(1000)

  expect([...w.store.keys()].sort()).toEqual([ENTRY, live, 'unrelated'].sort())
  expect(w.entry()?.held).toMatchObject({ dispatchId: FIRST, text: 'mine' })
  expect(w.sent('/attach')).toEqual([{ sessionId: SESSION, processKey: KEY }])
})

test('It_stays_inert_with_no_socket', async ($, on) => {
  const dead = 'process:6666:Sat Nov 8 06:00:00 2026'
  const w = world(on, { socket: '', store: { [dead]: { reports: [] } } })
  w.handovers.push({ dispatchId: FIRST, text: 'never' })

  await begin($, w)
  await w.clock.advance(60_000)

  expect(w.commands.filter(c => c[0] === 'kcap')).toEqual([['kcap', 'daemon', 'steering-socket']])
  expect(w.routes()).toEqual([])
  expect(w.submits).toEqual([])
  expect(w.store.has(dead)).toBe(false)
})

test('It_asks_for_a_dispatch_only_when_no_turn_is_running', async ($, on) => {
  const w = world(on)
  await begin($, w)
  await w.clock.advance(1000)
  expect(w.sent('/next').map(b => b.idle)).toEqual([true])

  await turnStart($, 't1')
  w.handovers.push({ dispatchId: FIRST, text: 'after the turn' })
  await w.clock.advance(1000)
  expect(w.sent('/next').map(b => b.idle)).toEqual([true, false])

  // A reload mid-turn: the new load knows of the running turn only from the store.
  await begin($, w)
  await w.clock.advance(2000)
  expect(w.sent('/next').map(b => b.idle)).toEqual([true, false, false, false])
  expect(w.submits).toEqual([])

  await turnEnd($, 't1')
  await w.clock.advance(1000)
  expect(w.sent('/next').map(b => b.idle)).toEqual([true, false, false, false, true])
  expect(w.submits.map(s => s.text)).toEqual(['after the turn'])
})

test('A_row_with_nothing_outstanding_is_reported_unmatched', async ($, on) => {
  const w = world(on)
  await begin($, w)
  await w.clock.advance(1000)

  await row($, 'nobody sent this', ROW_1)
  await w.clock.advance(2000)

  expect(w.sent('/link')).toEqual([])
  expect(w.logs).toEqual([{ text: expect.stringContaining(`unmatched ${ROW_1}`), to: 'debug' }])
})

test('A_drop_reports_rejected', async ($, on) => {
  const w = world(on)
  w.submit = () => ({ drop: 'a hook blocked it' })
  w.handovers.push({ dispatchId: FIRST, text: 'dropped' })
  await begin($, w)
  await w.clock.advance(2000)

  expect(w.sent('/undelivered')).toEqual([{ agentId: AGENT, dispatchId: FIRST, outcome: 'rejected', reason: 'dropped', processKey: KEY }])
  expect(w.sent('/next').at(-1)?.outstanding).toBeUndefined()
  expect(w.entry()?.held).toBeUndefined()
})

test('A_refused_submit_reports_rejected', async ($, on) => {
  const w = world(on)
  w.submit = () => 'refused'
  w.handovers.push({ dispatchId: FIRST, text: 'refused' })
  await begin($, w)
  await w.clock.advance(2000)

  expect(w.sent('/undelivered')).toEqual([{ agentId: AGENT, dispatchId: FIRST, outcome: 'rejected', reason: 'refused', processKey: KEY }])
  expect(w.entry()?.held).toBeUndefined()
})

test('A_drop_after_an_aborted_turn_reports_stopped', async ($, on) => {
  const w = world(on)
  w.submit = () => ({ drop: 'the session was stopped and nobody has sent input since' })
  await begin($, w)
  await turnStart($, 't1')
  await turnEnd($, 't1', 'aborted')

  w.handovers.push({ dispatchId: FIRST, text: 'while stopped' })
  await w.clock.advance(2000)
  expect(w.sent('/undelivered').map(b => b.reason)).toEqual(['stopped'])

  // Input at the terminal since the abort lifts the stop, so a later drop is an ordinary one.
  await turnStart($, 't2')
  await turnEnd($, 't2')
  w.handovers.push({ dispatchId: SECOND, text: 'after input' })
  await w.clock.advance(2000)
  expect(w.sent('/undelivered').map(b => [b.dispatchId, b.reason])).toEqual([
    [FIRST, 'stopped'],
    [SECOND, 'dropped'],
  ])
})

test('Idle_with_no_row_after_grace_reports_undelivered_and_never_resubmits', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'no row comes' })
  await begin($, w)
  await w.clock.advance(1000)
  expect(w.submits).toHaveLength(1)

  await w.clock.advance(9000)
  expect(w.sent('/undelivered')).toEqual([])

  await w.clock.advance(2000)
  expect(w.sent('/undelivered')).toEqual([{ agentId: AGENT, dispatchId: FIRST, outcome: 'unconfirmed', reason: 'no_row', processKey: KEY }])

  await w.clock.advance(30_000)
  expect(w.submits).toHaveLength(1)
  expect(w.sent('/undelivered')).toHaveLength(1)
  expect(w.sent('/next').at(-1)?.outstanding).toBe(FIRST)

  // A late row still lands its link.
  await row($, 'no row comes', ROW_1)
  await w.clock.advance(1000)
  expect(w.sent('/link').map(b => b.rowUuid)).toEqual([ROW_1])
  expect(w.sent('/next').at(-1)?.outstanding).toBeUndefined()
})

test('The_outstanding_dispatch_survives_a_reload_and_a_session_change', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'carried' })
  await begin($, w)
  await w.clock.advance(1000)
  expect(w.submits).toHaveLength(1)

  await begin($, w)
  await w.clock.advance(1000)
  expect(w.sent('/next').at(-1)).toMatchObject({ agentId: AGENT, outstanding: FIRST })

  await $.session.end({ reason: 'clear', sessionId: SESSION, resume: { id: SESSION } })
  await $.classic.SessionStart({ source: 'clear', session_id: NEW_SESSION })
  await w.clock.settle()
  await w.clock.advance(1000)
  expect(w.sent('/next').at(-1)).toMatchObject({ agentId: NEW_AGENT, outstanding: FIRST })
  expect(w.submits).toHaveLength(1)

  // The replacement's attach is where the server counts the dispatch Unconfirmed, so the mod never reports it as well.
  await w.clock.advance(15_000)
  expect(w.sent('/undelivered')).toEqual([])
  expect(w.sent('/next').at(-1)).toMatchObject({ agentId: NEW_AGENT, outstanding: FIRST })

  w.session = NEW_SESSION
  await row($, 'carried', ROW_1)
  await w.clock.advance(1000)
  expect(w.sent('/link')).toEqual([{ agentId: AGENT, dispatchId: FIRST, sessionId: NEW_SESSION, rowUuid: ROW_1, processKey: KEY }])
  expect(w.sent('/undelivered')).toEqual([])
})

test('Session_end_on_clear_attaches_a_replacement_naming_the_old_agent', async ($, on) => {
  const w = world(on)
  await begin($, w)
  await w.clock.advance(1000)

  await $.session.end({ reason: 'clear', sessionId: SESSION, resume: { id: SESSION } })
  await w.clock.advance(1000)
  expect(w.sent('/next').at(-1)).toMatchObject({ agentId: AGENT, idle: false })

  // Inside this hook `$.session.id()` still answers the old id, so the new one is the event's.
  await $.classic.SessionStart({ source: 'clear', session_id: NEW_SESSION })
  await w.clock.settle()
  await w.clock.advance(1000)

  expect(w.sent('/attach')).toEqual([
    { sessionId: SESSION, processKey: KEY },
    { sessionId: NEW_SESSION, processKey: KEY, replacesAgentId: AGENT },
  ])
  expect(w.sent('/next').at(-1)).toMatchObject({ agentId: NEW_AGENT, idle: true })
  expect(w.sent('/detach')).toEqual([])
  expect(w.commands.filter(c => c[0] === 'kcap')).toHaveLength(2)
})

test('Session_end_on_exit_detaches', async ($, on) => {
  const w = world(on)
  await begin($, w)
  await w.clock.advance(1000)

  await $.session.end({ reason: 'prompt_input_exit', sessionId: SESSION, resume: { id: SESSION } })
  expect(w.sent('/detach')).toEqual([{ agentId: AGENT, processKey: KEY }])
  expect(w.store.has(ENTRY)).toBe(false)

  await w.clock.advance(5000)
  expect(w.sent('/next')).toHaveLength(1)
})

test('A_refused_attach_retries_until_the_first_process_has_gone', async ($, on) => {
  const w = world(on)
  let refusals = 2
  w.replies['/attach'] = () =>
    refusals-- > 0
      ? { status: 200, body: { refused: 'session_attached', retry: true } }
      : { status: 200, body: { agentId: AGENT, pollIntervalMs: 1000 } }

  await begin($, w)
  await w.clock.advance(1000)
  await w.clock.advance(9000)
  expect(w.sent('/attach')).toHaveLength(1)

  await w.clock.advance(1000)
  expect(w.sent('/attach')).toHaveLength(2)
  expect(w.sent('/next')).toEqual([])

  await w.clock.advance(10_000)
  expect(w.sent('/attach')).toHaveLength(3)
  expect(w.sent('/next')).toHaveLength(1)
})

test('An_unreported_link_is_retried_after_the_daemon_returns', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'linked late' })
  await begin($, w)
  await w.clock.advance(1000)

  w.down = true
  await row($, 'linked late', ROW_1)
  await w.clock.advance(3000)
  expect(w.sent('/link')).toHaveLength(3)
  expect(w.entry()?.reports).toEqual([expect.objectContaining({ route: '/link', body: expect.objectContaining({ rowUuid: ROW_1 }) })])
  expect(w.sent('/next').at(-1)?.outstanding).toBe(FIRST)

  w.down = false
  w.replies['/link'] = () => ({ status: 503 })
  await w.clock.advance(1000)
  expect(w.entry()?.reports).toHaveLength(1)

  delete w.replies['/link']
  await w.clock.advance(1000)
  expect(w.entry()?.reports).toEqual([])
  expect(w.entry()?.held).toBeUndefined()
  expect(w.sent('/next').at(-1)?.outstanding).toBeUndefined()
  expect([...new Set(w.sent('/link').map(b => b.rowUuid))]).toEqual([ROW_1])
})

test('A_failing_hook_passes_the_row_through', async ($, on) => {
  const w = world(on)
  const session = mock.session(on)
  w.handovers.push({ dispatchId: FIRST, text: 'kept' })
  await begin($, w)
  await w.clock.advance(1000)

  w.storeFails = true
  const stored = await row($, 'kept', ROW_1)

  expect(w.refusedWrites()).toBeGreaterThan(0)
  expect(stored).toEqual({ message: { type: 'user', role: 'user', content: [{ type: 'text', text: 'kept' }] }, uuid: ROW_1 })
  expect(session.appended().map(r => r.uuid)).toEqual([ROW_1])
})

test('What_a_hook_sees_while_a_reload_sets_up_is_kept', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'landed mid-reload' })
  await begin($, w)
  await w.clock.advance(1000)

  const reloading = $.session.start({ cwd: '/work', surface: 'terminal', isInteractive: true })
  await Promise.all([reloading, row($, 'landed mid-reload', ROW_1), turnStart($, 't1')])
  await w.clock.settle()
  await w.clock.advance(1000)

  expect(w.sent('/link').map(b => b.rowUuid)).toEqual([ROW_1])
  expect(w.sent('/next').at(-1)?.idle).toBe(false)
})

test('A_gone_agent_attaches_again_unless_told_not_to', async ($, on) => {
  const w = world(on)
  await begin($, w)
  await w.clock.advance(1000)

  w.replies['/next'] = () => ({ status: 200, body: { gone: 'lost', retry: true } })
  await w.clock.advance(1000)
  delete w.replies['/next']
  await w.clock.advance(1000)
  expect(w.routes()).toEqual(['/attach', '/next', '/next', '/attach', '/next'])

  w.replies['/next'] = () => ({ status: 200, body: { gone: 'stopped', retry: false } })
  await w.clock.advance(1000)
  await w.clock.advance(30_000)
  expect(w.routes()).toEqual(['/attach', '/next', '/next', '/attach', '/next', '/next'])
})

test('A_hanging_call_and_a_missing_socket_are_retried', async ($, on) => {
  const w = world(on)
  // Longer than the mod's own bound for /next, so the tick gives up on this one and a later tick asks again.
  w.replies['/next'] = w.late(60_000, '/next')
  await begin($, w)
  await w.clock.advance(1000)
  expect(w.sent('/next')).toHaveLength(1)

  await w.clock.advance(12_000)
  expect(w.sent('/next')).toHaveLength(2)

  w.handovers.push({ dispatchId: FIRST, text: 'after the hang' })
  delete w.replies['/next']
  await w.clock.advance(13_000)
  expect(w.submits.map(s => s.text)).toEqual(['after the hang'])

  w.replies['/link'] = () => 'missing'
  await row($, 'after the hang', ROW_1)
  await w.clock.advance(2000)
  expect(w.sent('/link')).toHaveLength(2)
  expect(w.entry()?.reports).toHaveLength(1)

  delete w.replies['/link']
  await w.clock.advance(1000)
  expect(w.sent('/link')).toHaveLength(3)
  expect(w.entry()?.reports).toEqual([])
})

test('A_final_refusal_leaves_the_mod_inert', async ($, on) => {
  const w = world(on)
  w.replies['/attach'] = () => ({ status: 200, body: { refused: 'hosted', retry: false } })
  w.handovers.push({ dispatchId: FIRST, text: 'never' })

  await begin($, w)
  await w.clock.advance(60_000)
  expect(w.sent('/attach')).toHaveLength(1)
  expect(w.sent('/next')).toEqual([])

  // A new session id is a fresh judgement, so the mod asks again.
  await $.session.end({ reason: 'clear', sessionId: SESSION, resume: { id: SESSION } })
  await $.classic.SessionStart({ source: 'clear', session_id: NEW_SESSION })
  await w.clock.settle()
  delete w.replies['/attach']
  await w.clock.advance(1000)
  expect(w.sent('/attach')).toHaveLength(2)
})

test('A_resume_attaches_a_replacement_rather_than_detaching', async ($, on) => {
  const w = world(on)
  await begin($, w)
  await w.clock.advance(1000)

  await $.session.end({ reason: 'resume', sessionId: SESSION, resume: { id: SESSION } })
  await $.classic.SessionStart({ source: 'resume', session_id: NEW_SESSION })
  await w.clock.settle()
  await w.clock.advance(1000)

  expect(w.sent('/detach')).toEqual([])
  expect(w.sent('/attach').at(-1)).toEqual({ sessionId: NEW_SESSION, processKey: KEY, replacesAgentId: AGENT })
})

test('The_grace_waits_while_a_turn_runs', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'runs long' })
  await begin($, w)
  await w.clock.advance(1000)
  expect(w.submits).toHaveLength(1)

  await turnStart($, 't1')
  await w.clock.advance(30_000)
  expect(w.sent('/undelivered')).toEqual([])

  await turnEnd($, 't1')
  await w.clock.advance(2000)
  expect(w.sent('/undelivered')).toEqual([])

  await w.clock.advance(9000)
  expect(w.sent('/undelivered').map(b => b.reason)).toEqual(['no_row'])
})

test('A_session_change_while_the_daemon_is_down_keeps_the_socket', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'carried' })
  await begin($, w)
  await w.clock.advance(1000)
  expect(w.submits).toHaveLength(1)

  // The daemon is restarting: it answers nothing, and `kcap daemon steering-socket` prints nothing.
  w.down = true
  w.socket = ''
  await $.session.end({ reason: 'clear', sessionId: SESSION, resume: { id: SESSION } })
  await $.classic.SessionStart({ source: 'clear', session_id: NEW_SESSION })
  await w.clock.settle()

  w.session = NEW_SESSION
  await row($, 'carried', ROW_1)
  await w.clock.advance(2000)
  expect(w.sent('/link')).toHaveLength(2)
  expect(w.sockets()).toEqual([SOCKET])

  w.down = false
  await w.clock.advance(1000)
  expect(w.sent('/link').at(-1)).toMatchObject({ rowUuid: ROW_1, sessionId: NEW_SESSION })
  expect(w.entry()?.reports).toEqual([])

  // Only the attach waited for the path: the daemon is back but has printed none, so no agent is claimed.
  expect(w.sent('/attach')).toHaveLength(1)

  w.socket = SOCKET
  await $.session.end({ reason: 'clear', sessionId: NEW_SESSION, resume: { id: NEW_SESSION } })
  await $.classic.SessionStart({ source: 'clear', session_id: SESSION })
  await w.clock.settle()
  await w.clock.advance(1000)
  expect(w.sent('/attach')).toHaveLength(2)
})

test('A_refused_link_keeps_the_barrier', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'landed' })
  await begin($, w)
  await w.clock.advance(1000)

  w.replies['/link'] = () => ({ status: 400, body: { error: 'invalid_request' } })
  await row($, 'landed', ROW_1)
  await w.clock.advance(2000)

  expect(w.sent('/link')).toHaveLength(1)
  expect(w.entry()?.reports).toEqual([])
  expect(w.entry()?.held).toMatchObject({ dispatchId: FIRST, phase: 'linked' })
  expect(w.sent('/next').at(-1)?.outstanding).toBe(FIRST)
})

test('Owed_reports_outlive_an_exit_and_a_stale_key', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'owed' })
  await begin($, w)
  await w.clock.advance(1000)

  w.down = true
  await row($, 'owed', ROW_1)
  await w.clock.advance(1000)
  await $.session.end({ reason: 'prompt_input_exit', sessionId: SESSION, resume: { id: SESSION } })
  expect(w.entry()?.reports).toHaveLength(1)
  expect(w.entry()?.agentId).toBeUndefined()

  // The next Claude process finds that key stale, and sends what it owed before removing it.
  w.down = false
  delete w.alive[PID]
  w.pid = '5555'
  w.alive['5555'] = 'Sat Nov  8 09:00:00 2026'
  await begin($, w)
  await w.clock.advance(1000)

  expect(w.sent('/link').at(-1)).toMatchObject({ dispatchId: FIRST, rowUuid: ROW_1 })
  expect(w.store.has(ENTRY)).toBe(false)
})

test('Session_end_for_another_session_changes_nothing', async ($, on) => {
  const w = world(on)
  await begin($, w)
  await w.clock.advance(1000)

  await $.session.end({ reason: 'clear', sessionId: NEW_SESSION, resume: { id: NEW_SESSION } })
  await w.clock.advance(1000)

  expect(w.entry()?.changing).toBeUndefined()
  expect(w.sent('/next').at(-1)).toMatchObject({ agentId: AGENT, idle: true })
})

test('A_retired_load_neither_records_nor_submits_a_handover', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'answered too late' })
  w.replies['/next'] = w.late(3000, '/next')
  await begin($, w)
  await w.clock.advance(1000)
  expect(w.sent('/next')).toHaveLength(1)

  // The module reloads while that poll is in flight, so its answer reaches a load whose writes are dropped.
  await begin($, w)
  await w.clock.advance(3000)

  expect(w.submits).toEqual([])
  expect(w.entry()?.held).toBeUndefined()
})

test('A_report_taken_at_exit_is_not_sent_again', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'landed at exit' })
  await begin($, w)
  await w.clock.advance(1000)

  await row($, 'landed at exit', ROW_1)
  await $.session.end({ reason: 'prompt_input_exit', sessionId: SESSION, resume: { id: SESSION } })
  expect(w.sent('/link')).toHaveLength(1)
  expect(w.store.has(ENTRY)).toBe(false)

  delete w.alive[PID]
  w.pid = '5555'
  w.alive['5555'] = 'Sat Nov  8 09:00:00 2026'
  await begin($, w)
  await w.clock.advance(1000)
  expect(w.sent('/link')).toHaveLength(1)
})

test('A_sweep_keeps_only_what_is_still_owed', async ($, on) => {
  const dead = 'process:6666:Sat Nov 8 06:00:00 2026'
  const link = { route: '/link', dispatchId: FIRST, settles: true, body: { agentId: AGENT, dispatchId: FIRST, rowUuid: ROW_1 } }
  const rejected = { route: '/undelivered', dispatchId: SECOND, settles: true, body: { agentId: AGENT, dispatchId: SECOND } }
  const w = world(on, { store: { [dead]: { reports: [link, rejected] } } })
  w.replies['/undelivered'] = () => ({ status: 503 })

  await begin($, w)
  expect(w.sent('/link')).toHaveLength(1)
  expect(w.store.get(dead)).toEqual({ reports: [rejected] })
})

// A daemon that is away or failing when the sweep starts leaves what a gone process owed for a later tick.
test('A_sweep_the_daemon_cut_short_is_tried_again', async ($, on) => {
  const dead = 'process:6666:Sat Nov 8 06:00:00 2026'
  const link = { route: '/link', dispatchId: FIRST, settles: true, body: { agentId: AGENT, dispatchId: FIRST, rowUuid: ROW_1 } }
  const w = world(on, { store: { [dead]: { reports: [link] } } })
  w.replies['/link'] = () => ({ status: 503 })

  await begin($, w)
  expect(w.sent('/link')).toHaveLength(1)
  expect(w.store.has(dead)).toBe(true)

  await w.clock.advance(1000)
  expect(w.sent('/link')).toHaveLength(2)

  delete w.replies['/link']
  await w.clock.advance(1000)
  expect(w.sent('/link')).toHaveLength(3)
  expect(w.store.has(dead)).toBe(false)

  await w.clock.advance(3000)
  expect(w.sent('/link')).toHaveLength(3)
})

test('A_lost_path_is_looked_up_again_until_the_daemon_is_back', async ($, on) => {
  const w = world(on)
  w.handovers.push({ dispatchId: FIRST, text: 'waiting it out' })
  await begin($, w)
  await w.clock.advance(1000)
  expect(w.submits).toHaveLength(1)

  w.down = true
  w.socket = ''
  await $.session.end({ reason: 'clear', sessionId: SESSION, resume: { id: SESSION } })
  await $.classic.SessionStart({ source: 'clear', session_id: NEW_SESSION })
  await w.clock.settle()

  await w.clock.advance(10_000)
  w.down = false
  w.socket = SOCKET
  await w.clock.advance(19_000)
  expect(w.sent('/attach')).toHaveLength(1)
  expect(w.commands.filter(c => c[0] === 'kcap')).toHaveLength(2)

  await w.clock.advance(1000)
  expect(w.commands.filter(c => c[0] === 'kcap')).toHaveLength(3)
  expect(w.sent('/attach').at(-1)).toEqual({ sessionId: NEW_SESSION, processKey: KEY, replacesAgentId: AGENT })
  expect(w.sent('/next').at(-1)).toMatchObject({ agentId: NEW_AGENT, outstanding: FIRST })
})

test('A_refused_rejection_report_still_settles', async ($, on) => {
  const w = world(on)
  w.submit = () => ({ drop: 'a hook blocked it' })
  w.replies['/undelivered'] = () => ({ status: 400, body: { error: 'invalid_request' } })
  w.handovers.push({ dispatchId: FIRST, text: 'first drop' }, { dispatchId: SECOND, text: 'second drop' })

  await begin($, w)
  await w.clock.advance(2000)

  expect(w.sent('/undelivered').map(b => b.dispatchId)).toEqual([FIRST])
  expect(w.submits.map(s => s.text)).toEqual(['first drop', 'second drop'])
})

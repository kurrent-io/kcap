import type { Register, SessionEndReason, Timer } from 'claude-code'

const NAME = 'kcap-steering'
const NOTICE = 'Remote message from kcap'
const ENTRY = 'process:'
const POLL_MS = 1000
const SLOW_ATTACH_MS = 10_000
const LOOK_AGAIN_MS = 30_000
// Above the daemon's own budgets, so the mod never abandons a call the daemon still answers: a poll spends at most 8 s
// on the server however many handovers it confirms, and an attach 1 s on whether the server steers and 15 s on the
// registration.
const CALL_MS = 12_000
const ATTACH_MS = 17_000
// A late link corrects a false alarm, so the grace only has to outlast a normal landing.
const GRACE_MS = 10_000

type Phase = 'received' | 'submitted' | 'unconfirmed' | 'linked' | 'rejected'

type Held = { dispatchId: string; text: string; agentId: string; phase: Phase; submittedAt?: number }

/** A report the daemon has not taken yet; one that `settles` ends the held dispatch once it is taken. */
type Report = { route: '/link' | '/undelivered'; dispatchId: string; settles: boolean; body: Record<string, unknown> }

type Turn = { running: boolean; aborted: boolean; at: number }

/** One Claude process's state, under its own key: the store is one file that every Claude process shares. */
type Entry = {
  sessionId?: string
  agentId?: string
  replaces?: string
  changing?: boolean
  held?: Held
  reports: Report[]
  turn: Turn
}

type Row = { uuid: string; text: string; sessionId: string }

type Live = {
  retire: () => void
  turn: (turn: Turn) => Promise<void>
  session: (sessionId: string) => Promise<void>
  ended: (sessionId: string, reason: SessionEndReason, budgetMs: number) => Promise<void>
  row: (row: Row) => Promise<void>
}

// Built by each load's `session.start`. What a hook sees before then waits in `early`: a row is never raised again.
let live: Live | undefined
let early: { turn?: Turn; sessionId?: string; rows: Row[] } = { rows: [] }

const noteTurn = async (turn: Turn) => {
  if (live) await live.turn(turn)
  else early.turn = turn
}

const noteSession = async (sessionId: string) => {
  if (live) await live.session(sessionId)
  else early.sessionId = sessionId
}

const noteRow = async (row: Row) => {
  if (live) await live.row(row)
  else early.rows.push(row)
}

const textOf = (content: readonly unknown[]) =>
  content.map(b => (b && typeof b === 'object' && 'type' in b && b.type === 'text' && 'text' in b ? String(b.text) : '')).join('')

const parse = (text: string): Record<string, unknown> => {
  try {
    const value: unknown = JSON.parse(text)
    return value && typeof value === 'object' ? (value as Record<string, unknown>) : {}
  } catch {
    return {}
  }
}

export const register: Register = on => {
  // The engine refuses a module that binds or passes `$`, so the helpers are closures over this hook's.
  on('session.start', async ($, e, next) => {
    const started = await next(e)

    live?.retire()
    live = undefined

    const pid = (await $.process.run(['sh', '-c', 'echo $PPID'], { timeoutMs: 2000 })).stdout.trim()
    if (!/^\d+$/.test(pid)) return started

    // Under another locale a start time would not match the key another process wrote for itself.
    const startTimes = async (pids: string[]) => {
      const { stdout } = await $.process.run(['ps', '-o', 'pid=,lstart=', '-p', pids.join(',')], {
        env: { LC_ALL: 'C' },
        timeoutMs: 2000,
      })
      const times = new Map<string, string>()
      for (const line of stdout.split('\n')) {
        const m = /^\s*(\d+)\s+(\S.*?)\s*$/.exec(line)
        if (m) times.set(m[1]!, m[2]!.replace(/\s+/g, ' '))
      }
      return times
    }

    const entries = (await $.store.keys()).filter(k => k.startsWith(ENTRY))
    const pidOf = (k: string) => k.slice(ENTRY.length).split(':')[0] ?? ''
    const times = await startTimes([...new Set([pid, ...entries.map(pidOf).filter(p => /^\d+$/.test(p))])])
    const since = times.get(pid)
    if (!since) return started

    const key = `${pid}:${since}`
    const storeKey = ENTRY + key
    const stale = entries.filter(k => k !== storeKey && k.slice(ENTRY.length) !== `${pidOf(k)}:${times.get(pidOf(k))}`)

    const stored = await $.store.get(storeKey)
    const entry: Entry = {
      reports: [],
      turn: { running: false, aborted: false, at: 0 },
      ...(stored && typeof stored === 'object' ? (stored as Partial<Entry>) : {}),
    }

    let retired = false
    let saving = Promise.resolve()
    const save = () => {
      if (retired) return Promise.resolve()
      const value = JSON.parse(JSON.stringify(entry)) as Entry
      const done = saving.then(() => $.store.set(storeKey, value))
      saving = done.catch(() => {})
      return done
    }

    let socket: string | undefined
    let known = false
    let lookAgainAt = 0
    let timer: Timer | undefined
    let every = POLL_MS
    let ticking = false
    let inert = false
    let attachAfter = 0

    const call = async (route: string, body: Record<string, unknown>, ms: number) => {
      if (!socket) return undefined

      const stop = new AbortController()
      const late = $.clock.sleep(ms, { signal: stop.signal }).then(() => undefined)
      try {
        return await Promise.race([
          $.http.fetch(`http://kcap${route}`, {
            method: 'POST',
            socketPath: socket,
            headers: { 'content-type': 'application/json' },
            body: JSON.stringify(body),
          }),
          late,
        ])
      } catch {
        return undefined
      } finally {
        stop.abort()
      }
    }

    const run = (ms: number) => {
      timer?.cancel()
      every = ms
      timer = $.clock.every(ms, () => void tick())
    }

    // No path means steering is off, no daemon serves this session's profile, or the daemon is away: only the attach
    // waits for one. A path already found is kept and retried, because that is also how a daemon restart looks, and
    // the tick asks for a path again now and then. A session that never had one runs no timer, so asks nothing.
    const connect = async () => {
      const found = await $.process.run(['kcap', 'daemon', 'steering-socket'], { timeoutMs: 5000 }).catch(() => undefined)
      const path = found?.exitCode === 0 ? (found.stdout.split('\n')[0] ?? '').trim() : ''
      if (retired) return

      known = path.startsWith('/')
      if (!known) {
        lookAgainAt = (await $.clock.now()) + LOOK_AGAIN_MS
        return
      }

      socket = path
      if (!timer) run(every)
    }

    // A refused connect or an answer the daemon could not give yet leaves a report for the next tick. A report the
    // daemon will never accept is dropped. A refused link still keeps its dispatch outstanding: the row it names exists,
    // and letting the daemon's next confirm find nothing behind it would tell the sender a delivered message was refused.
    const flush = async () => {
      for (const report of [...entry.reports]) {
        const reply = await call(report.route, report.body, CALL_MS)
        if (!reply || reply.status >= 500) return
        if (reply.status !== 200) $.ui.log(`the daemon refused a ${report.route} report (${reply.status})`, { to: 'debug' })

        entry.reports = entry.reports.filter(r => r !== report)
        const settled = report.settles && (reply.status === 200 || report.route !== '/link')
        if (settled && entry.held?.dispatchId === report.dispatchId) delete entry.held
        await save()
      }
    }

    const attach = async () => {
      const sessionId = entry.sessionId
      const replacing = entry.replaces ? { replacesAgentId: entry.replaces } : {}
      const reply = await call('/attach', { sessionId, processKey: key, ...replacing }, ATTACH_MS)
      if (!reply || reply.status >= 500) return

      const answer = parse(reply.text)
      if (reply.status === 200 && typeof answer.agentId === 'string') {
        // Attached for a session this process has since left: the next attach names it as the one it replaces.
        if (entry.sessionId !== sessionId) entry.replaces = answer.agentId
        else {
          entry.agentId = answer.agentId
          delete entry.replaces
        }
        await save()

        const ms = answer.pollIntervalMs
        if (typeof ms === 'number' && ms >= 100 && ms <= 10_000 && ms !== every) run(ms)
      } else if (reply.status === 200 && answer.retry === true) {
        attachAfter = (await $.clock.now()) + SLOW_ATTACH_MS
      } else {
        inert = true
        $.ui.log(`not steered: ${String(answer.refused ?? reply.status)}`, { to: 'debug' })
      }
    }

    // A dispatch handed over while the session changed is still this process's: it lands in the new conversation.
    const poll = async (agentId: string) => {
      const reply = await call(
        '/next',
        { agentId, idle: !entry.turn.running && !entry.changing, outstanding: entry.held?.dispatchId, processKey: key },
        CALL_MS,
      )
      if (reply?.status !== 200) return

      const answer = parse(reply.text)
      if (typeof answer.dispatchId === 'string' && typeof answer.text === 'string') {
        // A retired load's write is dropped, so recording the handover here would lose it and submit it unrecorded.
        if (entry.held || retired) return

        entry.held = { dispatchId: answer.dispatchId, text: answer.text, agentId, phase: 'received' }
        await save()
      } else if (typeof answer.gone === 'string' && entry.agentId === agentId) {
        if (answer.retry === true) delete entry.agentId
        else inert = true
        await save()
      }
    }

    const undelivered = async (dispatchId: string, outcome: 'rejected' | 'unconfirmed', reason: string) => {
      const held = entry.held
      if (held?.dispatchId !== dispatchId || held.phase === 'linked' || held.phase === 'rejected') return

      held.phase = outcome
      entry.reports.push({
        route: '/undelivered',
        dispatchId,
        settles: outcome === 'rejected',
        body: { agentId: held.agentId, dispatchId, outcome, reason, processKey: key },
      })
      await save()
    }

    // The phase is saved before the call, so a reload never submits a dispatch twice. After a turn the person aborted,
    // the engine drops every plugin prompt until someone types at the terminal: that drop is reported as stopped.
    const submit = async () => {
      const held = entry.held
      if (held?.phase !== 'received') return

      held.phase = 'submitted'
      held.submittedAt = await $.clock.now()
      await save()
      if (retired) return

      const { dispatchId } = held
      $.ui.log(NOTICE)
      $.prompt
        .submit({ text: held.text, asUser: true })
        .then(
          answer =>
            answer.drop === undefined ? undefined : undelivered(dispatchId, 'rejected', entry.turn.aborted ? 'stopped' : 'dropped'),
          () => undelivered(dispatchId, 'rejected', 'refused'),
        )
        .catch(() => {})
    }

    const grace = async () => {
      const held = entry.held
      if (held?.phase !== 'submitted' || entry.turn.running) return

      if ((await $.clock.now()) - Math.max(held.submittedAt ?? 0, entry.turn.at) >= GRACE_MS)
        await undelivered(held.dispatchId, 'unconfirmed', 'no_row')
    }

    // What a process that has gone still owed is sent before its key goes, so a row it linked is not left unattributed.
    // A key owing nothing goes whatever the daemon is doing, which is every key while steering is off. A daemon that is
    // away or failing leaves the rest to a later tick.
    let unswept = stale
    let sweeping = false
    const sweep = async () => {
      if (sweeping) return
      sweeping = true
      try {
        for (const k of [...unswept]) {
          if (retired) return
          const left = await $.store.get(k)
          const owed = left && typeof left === 'object' ? ((left as Partial<Entry>).reports ?? []) : []

          for (const [i, report] of owed.entries()) {
            const reply = await call(report.route, report.body, CALL_MS)
            if (!reply || reply.status >= 500) {
              if (i > 0) await $.store.set(k, { reports: owed.slice(i) })
              return
            }
          }

          await $.store.delete(k)
          unswept = unswept.filter(u => u !== k)
        }
      } finally {
        sweeping = false
      }
    }

    const tick = async () => {
      if (ticking || retired) return
      ticking = true
      try {
        await flush()
        if (unswept.length > 0) await sweep()
        if (!known && (await $.clock.now()) >= lookAgainAt) await connect()
        if (!inert) {
          if (!entry.agentId && !entry.changing && known && (await $.clock.now()) >= attachAfter) await attach()
          if (entry.agentId) await poll(entry.agentId)
        }
        await submit()
        await grace()
      } catch (err) {
        $.ui.log(`steering tick failed: ${String(err)}`, { to: 'debug' })
      } finally {
        ticking = false
      }
    }

    // The server counts what the process holds Unconfirmed once the replacement attaches, so the grace has nothing to add.
    const change = (sessionId: string) => {
      if (entry.held?.phase === 'submitted') entry.held.phase = 'unconfirmed'
      if (entry.agentId) entry.replaces = entry.agentId
      delete entry.agentId
      delete entry.changing
      entry.sessionId = sessionId
      inert = false
      attachAfter = 0
    }

    const match = ({ uuid, text, sessionId }: Row) => {
      const held = entry.held
      if (!held || held.phase === 'linked' || held.phase === 'rejected' || held.text !== text) {
        $.ui.log(`unmatched ${uuid}: no outstanding dispatch has its text`, { to: 'debug' })
        return
      }

      held.phase = 'linked'
      entry.reports.push({
        route: '/link',
        dispatchId: held.dispatchId,
        settles: true,
        body: { agentId: held.agentId, dispatchId: held.dispatchId, sessionId, rowUuid: uuid, processKey: key },
      })
    }

    const sessionId = await $.session.id()

    if (early.turn) entry.turn = early.turn
    const current = early.sessionId ?? sessionId
    const rows = early.rows
    early = { rows: [] }
    if (entry.sessionId !== current) change(current)
    rows.forEach(match)

    live = {
      retire: () => {
        retired = true
        timer?.cancel()
      },

      turn: async turn => {
        entry.turn = turn
        await save()
      },

      // Never a detach: unregistering the agent it replaces would discard what that agent waits behind.
      session: async sessionId => {
        if (sessionId === entry.sessionId) {
          if (!entry.changing) return
          delete entry.changing
          await save()
          return
        }

        change(sessionId)
        await save()
        void connect()
      },

      // Only an exit ends the process, and `/logout` exits once it has signed out; `/clear` and resume go on under a new
      // session id that `classic.SessionStart` brings. The chain shares one short bound, so this keeps to half of what is
      // left of it.
      ended: async (sessionId, reason, budgetMs) => {
        if (sessionId !== entry.sessionId) return

        if (reason === 'clear' || reason === 'resume') {
          entry.changing = true
          if (reason === 'clear') entry.turn = { ...entry.turn, aborted: false }
          await save()
          return
        }
        if (reason !== 'prompt_input_exit' && reason !== 'logout' && reason !== 'other') return

        retired = true
        timer?.cancel()

        const until = (await $.clock.now()) + budgetMs / 2
        const left = async () => Math.min(CALL_MS, until - (await $.clock.now()))

        for (const report of [...entry.reports]) {
          const ms = await left()
          const reply = ms > 0 ? await call(report.route, report.body, ms) : undefined
          if (!reply || reply.status >= 500) break

          entry.reports = entry.reports.filter(r => r !== report)
        }

        const ms = await left()
        if (entry.agentId && socket && ms > 0) await call('/detach', { agentId: entry.agentId, processKey: key }, ms)

        await saving

        // The daemon takes a report whichever process sends it, so what is still owed outlives this process: the next
        // one finds this key stale and sends it. Nothing else of the key survives.
        if (entry.reports.length > 0) await $.store.set(storeKey, { reports: entry.reports })
        else await $.store.delete(storeKey)
      },

      row: async row => {
        match(row)
        await save()
      },
    }

    await save()

    void (async () => {
      await connect()
      await sweep()
    })()

    return started
  }).catch(($, e, next) => next(e))

  on('turn.start', async ($, e, next) => {
    if (e.agentId === undefined) await noteTurn({ running: true, aborted: false, at: await $.clock.now() })
    return next(e)
  }).catch(($, e, next) => next(e))

  on('turn.complete', async ($, e, next) => {
    if (e.agentId === undefined) await noteTurn({ running: false, aborted: e.reason === 'aborted', at: await $.clock.now() })
    return next(e)
  }).catch(($, e, next) => next(e))

  // `$.session.id()` here still answers the session that ended, so the new one is the event's.
  on('classic.SessionStart', async ($, e, next) => {
    await noteSession(e.session_id)
    return next(e)
  }).catch(($, e, next) => next(e))

  on('session.end', async ($, e, next) => {
    await live?.ended(e.sessionId, e.reason, next.budget.remainingMs)
    return next(e)
  }).catch(($, e, next) => next(e))

  // A hook beneath may refuse the row or store other words than this one was given: only the stored row is the dispatch's.
  on('session.append', { origin: { kind: 'plugin', name: NAME } }, async ($, e, next) => {
    const stored = await next(e)
    if (stored.deny === undefined && e.agentId === undefined && stored.message.type === 'user' && e.door !== 'queue')
      await noteRow({ uuid: stored.uuid, text: textOf(stored.message.content), sessionId: await $.session.id() })
    return stored
  }).catch(($, e, next) => next(e))
}

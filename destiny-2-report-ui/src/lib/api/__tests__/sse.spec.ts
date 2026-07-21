import { describe, expect, it } from 'vitest'
import { drainSseBuffer, isTerminalStatus } from '../sse'

describe('drainSseBuffer', () => {
  it('parses named events with JSON payloads', () => {
    const buffer = 'event: running\ndata: {"status":"running"}\n\n'
    const { events, rest } = drainSseBuffer(buffer)
    expect(events).toEqual([{ event: 'running', data: '{"status":"running"}' }])
    expect(rest).toBe('')
  })

  it('keeps a trailing partial event in the buffer', () => {
    const buffer = 'event: position\ndata: {"position":3}\n\nevent: running\ndata: {"stat'
    const { events, rest } = drainSseBuffer(buffer)
    expect(events).toHaveLength(1)
    expect(events[0]?.event).toBe('position')
    expect(rest).toBe('event: running\ndata: {"stat')
  })

  it('handles CRLF line endings', () => {
    const buffer = 'event: queued\r\ndata: {}\r\n\r\n'
    const { events } = drainSseBuffer(buffer)
    expect(events).toEqual([{ event: 'queued', data: '{}' }])
  })

  it('defaults the event name to message', () => {
    const { events } = drainSseBuffer('data: {"a":1}\n\n')
    expect(events[0]?.event).toBe('message')
  })

  it('joins multi-line data fields', () => {
    const { events } = drainSseBuffer('data: line1\ndata: line2\n\n')
    expect(events[0]?.data).toBe('line1\nline2')
  })

  it('ignores blocks without data', () => {
    const { events } = drainSseBuffer('event: ping\n\n')
    expect(events).toHaveLength(0)
  })
})

describe('isTerminalStatus', () => {
  it('recognizes the terminal crawl states', () => {
    expect(isTerminalStatus('completed')).toBe(true)
    expect(isTerminalStatus('failed')).toBe(true)
    expect(isTerminalStatus('private')).toBe(true)
    expect(isTerminalStatus('queued')).toBe(false)
    expect(isTerminalStatus('running')).toBe(false)
    expect(isTerminalStatus('not_found')).toBe(false)
  })
})

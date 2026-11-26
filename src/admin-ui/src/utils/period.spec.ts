import { describe, expect, it } from 'vitest'
import { periodRange } from './period'

// 10:00 in Stockholm on 25 November 2026.
const now = new Date('2026-11-25T09:00:00Z')

describe('periodRange', () => {
  it('covers the presets up to today, both ends included', () => {
    expect(periodRange('7d', '', '', now)).toEqual({ from: '2026-11-19', to: '2026-11-25' })
    expect(periodRange('30d', '', '', now)).toEqual({ from: '2026-10-27', to: '2026-11-25' })
    expect(periodRange('month', '', '', now)).toEqual({ from: '2026-11-01', to: '2026-11-25' })
    expect(periodRange('24h', '', '', now)).toEqual({ from: '2026-11-24', to: '2026-11-25' })
    expect(periodRange('90d', '', '', now)).toEqual({ from: '2026-08-28', to: '2026-11-25' })
  })

  it('reads a custom range forwards and fills an empty end', () => {
    expect(periodRange('custom', '2026-11-10', '2026-11-01', now)).toEqual({ from: '2026-11-01', to: '2026-11-10' })
    expect(periodRange('custom', '2026-11-10', '', now)).toEqual({ from: '2026-11-10', to: '2026-11-25' })
    expect(periodRange('custom', '', '2026-11-20', now)).toEqual({ from: '2026-10-27', to: '2026-11-20' })
  })

  it('reads an unknown value as the last 30 days', () => {
    expect(periodRange('year', '', '', now)).toEqual(periodRange('30d', '', '', now))
  })
})

import { describe, expect, it } from 'vitest'
import { requestsLink } from './requestsLink'

describe('requestsLink', () => {
  it('opens the requests tab with only the filters that are set, dates as a custom period', () => {
    expect(requestsLink({ from: '2026-10-24', to: '2026-10-25', model: '', outcome: 'errors' })).toEqual({
      name: 'usage',
      query: { tab: 'requests', period: 'custom', from: '2026-10-24', to: '2026-10-25', outcome: 'errors' },
    })
    expect(requestsLink({ keyId: 'k1' })).toEqual({ name: 'usage', query: { tab: 'requests', keyId: 'k1' } })
  })
})

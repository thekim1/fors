import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { AuditEntry } from '@/api/types'
import AuditView from './AuditView.vue'

const entries: AuditEntry[] = [
  {
    id: '2', timestamp: '2026-10-21T08:00:00Z', actor: 'f3a1-subject', action: 'export', entityType: 'Usage', entityId: 'usage.csv',
    details: { before: null, after: { rows: 12 } }, actorName: 'Anna Admin', actorRoles: 'gateway-admin', actorDepartments: 'IT',
    sourceAddress: '192.0.2.10', sessionId: 'a1b2c3', requestId: '4bf92f35', userAgent: 'Mozilla/5.0',
  },
  { id: '1', timestamp: '2026-10-20T08:00:00Z', actor: 'system:seed', action: 'seed', entityType: 'Database', entityId: null, details: 'Demo data created' },
]

const list = vi.fn()
vi.mock('@/api', () => ({ api: { audit: { list: (...a: unknown[]) => list(...a) } } }))
beforeEach(() => list.mockReset().mockImplementation(() => Promise.resolve({ items: entries, total: entries.length })))

describe('AuditView', () => {
  it('shows who made an entry, from where and in which session', async () => {
    setActivePinia(createPinia())
    const wrapper = mount(AuditView)
    await flushPromises()

    const rows = wrapper.findAll('tbody tr')
    expect(rows[0]!.text()).toContain('Anna Admin')
    expect(rows[0]!.text()).toContain('f3a1-subject')
    const context = rows[0]!.find('dl').text()
    for (const value of ['gateway-admin · IT', '192.0.2.10', 'a1b2c3', '4bf92f35', 'Mozilla/5.0']) expect(context).toContain(value)

    // Entries from before the upgrade have only the subject and details.
    expect(rows[1]!.text()).toContain('system:seed')
    expect(rows[1]!.find('dl').exists()).toBe(false)
  })

  it('filters by Type at once and by Who once the typing stops, both kept in the address (layout rule 2)', async () => {
    vi.useFakeTimers()
    try {
      setActivePinia(createPinia())
      const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/audit', component: { render: () => null } }] })
      await router.push('/audit?type=Team')
      const wrapper = mount(AuditView, { global: { plugins: [router] } })
      await flushPromises()
      expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ entityType: 'Team', actor: undefined }))

      await wrapper.get('[data-test="chip-Who"] input').setValue('anna')
      await flushPromises()
      expect(list).not.toHaveBeenLastCalledWith(expect.objectContaining({ actor: 'anna' }))
      await vi.advanceTimersByTimeAsync(300)
      expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ entityType: 'Team', actor: 'anna', page: 1 }))
      expect(router.currentRoute.value.query).toEqual({ type: 'Team', actor: 'anna' })

      await wrapper.get('[data-test=clear-filters]').trigger('click')
      await vi.advanceTimersByTimeAsync(300)
      expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ entityType: undefined, actor: undefined }))
      expect(router.currentRoute.value.query).toEqual({})
    } finally {
      vi.useRealTimers()
    }
  })
})

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { inlineOverlay } from '@/test/fixtures'
import { useAuthStore } from '@/stores/auth'
import YourAccessDrawer from './YourAccessDrawer.vue'

describe('YourAccessDrawer', () => {
  it('lists the roles, and each department with where it came from', () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = {
      isAuthenticated: true, name: 'Anna', email: null, roles: ['department-admin', 'viewer'], departmentCodes: ['1100', '2200'],
      departmentSources: [{ code: '1100', source: 'claim' }, { code: '2200', source: 'group' }], teamCodes: ['T7'], sessionExpiresAt: null,
    }
    auth.status = 'authenticated'
    const wrapper = mount(YourAccessDrawer, { props: { open: true }, global: { stubs: { UiDrawer: inlineOverlay } } })
    const text = wrapper.text()
    expect(text).toContain('Department admin')
    expect(text).toContain('Viewer')
    expect(text).toContain('The department claim of your sign-in')
    expect(text).toContain('A member group of the department')
    expect(text).toContain('T7')
  })
})

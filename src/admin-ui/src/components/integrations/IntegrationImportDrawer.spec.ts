import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ApiError } from '@/api/client'
import type { IntegrationImportResult } from '@/api/types'
import IntegrationImportDrawer from './IntegrationImportDrawer.vue'

const api = vi.hoisted(() => ({ presets: vi.fn(), previewImport: vi.fn(), import: vi.fn() }))
vi.mock('@/api', () => ({ api: { integrations: api } }))

const preset = { id: 'platform', title: 'Platform', description: 'A platform with two keys', manifest: '{ "schemaVersion": 1 }' }
const result = (state: 'Created' | 'Existing', secret: string | null = null): IntegrationImportResult => ({
  integrationId: null,
  integrationName: 'Platform',
  createdIntegration: state === 'Created',
  createdTeam: false,
  createdDepartment: false,
  defaultOwner: false,
  keys: [{ name: 'api', id: null, prefix: null, state, secret }],
  warnings: state === 'Existing' ? ["Credential key 'api' of 'Platform' differs from the manifest in integrationScopes"] : [],
})

function mountDrawer() {
  return mount(IntegrationImportDrawer, {
    props: { open: true },
    global: { stubs: { UiDrawer: { template: '<div><slot /><slot name="footer" /></div>' } } },
  })
}

const button = (wrapper: ReturnType<typeof mountDrawer>, label: string) => wrapper.findAll('button').find((b) => b.text().includes(label))!

describe('IntegrationImportDrawer', () => {
  beforeEach(() => {
    for (const fn of Object.values(api)) fn.mockReset()
    api.presets.mockResolvedValue([preset])
  })

  it('fills the manifest from a preset and creates only after a current preview', async () => {
    const wrapper = mountDrawer()
    await flushPromises()
    await wrapper.find('select').setValue('platform')
    expect((wrapper.find('textarea').element as HTMLTextAreaElement).value).toBe(preset.manifest)
    expect(button(wrapper, 'Create').attributes('aria-disabled')).toBe('true')

    api.previewImport.mockResolvedValue(result('Created'))
    await button(wrapper, 'Preview').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Will be created')
    expect(button(wrapper, 'Create').attributes('aria-disabled')).toBeUndefined()

    // Editing the manifest makes the preview out of date.
    await wrapper.find('textarea').setValue('{ "schemaVersion": 1, "x": 1 }')
    expect(wrapper.text()).toContain('out of date')
    expect(button(wrapper, 'Create').attributes('aria-disabled')).toBe('true')

    await wrapper.find('textarea').setValue(preset.manifest)
    api.import.mockResolvedValue(result('Created', 'ume-sk-secret'))
    await button(wrapper, 'Create').trigger('click')
    await flushPromises()
    expect(api.import).toHaveBeenCalledWith(preset.manifest)
    expect(wrapper.emitted('imported')?.[0]?.[0]).toMatchObject({ keys: [{ secret: 'ume-sk-secret' }] })
  })

  it('shows differences as warnings and does not offer to create when nothing is missing', async () => {
    const wrapper = mountDrawer()
    await flushPromises()
    await wrapper.find('textarea').setValue('{}')
    api.previewImport.mockResolvedValue(result('Existing'))
    await button(wrapper, 'Preview').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Exists, unchanged')
    expect(wrapper.text()).toContain('differs from the manifest')
    expect(button(wrapper, 'Create').attributes('aria-disabled')).toBe('true')
  })

  it('says when the manifest names no owner and where the integration is placed', async () => {
    const wrapper = mountDrawer()
    await flushPromises()
    await wrapper.find('textarea').setValue('{ "schemaVersion": 1, "integration": { "name": "Platform" } }')
    api.previewImport.mockResolvedValue({
      ...result('Created'),
      createdTeam: true,
      createdDepartment: true,
      defaultOwner: true,
      warnings: [
        "Integration 'Platform' names no owner in the manifest, so it is placed in the team 'Platform' (code 'platform') in the department 'Platform' (code 'platform'), both created for it. A gateway admin can move it to the organisation's own team under Integrations; its credential keys move with it.",
      ],
    })
    await button(wrapper, 'Preview').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('will be created in a new team named after it, as the manifest names no owner.')
    expect(wrapper.text()).toContain("A gateway admin can move it to the organisation's own team under Integrations")
    expect(button(wrapper, 'Create').attributes('aria-disabled')).toBeUndefined()
  })

  it('lists every problem the server finds in the manifest', async () => {
    const wrapper = mountDrawer()
    await flushPromises()
    await wrapper.find('textarea').setValue('{}')
    api.previewImport.mockRejectedValue(
      new ApiError(400, 'POST', { title: 'Check the highlighted fields.', errors: { manifest: ["'schemaVersion' must be 1.", "'integration' is required."] } }),
    )
    await button(wrapper, 'Preview').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain("'schemaVersion' must be 1.")
    expect(wrapper.text()).toContain("'integration' is required.")
  })
})

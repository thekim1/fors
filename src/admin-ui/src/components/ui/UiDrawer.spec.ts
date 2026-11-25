import { describe, expect, it, vi } from 'vitest'
import { h } from 'vue'
import { mount } from '@vue/test-utils'
import UiDrawer from './UiDrawer.vue'

HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true }
HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false }

function mountDrawer(unsaved: number) {
  return mount(UiDrawer, {
    props: { open: true, title: 'Edit team', unsaved },
    slots: { footer: ({ close }: { close: () => void }) => h('button', { 'data-test': 'cancel', onClick: close }, 'Cancel') },
    attachTo: document.body,
  })
}

describe('UiDrawer', () => {
  it('closes at once without unsaved changes', async () => {
    const confirm = vi.spyOn(window, 'confirm')
    const wrapper = mountDrawer(0)
    await wrapper.get('button[aria-label="Close"]').trigger('click')
    expect(confirm).not.toHaveBeenCalled()
    expect(wrapper.emitted('update:open')).toEqual([[false]])
  })

  it('asks before the × or the footer’s Cancel drops unsaved changes', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const wrapper = mountDrawer(2)
    await wrapper.get('button[aria-label="Close"]').trigger('click')
    expect(confirm).toHaveBeenCalledWith('Close without saving 2 unsaved changes?')
    expect(wrapper.emitted('update:open')).toBeUndefined()

    confirm.mockReturnValue(true)
    await wrapper.get('[data-test=cancel]').trigger('click')
    expect(wrapper.emitted('update:open')).toEqual([[false]])
  })

  it('asks on Esc too', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    const wrapper = mountDrawer(1)
    wrapper.get('dialog').element.dispatchEvent(new Event('cancel', { cancelable: true }))
    expect(confirm).toHaveBeenCalledWith('Close without saving 1 unsaved change?')
    expect(wrapper.emitted('update:open')).toBeUndefined()
  })
})

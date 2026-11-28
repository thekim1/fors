import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { routes as appRoutes } from '@/router'
import { DOMWrapper, flushPromises, type VueWrapper } from '@vue/test-utils'

/** The first button whose visible text contains `text` (icon buttons also carry the icon’s name as text). */
export function button(wrapper: VueWrapper, text: string) {
  const found = wrapper.findAll('button').find((b) => b.text().includes(text))
  if (!found) throw new Error(`No button containing "${text}". Buttons: ${wrapper.findAll('button').map((b) => b.text()).join(' | ')}`)
  return found
}

export function buttonByLabel(wrapper: VueWrapper, label: string) {
  const found = wrapper.find(`button[aria-label="${label}"]`)
  if (!found.exists()) throw new Error(`No button labelled "${label}"`)
  return found
}

/**
 * The open menu of a MenuButton (teleported to the body, out of the wrapper's reach): the last one opened, since a
 * spec that leaves its wrapper mounted can leave an earlier menu open.
 */
export function openMenu(): DOMWrapper<Element> {
  const el = [...document.body.querySelectorAll('[data-test=menu]')].at(-1)
  if (!el) throw new Error('No menu is open')
  return new DOMWrapper(el)
}

/** Opens the ⋯ menu whose name starts with `menuLabel` and chooses the item whose text contains `item`. */
export async function chooseMenuItem(wrapper: VueWrapper, menuLabel: string, item: string): Promise<void> {
  const menu = wrapper.find(`button[aria-haspopup="menu"][aria-label^="${menuLabel}"]`)
  if (!menu.exists()) throw new Error(`No ⋯ menu named "${menuLabel}…"`)
  await menu.trigger('click')
  await flushPromises()
  const items = openMenu().findAll('[role=menuitem]')
  const found = items.find((i) => i.text().includes(item))
  if (!found) throw new Error(`No menu item containing "${item}". Items: ${items.map((i) => i.text()).join(' | ')}`)
  await found.trigger('click')
  await flushPromises()
}

/**
 * A router with one page of the app at its real path (an optional detail parameter included, useListDetail), at
 * `params`; other paths render nothing. For views that read or change their address.
 */
export async function pageRouter(name: string, params: Record<string, string> = {}, query: Record<string, string> = {}): Promise<Router> {
  const record = appRoutes.find((r) => r.name === name)
  if (!record) throw new Error(`no route named ${name}`)
  const blank = { template: '<div />' }
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: record.path, name, component: blank }, { path: '/:rest(.*)*', component: blank }] })
  await router.push({ name, params, query })
  await router.isReady()
  return router
}

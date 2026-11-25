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

import { computed, nextTick, watch, type ComputedRef, type Ref } from 'vue'
import { useRoute, useRouter, type RouteLocationRaw } from 'vue-router'
import { focusPageHeading } from '@/router'
import { useMedia } from './useMedia'

/** Below this width a list-and-detail page stacks (its grid is `max-lg:grid-cols-1`), so it shows one or the other. */
export const LIST_DETAIL_NARROW = '(max-width: 1023px)'

export interface ListDetailOptions<T> {
  /** The route parameter that holds the chosen item's key (an optional parameter of the page's route). */
  param: string
  /** Every item an address may name. */
  items: () => readonly T[]
  /** The key in the address: stable and readable (a route's name, an id). */
  keyOf: (item: T) => string
  /** The item a wide screen shows when the address names none: the list's first, as before. */
  first: () => T | undefined
  /** True once the items have loaded: an address that names no item is dropped only then. */
  ready: () => boolean
  /** Focuses the detail's heading once a chosen item is shown. */
  focusDetail: () => void
}

export interface ListDetail<T> {
  /** The item shown: the address's, else on a wide screen the list's first; null on a phone's list. */
  selected: ComputedRef<T | null>
  /** The selected item's key (for the list's highlight). */
  selectedKey: ComputedRef<string | null>
  /** A phone, or any width at which the page stacks: the list or the detail, never both. */
  narrow: Ref<boolean>
  /** Whether the list is shown: always on a wide screen, on a phone only while the address names no item. */
  showList: ComputedRef<boolean>
  /** Whether the detail is shown as a page of its own (a phone with an item in the address): breadcrumb, `h1`. */
  detailPage: ComputedRef<boolean>
  /** Chooses an item: its address (pushed on a phone, so Back returns to the list; replaced on a wide screen). */
  select: (key: string) => void
  /** The address of the list, keeping the query (filters). */
  listTo: ComputedRef<RouteLocationRaw>
}

/**
 * A list-and-detail page (layout audit Q1 A; docs/admin-ui.md, layout rule 10): the chosen item is in the address
 * (`/routes/fors-chat`), so a detail can be linked. A wide screen shows the list and the detail side by side, as before,
 * and the address follows the selection; at the width where the page stacks, the address decides: the list alone, or the
 * item's detail alone with a breadcrumb back to the list. An address that names no item (deleted, mistyped) is replaced by
 * the list's.
 */
export function useListDetail<T>(options: ListDetailOptions<T>): ListDetail<T> {
  const route = useRoute()
  const router = useRouter()
  const narrow = useMedia(LIST_DETAIL_NARROW)

  const asked = computed(() => {
    const value = route.params[options.param]
    return typeof value === 'string' && value.length > 0 ? value : null
  })
  const named = computed(() => (asked.value === null ? undefined : options.items().find((i) => options.keyOf(i) === asked.value)))
  const selected = computed<T | null>(() => named.value ?? (narrow.value ? null : (options.first() ?? null)))
  const selectedKey = computed(() => (selected.value ? options.keyOf(selected.value) : null))
  const detailPage = computed(() => narrow.value && !!named.value)
  const showList = computed(() => !detailPage.value)
  const listTo = computed<RouteLocationRaw>(() => ({ name: String(route.name), query: { ...route.query } }))

  function select(key: string): void {
    const to = { name: String(route.name), params: { ...route.params, [options.param]: key }, query: route.query }
    void (narrow.value ? router.push(to) : router.replace(to))
  }

  // A key that names nothing once the items are in: back to the list's address (a wide screen then shows the first).
  watch(
    [asked, () => options.ready(), () => options.items()],
    () => {
      if (asked.value !== null && options.ready() && !named.value) {
        const params = { ...route.params }
        delete params[options.param]
        void router.replace({ name: String(route.name), params, query: route.query })
      }
    },
    { immediate: true },
  )

  // A new item in the address: its heading takes focus; back to the list on a phone: the page's heading does.
  watch(asked, (key, before) => {
    if (key !== null && named.value) void nextTick(() => options.focusDetail())
    else if (key === null && before !== null && narrow.value) focusPageHeading()
  })

  return { selected, selectedKey, narrow, showList, detailPage, select, listTo }
}

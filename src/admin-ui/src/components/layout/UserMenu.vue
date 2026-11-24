<script setup lang="ts">
import { computed, ref } from 'vue'
import type { Role } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { isTheme, useUiStore, type Theme } from '@/stores/ui'
import { ROLE } from '@/utils/labels'
import { initials } from '@/utils/format'
import AppIcon from '@/components/ui/AppIcon.vue'
import MenuButton, { type MenuItem } from '@/components/ui/MenuButton.vue'
import AccessDialog from './AccessDialog.vue'

/**
 * The user card at the bottom of the side menu (and of the phone's menu) as a menu button (thekim1/fors#127): the
 * theme, *Your access* and *Sign out*, everything that is about the person in one place. On a phone the menu is a
 * bottom sheet (MenuButton).
 */
const props = withDefaults(defineProps<{ signingOut?: boolean; place?: 'sidebar' | 'sheet' }>(), { signingOut: false, place: 'sidebar' })
const emit = defineEmits<{ (e: 'sign-out'): void }>()

const auth = useAuthStore()
const ui = useUiStore()
const accessOpen = ref(false)

const THEME_LABELS: Record<Theme, string> = { light: 'Light', dark: 'Dark', lumen: 'Lumen' }

const name = computed(() => auth.user?.name ?? auth.user?.email ?? '')
const role = computed(() => {
  const first = auth.roles[0]
  return first ? (ROLE[first as Role]?.label ?? first) : 'No role'
})

const items = computed<MenuItem[]>(() => [
  ...(Object.keys(THEME_LABELS) as Theme[]).map((t) => ({ key: t, label: THEME_LABELS[t], group: 'Theme', checked: ui.theme === t })),
  { key: 'access', label: 'Your access', icon: 'badge' },
  { key: 'sign-out', label: 'Sign out', icon: 'logout', disabled: props.signingOut },
])

function onSelect(key: string): void {
  if (isTheme(key)) ui.setTheme(key)
  else if (key === 'access') accessOpen.value = true
  else if (key === 'sign-out') emit('sign-out')
}
</script>

<template>
  <MenuButton
    label="Your account"
    :items="items"
    align="start"
    :button-class="
      place === 'sidebar'
        ? 'flex w-full items-center gap-2.5 rounded-[12px] bg-surface p-2 text-left shadow-1 hover:bg-surface-2 max-lg:justify-center max-lg:px-1'
        : 'flex w-full items-center gap-3 rounded-[12px] bg-surface p-2.5 text-left shadow-1 hover:bg-surface-2'
    "
    class="w-full"
    data-test="user-menu"
    @select="onSelect"
  >
    <span
      class="flex shrink-0 items-center justify-center rounded-full bg-accent-soft text-caption font-semibold text-accent-ink"
      :class="place === 'sidebar' ? 'size-8' : 'size-9'"
      aria-hidden="true"
    >{{ initials(name || '?') }}</span>
    <span class="min-w-0 flex-1" :class="place === 'sidebar' ? 'leading-4 max-lg:sr-only' : 'leading-5'">
      <span class="block truncate font-medium text-fg" :class="place === 'sidebar' ? 'text-[13px]' : 'text-body'" lang="sv">{{ name }}</span>
      <span class="block truncate text-caption text-fg-3">{{ role }}<span class="sr-only">, account menu</span></span>
    </span>
    <AppIcon name="unfold_more" class="shrink-0 text-fg-3" :class="{ 'max-lg:hidden': place === 'sidebar' }" />
  </MenuButton>
  <AccessDialog v-model:open="accessOpen" />
</template>

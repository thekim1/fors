/// <reference types="node" />
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import { routes } from './index'

// Read from disk: Vite serves nothing outside the admin UI, and the browser tests are a package of their own. Vitest
// runs in src/admin-ui, two levels below the repository.
const helpers = readFileSync(resolve(process.cwd(), '../../tests/e2e/helpers.ts'), 'utf8')

/**
 * The browser tests' accessibility spec scans `pages` from tests/e2e/helpers.ts (axe in three themes at 1 280 and 390
 * px, reflow at 320 px). A page added to the router without being added there would never be scanned, so this fails
 * until it is. Setup is public and has its own browser spec.
 */
function scannedPages(): string[] {
  const list = /export const pages = \[([^\]]*)\]/.exec(helpers)?.[1]
  if (!list) throw new Error('No `export const pages = [...]` in tests/e2e/helpers.ts')
  return [...list.matchAll(/'([^']+)'/g)].map((m) => m[1]!)
}

describe('the pages the accessibility spec scans', () => {
  it('include every page of the router, a 403 and a 404 page', () => {
    const scanned = new Set(scannedPages().map((p) => p.split('?')[0]))
    const routed = routes
      .filter((r) => !r.redirect && !r.meta?.public && !r.path.includes(':'))
      .map((r) => r.path)
    expect(routed.filter((path) => !scanned.has(path))).toEqual([])
    expect(scanned.has('/forbidden')).toBe(true)
    expect(routed.includes('/no-such-page')).toBe(false)
    expect(scanned.has('/no-such-page')).toBe(true)
  })

  it('include the tabs and views that show other content', () => {
    const scanned = scannedPages()
    for (const page of ['/usage?tab=requests', '/providers?show=models']) expect(scanned).toContain(page)
  })
})

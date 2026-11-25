import type { LocationQueryRaw, RouteLocationRaw } from 'vue-router'
import type { RequestOutcome } from '@/api/types'

/** `errors` is every request that did not succeed; an outcome narrows it to one kind. */
export type RequestsOutcomeFilter = RequestOutcome | 'errors'

/** Filters for the request list on Usage › Requests and costs, as other pages link to it. Dates are `yyyy-mm-dd`. */
export interface RequestsLink {
  from?: string
  to?: string
  departmentId?: string
  teamId?: string
  keyId?: string
  model?: string
  provider?: string
  outcome?: RequestsOutcomeFilter
}

export const REQUESTS_LINK_KEYS = ['from', 'to', 'departmentId', 'teamId', 'keyId', 'model', 'provider', 'outcome'] as const

/**
 * A link to the request list with these filters, e.g. the failed requests of the last day. The page keeps its filters
 * in the address under the same names (layout rule 2), so the link is simply that address; dates make a custom period.
 */
export function requestsLink(filter: RequestsLink): RouteLocationRaw {
  const query: LocationQueryRaw = { tab: 'requests' }
  if (filter.from || filter.to) query.period = 'custom'
  for (const key of REQUESTS_LINK_KEYS) if (filter[key]) query[key] = filter[key]
  return { name: 'usage', query }
}

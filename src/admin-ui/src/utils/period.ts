import { lastDaysIso, todayIso } from './format'

/** The period chip's presets (layout rule 2): PeriodPicker offers them, `?period=` holds one. */
export const PERIODS = ['24h', '7d', '30d', '90d', 'month', 'custom'] as const
export type Period = (typeof PERIODS)[number]

/**
 * The days a period covers, as `yyyy-mm-dd` (Stockholm), both included: the last 7, 30 or 90 days up to today (the last
 * 24 hours: yesterday and today), this month
 * up to today, or the custom range. A custom range written backwards is read forwards; an empty end is open and reads
 * as today (`to`) or the start of the last 30 days (`from`). An unknown value reads as the last 30 days.
 */
export function periodRange(period: string, from = '', to = '', now: Date = new Date()): { from: string; to: string } {
  const today = todayIso(now)
  const last = (days: number) => ({ from: lastDaysIso(days, now)[0]!, to: today })
  if (period === '24h') return last(2)
  if (period === '7d') return last(7)
  if (period === '90d') return last(90)
  if (period === 'month') return { from: `${today.slice(0, 8)}01`, to: today }
  if (period === 'custom') {
    const start = from || last(30).from
    const end = to || today
    return start > end ? { from: end, to: start } : { from: start, to: end }
  }
  return last(30)
}

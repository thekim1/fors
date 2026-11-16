import { describe, expect, it } from 'vitest'
import { SETUP_COMPLETE_REASON, SETUP_COMPLETE_REASONS, SETUP_TEST_REASON, SETUP_TEST_REASONS, setupCompleteReasonText, setupTestReasonText } from './labels'

// The reason lists mirror the admin API: SetupTestReasons (Setup/SetupProvider.cs) and SetupService.CompleteAsync (Setup/SetupService.cs).
describe('setup reasons', () => {
  it('has a text of its own for every test reason the admin API gives', () => {
    expect(Object.keys(SETUP_TEST_REASON).sort()).toEqual([...SETUP_TEST_REASONS].sort())
    for (const reason of SETUP_TEST_REASONS) {
      expect(setupTestReasonText(reason)).toBe(SETUP_TEST_REASON[reason])
      expect(SETUP_TEST_REASON[reason].length).toBeGreaterThan(20)
    }
  })

  it('has a text of its own for every reason saving is refused with', () => {
    expect(Object.keys(SETUP_COMPLETE_REASON).sort()).toEqual([...SETUP_COMPLETE_REASONS].sort())
    for (const reason of SETUP_COMPLETE_REASONS) expect(setupCompleteReasonText(reason)).toBe(SETUP_COMPLETE_REASON[reason])
  })

  it('names a reason it does not know', () => {
    expect(setupTestReasonText('new_reason')).toContain('(new_reason)')
    expect(setupCompleteReasonText('new_reason')).toContain('(new_reason)')
    expect(setupTestReasonText(null)).toContain('no reason given')
  })
})

# Admin UI layout audit (proposal)

**Status:** accepted, 2026-11-26: the work goes ahead with the recommendation of each [question](#questions) (the user:
"continue"); a step that turns on one of them says so in its pull request, so it can still be changed there. The work
is in [Order](#order), smallest first, one pull request per step; done so far: step 0 (#134), the accessibility
checks of #130 part D (#135) step 1, with [Q7](#q7-status-badges-for-the-normal-state) A, step 2, and step 3, with [Q2](#q2-segmented-filters-with-counts) A
for Keys and Budgets, [Q3](#q3-one-period-picker) A and [Q5](#q5-audit-log-filters) A, step 4, step 5 and step 6 (with [Q4](#q4-pii-incidents-summary) A).

Every page in `src/admin-ui/src/views/` was checked against the eleven [layout rules](admin-ui.md#layout-rules) (Källa's
admin design §5.12, from its UX pass of 2026-11-23, and the mockups in Källa's
`plans/proposals/admin-ui-ux-pass/mockups/`). The pages were seen on the browser-test stack (`scripts/Start-E2E.ps1`:
demo data, no usage yet) as a gateway admin, at 1 440 and 390 px, in the light, dark and Lumen themes, and their code
was read. #131 and #129 already brought the shared parts and the first pages:

- the ⋯ menu (`MenuButton`) on a routing rule, a department and its teams, a route, an integration and a Data API key;
- the save bar on three Settings cards, and the unsaved-changes guard on Settings and every form drawer;
- filter chips kept in the address on *Usage › Requests and costs* and *PII incidents*;
- summary tiles on the Overview, the breadcrumb on *Provider stability*, and the user menu.

The themes did not change any finding: every page uses tokens only, and the accessibility spec's axe scans pass in all
three at both widths. Colour findings (rule 8) are the same in each theme.

## Across pages

What breaks the rules on more than one page.

| Rule | What breaks it | Pages | Fix |
|---|---|---|---|
| 1 One primary action | Destructive or rare actions are red buttons in drawer footers and in table rows | Health & operations (*Open circuit* on every provider row), the budget, provider and model drawers (*Delete*), the key drawer (*Revoke*) | A row ⋯ for *Open circuit*; *Delete…* and *Revoke…* on a ⋯ in the drawer's footer, last and red, behind their confirmations (*Drain provider* stays: it has a section of its own in the provider drawer, with its explanation) |
| 2 Filters as chips | Lists filter with labelled selects or segmented controls that are not in the address | Usage › Dashboard, Keys, Providers & models (both views), Budgets & alerts, Routing rules, Audit log, Provider stability | `FilterChip`/`FilterTextChip`/`PeriodPicker` with `useQueryFilters`; the search box keeps its `aria-label` and goes into the address as `q` |
| 3 Field widths | Number fields span the drawer (about 470 px) for 3 to 6 digits | Key drawer (requests and tokens per minute, requests at once), provider drawer (timeout), team drawer (monthly budget), price fields (`PriceFields`, `DiscoverModelsDialog`), request policy (max output tokens), budget drawer (limit, alert levels) | `TextField short`; the unit stays in the label |
| 5 Say it once | Text repeated on every row, or said twice | Health & operations (*OK* under every healthy component; *Open circuit* on every row), Catalogue (the same capability list on every alias), Provider stability (*Back to operations* beside the breadcrumb) | Into the summary, the row ⋯, or gone |
| 8 Colour for attention | Green badges for the normal state; zeros as badges or as bold figures | *Active* on every key, team and integration; *Enabled* on every provider and model; *Healthy* on every component; *0* in a green badge (usage writer failures); nine big **0** tiles on PII incidents; *Never used* in body colour | A badge only for the state that needs attention (*Disabled*, *Revoked*, *Down*); zeros, "never" and "none" in `text-fg-3` (Källa's `quietZero()`) |
| 9 One rhythm | Settings cards are 680, 768 or 576 px wide; status pages start with lists, not tiles | Settings, Health & operations, PII incidents | One width for every Settings card; up to four `SummaryTile`s on top of Health & operations and PII incidents |
| 10 Phone | The primary action keeps its own width on its own row and secondary actions sit beside it; segmented controls wrap to a second row; tiles are one per row; list and detail pages stack the whole list above the detail | Every page with a header action; Keys, Routing rules, Usage › Dashboard (segmented); Overview, PII incidents (tiles); Organisation, Routes, Routing rules (list and detail) | The primary action across the width beside a ⋯ that holds the secondary actions (`MenuButton` `phoneOnly`); filters as chips; tiles two by two; see [Q1](#q1-list-and-detail-pages) |

One thing that is not layout, found on the way: **sections showed nothing while loading.** Usage › Dashboard, the
Usage summary and request list, the Overview, Budgets & alerts and every Settings card passed `empty="false"` to
`AsyncState`, which then never says *Loading…*: until the data arrived they were an empty space (or "No open alerts"),
which in the screenshots (taken 400 ms after navigation) looked like empty pages. Fixed in #134 (step 0).

## Page by page

**Overview.** Rule 10: the four tiles are one per row at 390 px; two by two, as Källa's mockups. Rule 8: with no data,
every tile shows "—"; fine, they are grey (`quiet`). The *Providers* card explains its columns in a paragraph under
the table on every load (rule 5): behind a *Details* disclosure or the column headers' titles. No other finding.

**Usage › Dashboard.** Rule 2: a segmented period (24 hours, 7 days, 30 days, 90 days, Custom with two date fields and
*Apply*) and seven labelled selects, not in the address; the Requests tab beside it already has chips. Fix: the same
chips as the Requests tab, kept in the address under the same names, so switching tabs keeps the filters
([Q3](#q3-one-period-picker)). Rule 10: the segmented period wraps at 390 px.

**Usage › Requests and costs.** Done in #131. Left: the *Group by* select is full size with its label above, at the
summary's heading (rule 3: a compact select, label beside it), and *Look up a request* sits at the end of the page
under two tables; it is a different job (rule 4), better as a small form at the top right of the Requests list or in
the header's ⋯.

**PII incidents.** Filters done in #131. Rule 8 and 9: nine tiles (five categories, four actions) that are all big
**0**s on a quiet installation, one per row on a phone (nine screens of zeros). Fix: one row of up to four
`SummaryTile`s (requests with PII, the most found category, blocked, redacted), the per-category and per-action counts as
one compact table under them, zeros grey ([Q4](#q4-pii-incidents-summary)).

**Health & operations.** Rule 1 and 5: *Open circuit* in red on every provider row; on a row ⋯. Rule 8: *Healthy*
green badge and *OK* under each of five components; *0* consecutive failures in a green badge. Rule 9 and 4: the page
answers "is it working?" with four lists. Fix: four `SummaryTile`s on top (components, usage writer, providers
degraded or down, version), each with attention in text when something needs it; the component list folds under
*Details* while all are healthy, and shows only the ones that are not otherwise. *Refresh* stays the one header button.

**Provider stability.** Breadcrumb done (#131). Rule 5: *Back to operations* beside the period repeats the breadcrumb;
remove it. Rule 2: the period is a segmented control, not in the address; a `PeriodPicker` chip (`?period=7d`), with
the presets this page needs ([Q3](#q3-one-period-picker)). The long explanation above the charts goes behind a
*How it is measured* disclosure (rule 2: data first).

**Keys.** Rule 2: search and a segmented status filter with counts, neither in the address; a search field plus a
*Status* chip (`?q=&status=revoked`), counts kept in the chip's options ([Q2](#q2-segmented-filters-with-counts)).
Rule 8: *Active* badge on every key (only *Grace period*, *Expired* and *Revoked* get one); *Never used* grey. Rule 10:
the status segmented control wraps at 390 px. Key drawer: *Revoke* in red in the footer (rule 1) and the number fields
(rule 3), see *Across pages*.

**Organisation.** ⋯ menus done (#131). Rule 8: *Active* badge on every team; a team's *0* keys in body colour. Rule 3:
team drawer's monthly budget. Rule 10: at 390 px the department list is above the detail, which starts a screen down
([Q1](#q1-list-and-detail-pages)). The team row's *Keys* is a text button with no visible affordance next to the ⋯;
make it a link ("2 keys") in the *Keys* column instead (rule 5).

**Integrations.** ⋯ done (#131). Two header actions (*Add from manifest* and *New integration*) are right (one
primary); on a phone *Add from manifest* moves into the ⋯ (rule 10). The info paragraph above the list repeats
docs/integrations.md (rule 5): one line with a link. Rule 8: *Active* badge per row.

**Data API access.** ⋯ done (#131). Rule 8: *Never used* grey. The info line mixes the Data API address, a link and a
sentence about OAuth; the address belongs in a small card like the Catalogue's *Gateway base URL* (rule 4: "how do I
connect?").

**Catalogue.** Rule 5: every alias repeats the full capability list of its provider (four lines per row); show the
capabilities as short chips, or only where an alias differs from the rest. Rule 2 and 10: 11 aliases and 15 models
without search; a search field (`?q=`) and a *Kind* chip. At 390 px the page is about 5 700 px tall.

**Providers & models.** Rule 6: *Providers* / *Models* is a segmented control kept as `?show=models`; it is a pair of
tabs (`UiTabs` with `useTabQuery`, `?tab=models`). Rule 2: a search and three (providers) or five (models) labelled
selects, wrapping to a second row at 1 440 px; chips in the address. Rule 8: *Enabled* on every provider and model (a
badge only for *Disabled* and *Drained*); *Unknown* capabilities in grey on every model (rule 5: the column could go
when nothing is known). Drawers: *Delete* red in the footer, *Drain provider* beside *Save* (rule 1), timeout and price
fields (rule 3).

**Routes.** ⋯ done (#131). Rule 10: list and detail stack at 390 px ([Q1](#q1-list-and-detail-pages)). No other finding.

**Routing rules.** ⋯ done (#131). Rule 2: the scope filter is a segmented control with counts, not in the address
([Q2](#q2-segmented-filters-with-counts)); it wraps to two rows at 390 px. Rule 10: list and detail stack
([Q1](#q1-list-and-detail-pages)). The detail's *New rule from template*, *Edit* and *Enable* are three outlined
buttons; fine (none is the page's primary).

**Budgets & alerts.** Rule 2: the scope filter is a segmented control with counts beside the *Budgets* heading, not in
the address ([Q2](#q2-segmented-filters-with-counts)). Rule 8: every meter and figure is 0 % and 0,00 US$ in body
colour; grey while nothing is spent. Budget drawer: *Delete* red in the footer (rule 1), limit and alert levels (rule 3).

**Audit log.** Rule 2: one labelled select (*Entity type*) above the list, not in the address; Källa's audit log has
*Who* and *Type of object* as chips and the rest under *More filters*. Fix: a *Type* chip and a *Who* text chip
(`FilterTextChip`, debounced), in the address. `GET /api/audit` filters by `entityType` only, so *Who* needs an actor
filter in the admin API ([Q5](#q5-audit-log-filters)).

**Settings.** Save bar done on three cards (#131). Rule 9: cards are 680, 768 or 576 px wide; one width (680 px).
Rule 3: *Proxy addresses* is a text area with one address per line; Källa uses a chip input for lists of hosts. Rule
4 and 10: seven cards on one page, about 2 300 px at 1 440 and longer on a phone ([Q6](#q6-settings-structure)).

**No access, Page not found, Setup.** No findings.

## Questions

### Q1: List and detail pages

Organisation, Routes and Routing rules show a list on the left and the chosen item's detail on the right. At 390 px
the whole list comes first and the detail starts one or more screens down, and none of them has an address of its own
(no breadcrumb is possible).

- **A.** Keep the side-by-side layout on wide screens; on a phone, choosing an item shows only its detail with a
  breadcrumb back to the list (`/routes/fors-chat-advanced`, one route per item). The detail gets a linkable address
  everywhere.
- **B.** A as phone behaviour only, without addresses (the list hides while an item is shown).
- **C.** Leave it.

Recommendation: **A**. The addresses also make the detail linkable from Usage and the audit log.

### Q2: Segmented filters with counts

Keys (status), Budgets (scope) and Routing rules (scope) filter with a segmented control that shows a count per value
("Revoked 0"). Rule 2 makes filters chips; a chip shows the counts only in its open list.

- **A.** Chips in the address; the counts in the options ("Revoked (0)").
- **B.** Keep the segmented controls (they read as a small summary), in the address, scrolling in one row on a phone.

Recommendation: **A** for Keys and Budgets (filters like any other); **B** for Routing rules, where the scope is also
the list's grouping.

### Q3: One period picker

`PeriodPicker` (shared with Källa) has *Last 7 days*, *Last 30 days* (default), *This month* and *Custom…*. The Usage
dashboard offers 24 hours, 7, 30 and 90 days and Custom; Provider stability 24 hours, 7 and 30 days.

- **A.** Give `PeriodPicker` a `presets` prop (default as now), and use 24 hours / 7 / 30 / 90 days on the dashboard
  and 24 hours / 7 / 30 days on Provider stability. Offered to Källa.
- **B.** Use the shared presets everywhere (the dashboard loses 24 hours and 90 days).

Recommendation: **A**.

### Q4: PII incidents summary

- **A.** Up to four tiles (requests with PII, most found category, blocked, redacted) and one compact table of every
  category and action, zeros grey.
- **B.** Keep a tile per category and action, two by two on a phone, zeros grey and small.

Recommendation: **A**.

### Q5: Audit log filters

Källa's audit log filters by *Who*, *Type of object*, *Service* and object. Fors filters by entity type only.

- **A.** *Type* and *Who* chips now. *Who* needs an actor filter on `GET /api/audit`, which today takes `entityType`
  only: a small admin API change (and `docs/admin-api.md`). The rest later.
- **B.** Only *Type* as a chip, in the address.

Recommendation: **A**.

### Q6: Settings structure

Seven cards on one long page: Trusted proxies, Data retention, Model prices, Request logging, Branding, Sign-in,
Version.

- **A.** Group them under headings by the question people ask (rule 4): *Network* (Trusted proxies), *Data* (Retention,
  Request logging), *Prices* (Model prices), *This console* (Branding, Sign-in, Version), with a small section menu at
  the top (a *Section* picker on a phone, as Källa).
- **B.** Split into tabs (`?tab=`), at most six.
- **C.** One width, nothing else.

Recommendation: **A**.

### Q7: Status badges for the normal state

Rule 8 gives a badge only to the state that needs attention. Today *Active*, *Enabled* and *Healthy* are green badges
on every row, so the one *Disabled* or *Revoked* row is harder to find.

- **A.** Badges only for the other states; the normal state is plain text or nothing.
- **B.** Keep green badges for the normal state.

Recommendation: **A**, as Källa did (*Enabled* "Yes" shown only when it is *Off*).

## Order

Smallest first; each a pull request of its own, with its unit tests, the browser specs it touches run locally
(`scripts/Start-E2E.ps1`), and the docs and CHANGELOG.

0. **Loading states** (done in #134): sections say *Loading…* instead of showing nothing, and the accessibility spec
   scans them with their content.
1. **Quiet zeros and badges** (rule 8, [Q7](#q7-status-badges-for-the-normal-state)), **field widths** (rule 3, every
   drawer and dialog), **one Settings card width** (rule 9), and the duplicate *Back to operations* (rule 5). About a
   day.
2. **Row and drawer actions** (rule 1): *Open circuit* on a row ⋯; *Delete…*, *Revoke…*, *Drain…* on a ⋯ in the drawer
   footers.
3. **Chips in the address, simple pages** (rule 2): Audit log ([Q5](#q5-audit-log-filters)), Keys and Budgets
   ([Q2](#q2-segmented-filters-with-counts)), Provider stability and a `presets` prop on `PeriodPicker`
   ([Q3](#q3-one-period-picker)).
4. **Providers & models** (rules 2, 6): tabs in `?tab=`, both filter sets as chips in the address.
5. **Usage › Dashboard** (rule 2): the Requests tab's chips, one set of filters for both tabs.
6. **Summary tiles** (rule 9): Health & operations, PII incidents ([Q4](#q4-pii-incidents-summary)); Overview two by
   two on a phone.
7. **Phone headers** (rule 10): primary action across the width with a ⋯ for the secondary actions; segmented
   controls in one scrolling row.
8. **Settings structure** ([Q6](#q6-settings-structure)) and the proxy chip input.
9. **List and detail pages** ([Q1](#q1-list-and-detail-pages)): detail addresses and breadcrumbs.
10. **Smaller page items:** Catalogue search and capability chips, Data API address card, Integrations info line,
    Usage's *Group by* and *Look up a request*.

Each step that changes a shared component (`PeriodPicker` presets, `MenuButton` in drawers) is offered to Källa, as
[Shared with Källa](admin-ui.md#shared-with-källa) says.

## Not covered

- Roles other than gateway admin: department admins, viewers and team admins see fewer pages and actions; the same
  rules apply, but each step should be checked as one of them too (the browser specs sign in as several roles).
- Pages with real usage: the test stack had no requests, so tables with many rows, long names and charts with data
  were not seen. Check steps 4 to 6 on a stack with traffic (the demo scenario in `tests/e2e/specs/demo.spec.ts`).

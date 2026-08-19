/**
 * Indonesian number formatting — "." for thousands, "," for decimals: 1.234.567,89
 *
 * Everything the user sees goes through here so the app never mixes conventions.
 * Note this is display only: values are stored and sent to the API unformatted,
 * so parsing, sorting and exports are unaffected.
 */
const LOCALE = 'id-ID'

/** Counts and whole numbers: 1.353 */
export function fmtInt(v: number | string | null | undefined): string {
  if (v == null || v === '') return ''
  const n = Number(v)
  return Number.isNaN(n) ? String(v) : n.toLocaleString(LOCALE, { maximumFractionDigits: 0 })
}

/**
 * Quantities, money and rates: 8.201,657
 *
 * Allows up to 10 decimals so a stored value is never silently rounded on screen —
 * a BC quantity of 8201,657 must not display as 8.202 — while trailing zeros are
 * dropped so whole numbers stay clean.
 */
export function fmtNum(v: number | string | null | undefined): string {
  if (v == null || v === '') return ''
  const n = Number(v)
  return Number.isNaN(n) ? String(v) : n.toLocaleString(LOCALE, { maximumFractionDigits: 10 })
}

/**
 * Date: 24/07/2026
 *
 * An ISO date string is reformatted textually rather than through `new Date()`,
 * because a date-only string is parsed as UTC and would shift by a day for any
 * viewer west of Greenwich.
 */
export function fmtDate(v: string | Date | null | undefined): string {
  if (v == null || v === '') return ''
  if (typeof v === 'string') {
    const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(v)
    if (m) return `${m[3]}/${m[2]}/${m[1]}`
  }
  const d = v instanceof Date ? v : new Date(v)
  return Number.isNaN(d.getTime())
    ? String(v)
    : d.toLocaleDateString(LOCALE, { day: '2-digit', month: '2-digit', year: 'numeric' })
}

/**
 * Date and time: 24/07/2026 09:46 (or 09:46:24 with seconds).
 *
 * The time uses a colon, not the period that CLDR gives Indonesian ("09.46") —
 * beside dot-grouped numbers that reads like a decimal, and every Indonesian
 * business system we integrate with writes the time with a colon.
 */
export function fmtDateTime(v: string | Date | null | undefined, withSeconds = false): string {
  if (v == null || v === '') return ''
  const d = v instanceof Date ? v : new Date(v)
  if (Number.isNaN(d.getTime())) return String(v)
  const time = d.toLocaleTimeString('en-GB', {
    hour: '2-digit', minute: '2-digit',
    ...(withSeconds ? { second: '2-digit' } : {}),
    hour12: false,
  })
  return `${fmtDate(d)} ${time}`
}

/** Reporting period: 2026-07 → 07/2026 */
export function fmtPeriod(v: string | null | undefined): string {
  const m = /^(\d{4})-(\d{2})/.exec(String(v ?? ''))
  return m ? `${m[2]}/${m[1]}` : String(v ?? '')
}

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

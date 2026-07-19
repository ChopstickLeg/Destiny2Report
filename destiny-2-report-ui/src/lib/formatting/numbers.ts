/**
 * Number formatting used across statistics.
 * The backend already rounds rate fields (`clearRate`, `winRate`) to four
 * decimal places as fractions of 1. They are multiplied by 100 exactly
 * once, here.
 */

const INTEGER_FORMAT = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 })

export function formatInteger(value: number): string {
  return INTEGER_FORMAT.format(value)
}

/**
 * Percentage from a backend fraction (0–1). "0.5423" → "54.2%".
 */
export function formatPercent(fraction: number, fractionDigits = 1): string {
  return `${(fraction * 100).toFixed(fractionDigits)}%`
}

/**
 * Efficiency ratios (KD, KDA) with a stable two-decimal presentation.
 */
export function formatRatio(value: number): string {
  return value.toFixed(2)
}

/**
 * Share of a total as a percentage string; empty totals return "N/A" instead
 * of implying a measured zero.
 */
export function formatShare(part: number, total: number, fractionDigits = 1): string {
  if (total <= 0) return 'N/A'
  return formatPercent(part / total, fractionDigits)
}

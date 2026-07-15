/**
 * Devuelve las lineas monetarias aptas para mostrar en el listado de operadores.
 * Nunca cae al CurrentBalance legacy: ese escalar no tiene moneda y puede mezclar ARS con USD.
 */
export function supplierBalanceLines(supplier) {
  if (supplier?.amountsVisible === false) return [];

  return (Array.isArray(supplier?.balancesByCurrency) ? supplier.balancesByCurrency : [])
    .map((line) => ({
      currency: String(line?.currency || "ARS").toUpperCase(),
      balance: Number(line?.balance || 0),
    }))
    .filter((line) => Number.isFinite(line.balance) && line.balance !== 0)
    .sort((a, b) => a.currency.localeCompare(b.currency));
}

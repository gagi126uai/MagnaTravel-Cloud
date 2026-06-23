import React from "react";
import { Link } from "react-router-dom";
import { ExternalLink, TrendingUp } from "lucide-react";
import { formatCurrency } from "../../../lib/utils";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { isAdmin, hasPermission } from "../../../auth";

/**
 * Franja de 3 ejes del Estado de Cuenta de una reserva.
 *
 * Muestra separados y sin mezclar:
 *   1) Venta / Facturación: vendido firme, facturado, falta facturar + chip de estado.
 *   2) Cobranza: cobrado, saldo a cobrar, saldo a favor.
 *   3) Costo / Margen: SOLO para admins o usuarios con permiso cobranzas.see_cost.
 *
 * En multimoneda repite cada bloque numérico por moneda (nunca suma ARS + USD).
 * El saldo del cliente (cuenta corriente) y el link van en este componente como
 * tercer bloque de info, separado de la cobranza de la reserva.
 *
 * Decisión UX 2026-06-22: tres ejes separados, sin mezclarlos.
 *
 * Props:
 *   - reserva: el DTO completo de la reserva (ya cargado en la página).
 *   - saldoClientePorMoneda: array { currency, amount } con saldos a favor del cliente
 *     (de su cuenta corriente, fetch best-effort). null = no cargado aún o error.
 *   - loadingSaldoCliente: bool mientras se carga el saldo del cliente.
 */
export function EstadoCuentaResumen({ reserva, saldoClientePorMoneda, loadingSaldoCliente }) {
  // Permiso de ver costos: admin o tiene cobranzas.see_cost
  const puedeVerCostos = isAdmin() || hasPermission("cobranzas.see_cost");

  // Multimoneda: si hay más de una moneda usamos el array porMoneda
  const esMultimoneda =
    reserva.esMultimoneda && Array.isArray(reserva.porMoneda) && reserva.porMoneda.length > 1;

  // Link a la cuenta del cliente (si el DTO trae el publicId del cliente)
  const clientePublicId = reserva.customerPublicId;

  return (
    <div className="space-y-6">

      {/* ── Eje 1: Venta / Facturación ─────────────────────────────────────── */}
      <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="border-b border-slate-100 bg-slate-50/30 px-6 py-3 dark:border-slate-800 dark:bg-slate-800/10">
          <h4 className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
            Venta y facturación
          </h4>
        </div>
        <div className="flex flex-wrap gap-6 px-6 py-4">
          {esMultimoneda ? (
            // Multimoneda: "Vendido firme" SÍ por moneda (el backend lo expone en porMoneda[]).
            // "Facturado" y "Falta facturar" NO por moneda: el backend los expone como escalares
            // (mezcla de monedas). Atribuirlos a la fila de ARS sería falsa precisión.
            // Se muestran debajo, separados, con aclaración de que son totales globales.
            <ColumnaNumericaMulti
              label="Vendido firme"
              porMoneda={reserva.porMoneda}
              campo="confirmedSale"
              colorClass="text-slate-800 dark:text-slate-200"
            />
          ) : (
            // Mono-moneda: fila plana con los tres valores
            <>
              <EjeNumero
                label="Vendido firme"
                valor={reserva.confirmedSale}
                moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
                colorClass="text-slate-800 dark:text-slate-200"
              />
              <EjeNumero
                label="Facturado"
                valor={reserva.facturadoNeto}
                moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
                colorClass="text-indigo-700 dark:text-indigo-400"
              />
              <EjeNumero
                label="Falta facturar"
                valor={reserva.disponibleParaFacturar}
                moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
                colorClass={
                  (reserva.disponibleParaFacturar ?? 0) > 0
                    ? "text-amber-700 dark:text-amber-400"
                    : "text-slate-400 dark:text-slate-600"
                }
              />
            </>
          )}

          {/* Chip de estado de facturación */}
          <div className="flex items-end pb-1">
            <ChipInvoicingStatus status={reserva.invoicingStatus} />
          </div>
        </div>

        {/* I1: en multimoneda, facturado y falta-facturar son escalares globales (no por moneda).
            El backend mezcla monedas en estos campos — mostrarlos en la columna de ARS sería mentira.
            Se presentan como totales globales, separados del grid por moneda, con nota aclaratoria. */}
        {esMultimoneda && (
          <div className="border-t border-slate-100 bg-slate-50/50 px-6 py-3 dark:border-slate-800 dark:bg-slate-800/20">
            <div className="flex flex-wrap items-center gap-6">
              <EjeEscalarGlobal
                label="Facturación (total, todas las monedas)"
                valor={reserva.facturadoNeto}
                colorClass="text-indigo-700 dark:text-indigo-400"
              />
              <EjeEscalarGlobal
                label="Falta facturar (total)"
                valor={reserva.disponibleParaFacturar}
                colorClass={
                  (reserva.disponibleParaFacturar ?? 0) > 0
                    ? "text-amber-700 dark:text-amber-400"
                    : "text-slate-400 dark:text-slate-600"
                }
              />
            </div>
            {/* Aclaración honesta: estos totales mezclan monedas (ARS + USD convertidos o directos). */}
            <p className="mt-1 text-[10px] text-slate-400 dark:text-slate-500">
              Estos totales combinan todas las monedas. No se pueden desglosar por moneda aún.
            </p>
          </div>
        )}
      </div>

      {/* ── Eje 2: Cobranza ────────────────────────────────────────────────── */}
      <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="border-b border-slate-100 bg-slate-50/30 px-6 py-3 dark:border-slate-800 dark:bg-slate-800/10">
          <h4 className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
            Cobranza
          </h4>
        </div>
        <div className="flex flex-wrap gap-6 px-6 py-4">
          {esMultimoneda ? (
            <>
              <ColumnaNumericaMulti
                label="Cobrado"
                porMoneda={reserva.porMoneda}
                campo="totalPaid"
                colorClass="text-emerald-700 dark:text-emerald-500"
              />
              <ColumnaNumericaMulti
                label="Saldo a cobrar"
                porMoneda={reserva.porMoneda}
                campo="balance"
                colorClass="text-rose-600 dark:text-rose-500"
              />
            </>
          ) : (
            <>
              {/* Fix "Recaudado": usamos TotalPaid del backend directamente.
                  No recalculamos sumando reserva.payments en el front (puede incluir
                  pagos puente AffectsCash=false y divergir del backend). */}
              <EjeNumero
                label="Cobrado"
                valor={reserva.totalPaid}
                moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
                colorClass="text-emerald-700 dark:text-emerald-500"
              />
              <EjeNumero
                label="Saldo a cobrar"
                valor={reserva.balance}
                moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
                colorClass={
                  (reserva.balance ?? 0) > 0
                    ? "text-rose-600 dark:text-rose-500"
                    : "text-slate-400 dark:text-slate-600"
                }
              />
            </>
          )}

          {/* Saldo a favor de ESTA reserva (collectionStatus del backend) */}
          {reserva.collectionStatus === "SaldoAFavor" && (
            <div className="flex flex-col gap-0.5">
              <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
                Estado
              </span>
              <span className="rounded-full bg-emerald-100 px-3 py-1 text-xs font-black uppercase text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400">
                A favor
              </span>
            </div>
          )}
        </div>
      </div>

      {/* ── Eje 3: Costo / Margen (solo si el usuario puede ver costos) ────── */}
      {puedeVerCostos && (
        <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <div className="border-b border-slate-100 bg-slate-50/30 px-6 py-3 dark:border-slate-800 dark:bg-slate-800/10">
            <h4 className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
              Costo y margen
              <span className="ml-2 rounded bg-amber-100 px-1.5 py-0.5 text-[9px] text-amber-700 dark:bg-amber-900/30 dark:text-amber-400">
                Solo visible para vos
              </span>
            </h4>
          </div>
          <div className="flex flex-wrap gap-6 px-6 py-4">
            {esMultimoneda ? (
              <>
                <ColumnaNumericaMulti
                  label="Inversión (costo)"
                  porMoneda={reserva.porMoneda}
                  campo="totalCost"
                  colorClass="text-slate-600 dark:text-slate-400"
                />
                <ColumnaNumericaMulti
                  label={<span className="flex items-center gap-1"><TrendingUp className="h-3 w-3" />Margen</span>}
                  porMoneda={reserva.porMoneda}
                  campo="margin"
                  colorClass="text-violet-700 dark:text-violet-400"
                />
              </>
            ) : (
              <>
                <EjeNumero
                  label="Inversión (costo)"
                  valor={reserva.totalCost}
                  moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
                  colorClass="text-slate-600 dark:text-slate-400"
                />
                <EjeNumero
                  label={<span className="flex items-center gap-1"><TrendingUp className="h-3 w-3" />Margen</span>}
                  valor={reserva.totalMargin}
                  moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
                  colorClass={
                    (reserva.totalMargin ?? 0) >= 0
                      ? "text-violet-700 dark:text-violet-400"
                      : "text-rose-600 dark:text-rose-500"
                  }
                />
              </>
            )}
          </div>
        </div>
      )}

      {/* ── Saldo a favor del cliente + link a su cuenta ───────────────────── */}
      {clientePublicId && (
        <div className="flex flex-col gap-3 rounded-xl border border-slate-200 bg-slate-50 px-5 py-4 dark:border-slate-800 dark:bg-slate-800/30">
          <div className="flex items-center justify-between gap-3 flex-wrap">
            <span className="text-sm font-bold text-slate-700 dark:text-slate-300">
              Cuenta corriente del cliente
            </span>
            <Link
              to={`/customers/${clientePublicId}/account`}
              className="inline-flex items-center gap-1.5 rounded-lg border border-indigo-200 px-3 py-1.5 text-xs font-bold text-indigo-700 transition-colors hover:bg-indigo-50 dark:border-indigo-800 dark:text-indigo-300 dark:hover:bg-indigo-900/20"
            >
              <ExternalLink className="h-3.5 w-3.5" />
              Ver cuenta del cliente
            </Link>
          </div>

          {/* Saldo a favor del cliente en todas sus reservas (fetch best-effort) */}
          {loadingSaldoCliente ? (
            <span className="text-xs text-slate-400 dark:text-slate-500">Cargando saldo del cliente…</span>
          ) : Array.isArray(saldoClientePorMoneda) && saldoClientePorMoneda.length > 0 ? (
            <div className="flex flex-wrap gap-3">
              {saldoClientePorMoneda.map((entrada) => (
                <span
                  key={entrada.currency}
                  className="inline-flex items-center gap-1.5 rounded-full bg-emerald-100 px-3 py-1 text-xs font-bold text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400"
                >
                  A favor: {formatCurrency(entrada.amount, entrada.currency)}
                </span>
              ))}
            </div>
          ) : null}
        </div>
      )}

    </div>
  );
}

// ─── Componentes internos de presentación ───────────────────────────────────

/**
 * Un número de eje en modo mono-moneda.
 * label puede ser string o JSX (para el margen con ícono).
 */
function EjeNumero({ label, valor, moneda, colorClass }) {
  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
        {label}
      </span>
      <span className={`text-xl font-extrabold leading-none ${colorClass}`}>
        {formatCurrency(valor ?? 0, moneda)}
      </span>
    </div>
  );
}

/**
 * Valor escalar global (sin moneda específica) para datos que el backend devuelve
 * como mezcla de monedas y no se pueden desglosar por moneda.
 *
 * Se usa en multimoneda para "Facturado" y "Falta facturar", que son escalares globales.
 * Muestra el número sin símbolo de moneda para no mentirle al usuario.
 */
function EjeEscalarGlobal({ label, valor, colorClass }) {
  // Formateamos el número sin símbolo de moneda porque es una mezcla de monedas.
  // Usamos es-AR con notación decimal estándar.
  const valorFormateado = valor != null
    ? new Intl.NumberFormat("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(valor)
    : "—";

  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
        {label}
      </span>
      <span className={`text-lg font-extrabold leading-none ${colorClass}`}>
        {valorFormateado}
      </span>
    </div>
  );
}

/**
 * Una columna numérica en modo multimoneda: apila una línea por moneda.
 * Si el valor del campo es null para una moneda, muestra nullLabel ("—").
 */
function ColumnaNumericaMulti({ label, porMoneda, campo, colorClass, nullLabel }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
        {label}
      </span>
      <div className="flex flex-col gap-1">
        {porMoneda.map((pm) => {
          const valor = pm[campo];
          return (
            <div key={pm.currency} className="flex items-center gap-1.5">
              <CurrencyBadge currency={pm.currency} size="sm" />
              <span className={`text-lg font-extrabold leading-none ${colorClass}`}>
                {valor == null
                  ? (nullLabel ?? formatCurrency(0, pm.currency))
                  : formatCurrency(valor, pm.currency)}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

/**
 * Chip de estado de facturación.
 * El backend puede devolver: NotInvoiced / PartiallyInvoiced / FullyInvoiced.
 */
function ChipInvoicingStatus({ status }) {
  if (!status || status === "NotInvoiced") {
    return (
      <span className="rounded-full bg-slate-100 px-3 py-1 text-[10px] font-black uppercase text-slate-500 dark:bg-slate-800 dark:text-slate-400">
        Sin facturar
      </span>
    );
  }
  if (status === "PartiallyInvoiced") {
    return (
      <span className="rounded-full bg-amber-100 px-3 py-1 text-[10px] font-black uppercase text-amber-700 dark:bg-amber-900/30 dark:text-amber-400">
        Facturada parcial
      </span>
    );
  }
  if (status === "FullyInvoiced") {
    return (
      <span className="rounded-full bg-emerald-100 px-3 py-1 text-[10px] font-black uppercase text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400">
        Facturada total
      </span>
    );
  }
  return null;
}

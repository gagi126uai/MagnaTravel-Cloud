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
            // Multimoneda: Vendido firme, Facturado y Falta facturar TODOS vienen por moneda
            // desde porMoneda[].facturadoNeto y porMoneda[].disponibleParaFacturar (nuevo backend).
            // Se muestran en columnas separadas, una fila por moneda en cada columna.
            <>
              <ColumnaNumericaMulti
                label="Vendido firme"
                porMoneda={reserva.porMoneda}
                campo="confirmedSale"
                colorClass="text-slate-800 dark:text-slate-200"
              />
              <ColumnaNumericaMulti
                label="Facturado"
                porMoneda={reserva.porMoneda}
                campo="facturadoNeto"
                colorClass="text-indigo-700 dark:text-indigo-400"
              />
              {/* F4-5: data-testid en la columna de "Falta facturar" para tests y QA.
                  Sub-testids por moneda van en ColumnaNumericaMultiCondicional. */}
              <div data-testid="kpi-falta-facturar">
                <ColumnaNumericaMultiCondicional
                  label="Falta facturar"
                  porMoneda={reserva.porMoneda}
                  campo="disponibleParaFacturar"
                  rowTestIdPrefix="kpi-falta-facturar"
                />
              </div>
            </>
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
              {/* F4-5: data-testid en mono-moneda también, para consistencia en tests. */}
              <div data-testid="kpi-falta-facturar">
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
              </div>
            </>
          )}

          {/* Chip de estado de facturación */}
          <div className="flex items-end pb-1">
            <ChipInvoicingStatus status={reserva.invoicingStatus} />
          </div>
        </div>

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
              {/*
                BUG IMP-4 fix 2026-06-24: en vez de mostrar "Saldo a cobrar" con color rojo
                fijo para todas las monedas, usamos ColumnaBalanceMulti que distingue:
                  - balance > 0: "Saldo a cobrar" en rojo (cliente debe plata).
                  - balance < 0: "A favor" en verde (cliente pagó de más en esa moneda).
                  - balance = 0: gris neutro.
                No mezclamos monedas: cada moneda tiene su propio signo.
              */}
              <ColumnaBalanceMulti porMoneda={reserva.porMoneda} />
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
              {/*
                BUG IMP-4 fix 2026-06-24: cuando balance < 0 el cliente pagó de más →
                mostrar "A favor" en verde con el monto en positivo, no "Saldo a cobrar: -$X" en rojo.
                  - balance > 0: "Saldo a cobrar" rojo.
                  - balance < 0: "A favor" verde, mostramos Math.abs(balance).
                  - balance = 0: "Saldo a cobrar: $0" en gris neutro.
              */}
              <EjeBalanceMono
                balance={reserva.balance}
                moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
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
 * Columna numérica multimoneda con color condicional por valor.
 * Se usa para "Falta facturar" donde el color cambia según si queda algo pendiente (ámbar)
 * o si ya está todo facturado (gris apagado).
 *
 * F4-5: acepta `rowTestIdPrefix` para agregar data-testid por fila de moneda.
 * Ej: rowTestIdPrefix="kpi-falta-facturar" → data-testid="kpi-falta-facturar-ars" / "...-usd".
 */
function ColumnaNumericaMultiCondicional({ label, porMoneda, campo, rowTestIdPrefix }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
        {label}
      </span>
      <div className="flex flex-col gap-1">
        {porMoneda.map((pm) => {
          const valor = pm[campo] ?? 0;
          // Color ámbar si queda algo pendiente, gris si es cero o negativo.
          const colorClass =
            valor > 0
              ? "text-amber-700 dark:text-amber-400"
              : "text-slate-400 dark:text-slate-600";
          return (
            <div
              key={pm.currency}
              className="flex items-center gap-1.5"
              data-testid={rowTestIdPrefix ? `${rowTestIdPrefix}-${pm.currency.toLowerCase()}` : undefined}
            >
              <CurrencyBadge currency={pm.currency} size="sm" />
              <span className={`text-lg font-extrabold leading-none ${colorClass}`}>
                {formatCurrency(valor, pm.currency)}
              </span>
            </div>
          );
        })}
      </div>
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
 * Muestra el balance en modo mono-moneda con semántica de saldo a favor / saldo a cobrar.
 *
 * BUG IMP-4 fix 2026-06-24:
 *  - balance > 0: "Saldo a cobrar" en rojo (cliente debe plata).
 *  - balance < 0: "A favor" en verde, mostrando el monto en positivo (Math.abs).
 *  - balance = 0: "Saldo a cobrar: $0" en gris neutro.
 *
 * Regla de negocio: balance negativo = el cliente pagó de más en esta reserva.
 * Mostrarlo como deuda roja confunde al vendedor; debe verse como crédito verde.
 */
function EjeBalanceMono({ balance, moneda }) {
  const valor = balance ?? 0;

  if (valor < 0) {
    return (
      <div className="flex flex-col gap-0.5">
        <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
          A favor
        </span>
        <span className="text-xl font-extrabold leading-none text-emerald-600 dark:text-emerald-500">
          {formatCurrency(Math.abs(valor), moneda)}
        </span>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
        Saldo a cobrar
      </span>
      <span className={`text-xl font-extrabold leading-none ${
        valor > 0
          ? "text-rose-600 dark:text-rose-500"
          : "text-slate-400 dark:text-slate-600"
      }`}>
        {formatCurrency(valor, moneda)}
      </span>
    </div>
  );
}

/**
 * Columna de balance en modo multimoneda. Por cada moneda en porMoneda[],
 * si el balance es negativo muestra "A favor" en verde; si es positivo o cero,
 * "Saldo a cobrar" en rojo/gris.
 *
 * BUG IMP-4 fix 2026-06-24: antes se pasaba colorClass="text-rose-600" fijo
 * a ColumnaNumericaMulti, ignorando el signo del balance por moneda.
 */
function ColumnaBalanceMulti({ porMoneda }) {
  return (
    <div className="flex flex-col gap-1">
      {/* La etiqueta de cabecera es dinámica: si TODAS las monedas son a favor
          mostramos "A favor"; si hay mezcla o todas son deuda, "Saldo a cobrar". */}
      <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
        Saldo
      </span>
      <div className="flex flex-col gap-2">
        {porMoneda.map((pm) => {
          const valor = pm.balance ?? 0;
          const esAFavor = valor < 0;
          return (
            <div key={pm.currency} className="flex flex-col gap-0.5">
              <div className="flex items-center gap-1.5">
                <CurrencyBadge currency={pm.currency} size="sm" />
                <span className={`text-lg font-extrabold leading-none ${
                  esAFavor
                    ? "text-emerald-600 dark:text-emerald-500"
                    : valor > 0
                    ? "text-rose-600 dark:text-rose-500"
                    : "text-slate-400 dark:text-slate-600"
                }`}>
                  {formatCurrency(esAFavor ? Math.abs(valor) : valor, pm.currency)}
                </span>
              </div>
              {/* Sub-etiqueta por moneda para que quede claro si es deuda o crédito */}
              <span className={`text-[9px] font-semibold uppercase tracking-wider ${
                esAFavor
                  ? "text-emerald-500 dark:text-emerald-600"
                  : valor > 0
                  ? "text-rose-400 dark:text-rose-600"
                  : "text-slate-300 dark:text-slate-700"
              }`}>
                {esAFavor ? "a favor" : valor > 0 ? "a cobrar" : "saldado"}
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
    // Fix C5 (Tanda 6, 2026-07-05): unificamos el rótulo con ReservaStatusChips
    // ("Facturada en parte"). Antes decía "Facturada parcial" acá y distinto en el chip
    // de la ficha — mismo estado, dos textos, confundía a quien comparaba las dos pantallas.
    return (
      <span className="rounded-full bg-amber-100 px-3 py-1 text-[10px] font-black uppercase text-amber-700 dark:bg-amber-900/30 dark:text-amber-400">
        Facturada en parte
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

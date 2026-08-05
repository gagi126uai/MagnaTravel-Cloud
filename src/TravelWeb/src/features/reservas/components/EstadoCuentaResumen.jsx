import React from "react";
import { Link } from "react-router-dom";
import { ExternalLink, TrendingUp } from "lucide-react";
import { formatCurrency } from "../../../lib/utils";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { isAdmin, hasPermission } from "../../../auth";
import { formatearFaltaFacturar, formatearMargen } from "../lib/invoicingSummaryLogic";

/**
 * Franja de la solapa Estado de Cuenta con los ejes de plata que NO se ven en
 * ningún otro lado de la ficha:
 *   1) Venta / Facturación: vendido firme, facturado, falta facturar + chip de estado.
 *   2) Costo / Margen: SOLO para admins o usuarios con permiso cobranzas.see_cost.
 *
 * Fix (Tanda 4 del rediseño de fichas, 2026-08-04, maqueta sección 9, nota "Sin
 * repetir la plata de arriba"): el eje "Cobranza" (Cobrado / Saldo a cobrar / A
 * favor) se sacó de acá — esos mismos tres números YA están en el encabezado de
 * la ficha (los "números grandes": Saldo a cobrar, Recaudado, Inversión), y
 * mostrarlos de nuevo acá era repetir la misma plata dos veces en la misma
 * pantalla. "Costo y margen" se conserva porque el Margen es un dato que NO
 * está en ningún otro lado (Inversión sí se repite del encabezado, pero va de
 * la mano del Margen — separarlos hubiese sido más confuso que mostrar un
 * numerito de más).
 *
 * En multimoneda repite cada bloque numérico por moneda (nunca suma ARS + USD).
 * El saldo del cliente (cuenta corriente) y el link van en este componente como
 * bloque de info aparte, separado de la venta/facturación de la reserva.
 *
 * Decisión UX 2026-06-22: ejes de plata separados, sin mezclarlos.
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
                <EjeFaltaFacturar
                  valor={reserva.disponibleParaFacturar}
                  moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
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
                <ColumnaMargenMulti porMoneda={reserva.porMoneda} />
              </>
            ) : (
              <>
                <EjeNumero
                  label="Inversión (costo)"
                  valor={reserva.totalCost}
                  moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
                  colorClass="text-slate-600 dark:text-slate-400"
                />
                <EjeMargen
                  valor={reserva.totalMargin}
                  moneda={reserva.porMoneda?.[0]?.currency ?? "ARS"}
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
 * "Margen" en modo mono-moneda (FIX 2026-08-05, prueba integral: un margen negativo
 * se pintaba violeta —igual que una ganancia— y mostraba el signo "-" pelado). Sin
 * CurrencyBadge acá (mono-moneda), así que `formatearMargen` sigue mostrando el
 * símbolo "$"/"US$" como siempre.
 */
function EjeMargen({ valor, moneda }) {
  const { texto, esPerdida } = formatearMargen(valor, moneda);
  const colorClass = esPerdida
    ? "text-rose-600 dark:text-rose-500"
    : "text-violet-700 dark:text-violet-400";

  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500 flex items-center gap-1">
        <TrendingUp className="h-3 w-3" />Margen
      </span>
      <span className={`text-xl font-extrabold leading-none ${colorClass}`}>
        {texto}
      </span>
    </div>
  );
}

/**
 * "Falta facturar" en modo mono-moneda (hallazgo #23 del barrido de PROD 2026-07-24):
 * si `disponibleParaFacturar` da negativo (se facturó más de lo vendido firme), en vez
 * del número pelado con signo se muestra la frase explicativa que arma
 * `formatearFaltaFacturar` — mismo criterio que la versión multimoneda de abajo.
 */
function EjeFaltaFacturar({ valor, moneda }) {
  const { texto, esExceso } = formatearFaltaFacturar(valor, moneda);
  const colorClass = esExceso
    ? "text-violet-700 dark:text-violet-400"
    : (valor ?? 0) > 0
    ? "text-amber-700 dark:text-amber-400"
    : "text-slate-400 dark:text-slate-600";

  return (
    <div className="flex flex-col gap-0.5">
      <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
        Falta facturar
      </span>
      <span className={`text-xl font-extrabold leading-none ${colorClass}`}>
        {texto}
      </span>
    </div>
  );
}

/**
 * Columna numérica multimoneda con color condicional por valor.
 * Se usa para "Falta facturar" donde el color cambia según si queda algo pendiente (ámbar),
 * ya está todo facturado (gris apagado) o se facturó de MÁS (hallazgo #23 del barrido:
 * en ese caso `formatearFaltaFacturar` arma la frase explicativa en vez del número negativo pelado).
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
          // withSymbol:false: el CurrencyBadge de al lado ya muestra el "$"/"US$" — repetirlo
          // acá era el bug "US$ US$5.800,00" (fix símbolo duplicado, prueba integral 2026-08-05).
          const { texto, esExceso } = formatearFaltaFacturar(valor, pm.currency, { withSymbol: false });
          // Ámbar si queda algo pendiente, violeta si se facturó de más (llama la atención
          // sin ser un color de error), gris si es cero.
          const colorClass = esExceso
            ? "text-violet-700 dark:text-violet-400"
            : valor > 0
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
                {texto}
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
 *
 * withSymbol:false en el formatCurrency de acá adentro: el CurrencyBadge de cada fila
 * ya muestra el "$"/"US$", así que el número no lo repite (fix símbolo duplicado,
 * prueba integral 2026-08-05 — antes se leía "US$ US$5.800,00").
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
                  ? (nullLabel ?? formatCurrency(0, pm.currency, { withSymbol: false }))
                  : formatCurrency(valor, pm.currency, { withSymbol: false })}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

/**
 * "Margen" en modo multimoneda (FIX 2026-08-05, prueba integral): cada moneda evalúa
 * su propio signo (regla P-3⭐ — puede haber ganancia en ARS y pérdida en USD a la
 * vez), rojo + "Pérdida de $X" cuando da negativo, en vez de violeta con el signo
 * pelado. No usa el `ColumnaNumericaMulti` genérico porque el color y el texto
 * dependen del signo de CADA fila, no de un `colorClass` fijo para toda la columna.
 */
function ColumnaMargenMulti({ porMoneda }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500 flex items-center gap-1">
        <TrendingUp className="h-3 w-3" />Margen
      </span>
      <div className="flex flex-col gap-1">
        {porMoneda.map((pm) => {
          // withSymbol:false: el CurrencyBadge de al lado ya muestra el "$"/"US$".
          const { texto, esPerdida } = formatearMargen(pm.margin, pm.currency, { withSymbol: false });
          const colorClass = esPerdida
            ? "text-rose-600 dark:text-rose-500"
            : "text-violet-700 dark:text-violet-400";
          return (
            <div key={pm.currency} className="flex items-center gap-1.5">
              <CurrencyBadge currency={pm.currency} size="sm" />
              <span className={`text-lg font-extrabold leading-none ${colorClass}`}>
                {texto}
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
 * El backend puede devolver: NotInvoiced / PartiallyInvoiced / FullyInvoiced / FullyReturned.
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
  if (status === "FullyReturned") {
    // ADR-048 T3/T4 (2026-07-17, spec Punto 2, P1=B FIRMADA): hubo factura y una Nota de
    // Crédito la devolvió entera. ANTES este chip desaparecía (return null) para este valor
    // nuevo — un hueco visual justo donde había que mostrar el rastro fiscal. Mismo gris
    // pizarra + tilde que ReservaStatusChips, para que las dos pantallas digan lo mismo.
    return (
      <span className="rounded-full bg-slate-100 px-3 py-1 text-[10px] font-black uppercase text-slate-600 dark:bg-slate-800 dark:text-slate-300">
        ✓ Facturada y devuelta
      </span>
    );
  }
  return null;
}

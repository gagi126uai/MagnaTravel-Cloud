import React, { useState, useEffect, useCallback } from "react";
import { RefreshCw, Loader2, BookOpen } from "lucide-react";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import {
  DataGrid,
  DataGridBody,
  DataGridCell,
  DataGridEmptyState,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";

/**
 * Extracto contable de la reserva: una línea cronológica por factura, cobro y NC.
 *
 * Estilo libro mayor / extracto bancario:
 *   - Factura / Nota de Débito → columna CARGO (suma deuda).
 *   - Cobro / Nota de Crédito → columna ABONO (resta deuda).
 *   - Cada línea muestra el saldo corriente acumulado.
 *
 * Un bloque por moneda (apilados). Decisión UX 2026-06-22: nunca mezcla monedas.
 *
 * Las acciones por renglón (ver PDF, emitir/anular recibo) se inyectan desde afuera
 * vía las props `renderAccionesFactura` y `renderAccionesCobro`. De esa forma este
 * componente no necesita saber cómo funciona InvoicePdfActions ni PaymentReceiptActions;
 * el padre (ReservaDetailPage) los conecta con los handlers existentes.
 *
 * Props:
 *   - reservaPublicId: string — el publicId de la reserva.
 *   - reserva: el DTO completo de la reserva (para cruzar sourcePublicId con invoices[]/payments[]).
 *   - congelado: boolean — en estados congelados las acciones de escritura se ocultan.
 *   - refreshKey: number — se incrementa desde el padre cada vez que ocurre un cambio de plata
 *       (cobro registrado, factura emitida, anulación, comprobante emitido/anulado). El extracto
 *       lo incluye como dependencia del efecto de carga para que se recargue automáticamente.
 *       No se necesita refresco manual: solo cambia ante acciones reales, nunca en cada render.
 *   - renderAccionesFactura(invoice): función que recibe la factura y devuelve JSX de acciones.
 *   - renderAccionesCobro(payment, congelado): función que recibe el pago y devuelve JSX.
 */
export function EstadoCuentaExtracto({
  reservaPublicId,
  reserva,
  congelado,
  refreshKey,
  renderAccionesFactura,
  renderAccionesCobro,
}) {
  const [extracto, setExtracto] = useState(null);
  const [cargando, setCargando] = useState(true);
  const [error, setError] = useState(null);

  const cargarExtracto = useCallback(async () => {
    setCargando(true);
    setError(null);
    try {
      const data = await api.get(`/reservas/${reservaPublicId}/account-statement`);
      setExtracto(data);
    } catch (err) {
      setError(getApiErrorMessage(err) || "No se pudo cargar el extracto.");
    } finally {
      setCargando(false);
    }
    // refreshKey se incluye como dependencia para que el extracto se recargue
    // automáticamente cada vez que el padre registra un cambio de plata
    // (cobro, factura, anulación, comprobante). El padre solo la incrementa ante
    // acciones reales, así que no hay riesgo de recarga en bucle.
  }, [reservaPublicId, refreshKey]); // eslint-disable-line react-hooks/exhaustive-deps

  // Carga al montar. Se recarga si cambia el publicId de la reserva o refreshKey.
  useEffect(() => {
    cargarExtracto();
  }, [cargarExtracto]);

  if (cargando) {
    return (
      <div className="flex items-center justify-center gap-2 py-10 text-sm text-slate-400 dark:text-slate-500">
        <Loader2 className="h-4 w-4 animate-spin" />
        Cargando extracto…
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex flex-col items-center gap-3 py-10 text-center">
        <p className="text-sm text-rose-600 dark:text-rose-400">{error}</p>
        <button
          type="button"
          onClick={cargarExtracto}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
        >
          <RefreshCw className="h-3.5 w-3.5" />
          Reintentar
        </button>
      </div>
    );
  }

  const bloques = extracto?.currencies ?? [];

  // El extracto se considera vacío si no hay ninguna línea en ningún bloque.
  const totalLineas = bloques.reduce((acc, b) => acc + (b.lines?.length ?? 0), 0);

  if (totalLineas === 0) {
    return (
      <div className="py-10 text-center">
        <BookOpen className="mx-auto mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Todavía no hay movimientos en esta reserva.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      {bloques.map((bloque) => (
        <BloqueMoneda
          key={bloque.currency}
          bloque={bloque}
          reserva={reserva}
          congelado={congelado}
          renderAccionesFactura={renderAccionesFactura}
          renderAccionesCobro={renderAccionesCobro}
        />
      ))}

      {/*
        Aclaración honesta (decisión UX 2026-06-22): el saldo del extracto refleja lo FACTURADO.
        Si todavía hay servicios confirmados sin facturar, el "Saldo a cobrar" de arriba
        (que refleja lo VENDIDO / confirmado) puede ser mayor que el saldo del extracto.
        Texto chico y sobrio — no alarma, solo informa.
      */}
      <p className="text-[10px] text-slate-400 dark:text-slate-500 text-center">
        El saldo del extracto refleja lo facturado. Puede diferir del "Saldo a cobrar" si hay servicios
        confirmados aún sin facturar.
      </p>
    </div>
  );
}

// ─── Bloque de una moneda ────────────────────────────────────────────────────

function BloqueMoneda({ bloque, reserva, congelado, renderAccionesFactura, renderAccionesCobro }) {
  return (
    <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      {/* Cabecera del bloque con moneda y saldo de cierre */}
      <div className="flex items-center justify-between gap-3 border-b border-slate-100 bg-slate-50/30 px-6 py-3 dark:border-slate-800 dark:bg-slate-800/10">
        <div className="flex items-center gap-2">
          <CurrencyBadge currency={bloque.currency} />
          <span className="text-sm font-bold text-slate-700 dark:text-slate-300">
            {bloque.currency === "USD" ? "Dólares" : "Pesos"}
          </span>
        </div>
        <div className="flex items-center gap-2">
          <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400">
            Saldo
          </span>
          <span
            className={`text-sm font-extrabold ${
              (bloque.closingBalance ?? 0) > 0
                ? "text-rose-600 dark:text-rose-500"
                : (bloque.closingBalance ?? 0) < 0
                ? "text-emerald-600 dark:text-emerald-500"
                : "text-slate-400 dark:text-slate-600"
            }`}
          >
            {formatCurrency(bloque.closingBalance ?? 0, bloque.currency)}
          </span>
        </div>
      </div>

      {/* Tabla del extracto */}
      <DataGrid density="compact" minWidth="860px">
        <DataGridHeader>
          <DataGridHeaderRow>
            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
            <DataGridHeaderCell>Concepto</DataGridHeaderCell>
            <DataGridHeaderCell>Comprobante</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Cargo</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Abono</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Saldo</DataGridHeaderCell>
            <DataGridHeaderCell>Acciones</DataGridHeaderCell>
          </DataGridHeaderRow>
        </DataGridHeader>
        <DataGridBody>
          {bloque.lines?.length > 0 ? (
            bloque.lines.map((linea, idx) => (
              <FilaExtracto
                key={`${linea.kind}-${linea.sourcePublicId ?? idx}`}
                linea={linea}
                reserva={reserva}
                congelado={congelado}
                renderAccionesFactura={renderAccionesFactura}
                renderAccionesCobro={renderAccionesCobro}
              />
            ))
          ) : (
            <DataGridEmptyState colSpan={7} title="Sin movimientos en esta moneda." />
          )}
        </DataGridBody>
      </DataGrid>
    </div>
  );
}

// ─── Fila individual del extracto ────────────────────────────────────────────

/**
 * Una fila del extracto. Determina el tipo de línea (factura vs cobro) y
 * cruza el sourcePublicId con las colecciones del DTO de la reserva para
 * obtener el objeto original (donde están los campos necesarios para las acciones).
 *
 * Regla de cruce:
 *   - Kind "Invoice" / "CreditNote" / "DebitNote" → cruzar con reserva.invoices[]
 *   - Kind "Payment" → cruzar con reserva.payments[]
 *
 * Importante: la moneda de cargo/abono viene del bloque, NO de ARS hardcodeado.
 */
function FilaExtracto({ linea, reserva, congelado, renderAccionesFactura, renderAccionesCobro }) {
  const esCargo = linea.charge > 0;
  const esAbono = linea.credit > 0;

  // Cruce con las colecciones del DTO de la reserva para encontrar el objeto original
  const esDocumentoFiscal =
    linea.kind === "Invoice" || linea.kind === "CreditNote" || linea.kind === "DebitNote";

  const esCobro = linea.kind === "Payment";

  // Buscamos el objeto original por publicId para pasarle a los renderers de acciones.
  // getPublicId() normaliza Guid de distintos casings (publicId, PublicId, id, Id).
  const facturaOrigen = esDocumentoFiscal && linea.sourcePublicId
    ? (reserva.invoices ?? []).find(
        (inv) => String(getPublicId(inv)) === String(linea.sourcePublicId)
      )
    : null;

  const cobroOrigen = esCobro && linea.sourcePublicId
    ? (reserva.payments ?? []).find(
        (pay) => String(getPublicId(pay)) === String(linea.sourcePublicId)
      )
    : null;

  return (
    <DataGridRow>
      <DataGridCell className="text-slate-500 dark:text-slate-400">
        {linea.date ? new Date(linea.date).toLocaleDateString("es-AR") : "—"}
      </DataGridCell>
      <DataGridCell>
        <span className={esCargo ? "font-medium text-slate-800 dark:text-slate-200" : "text-slate-600 dark:text-slate-400"}>
          {linea.description || "—"}
        </span>
      </DataGridCell>
      <DataGridCell className="font-mono text-xs text-slate-500 dark:text-slate-400">
        {linea.documentRef || "—"}
      </DataGridCell>

      {/* Cargo: solo si suma deuda (factura / ND); la columna de abono queda vacía */}
      <DataGridCell align="right">
        {esCargo ? (
          <span className="font-bold text-slate-800 dark:text-slate-200">
            {formatCurrency(linea.charge, linea.currency)}
          </span>
        ) : (
          <span className="text-slate-300 dark:text-slate-700">—</span>
        )}
      </DataGridCell>

      {/* Abono: solo si resta deuda (cobro / NC); la columna de cargo queda vacía */}
      <DataGridCell align="right">
        {esAbono ? (
          <span className="font-bold text-emerald-600 dark:text-emerald-500">
            {formatCurrency(linea.credit, linea.currency)}
          </span>
        ) : (
          <span className="text-slate-300 dark:text-slate-700">—</span>
        )}
      </DataGridCell>

      {/* Saldo corriente — positivo=debe (rojo), negativo=a favor (verde), cero=gris */}
      <DataGridCell align="right">
        <span
          className={`font-extrabold ${
            (linea.runningBalance ?? 0) > 0
              ? "text-rose-600 dark:text-rose-500"
              : (linea.runningBalance ?? 0) < 0
              ? "text-emerald-600 dark:text-emerald-500"
              : "text-slate-400 dark:text-slate-600"
          }`}
        >
          {formatCurrency(linea.runningBalance ?? 0, linea.currency)}
        </span>
      </DataGridCell>

      {/* Acciones: Ver PDF (siempre), Emitir/Anular recibo (según congelado) */}
      <DataGridCell>
        {esDocumentoFiscal && facturaOrigen && typeof renderAccionesFactura === "function" ? (
          renderAccionesFactura(facturaOrigen)
        ) : esCobro && cobroOrigen && typeof renderAccionesCobro === "function" ? (
          renderAccionesCobro(cobroOrigen, congelado)
        ) : (
          // Si no se encontró el objeto en el DTO (datos legacy o recién emitido antes de refresco)
          <span className="text-xs text-slate-400">—</span>
        )}
      </DataGridCell>
    </DataGridRow>
  );
}

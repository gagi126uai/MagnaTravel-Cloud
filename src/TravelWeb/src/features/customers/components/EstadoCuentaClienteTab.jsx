/**
 * Solapa "Estado de cuenta" de la cuenta corriente del cliente.
 *
 * Muestra el extracto cronológico estilo libro mayor que devuelve el backend
 * (GET /customers/{id}/account/statement), YA calculado en el servidor:
 *   - Las ventas confirmadas son CARGOS (aumentan la deuda del cliente).
 *   - Los cobros son ABONOS (reducen la deuda del cliente).
 *   - Hay UN BLOQUE POR MONEDA: el saldo en pesos y el saldo en dólares nunca
 *     se suman ni se mezclan (regla multimoneda del sistema).
 *   - Cada bloque trae su propio saldo corriente (runningBalance) y saldo de
 *     cierre (closingBalance), calculados por el servidor — por construcción
 *     el saldo de cierre reconcilia con el "Debe" del encabezado de la página.
 *
 * Estructura visual (Tanda D2, 2026-07-16 — extracto profesional, 5 columnas):
 *   ┌── Pesos ($) ───────────────────────────────────────────────┐
 *   │  Fecha | Documento | Debe | Haber | Saldo                  │
 *   └────────────────────────────────────────────────────────────┘
 *   ┌── Dólares (US$) ──────────────────────────────────────────┐
 *   │  ...                                                       │
 *   └────────────────────────────────────────────────────────────┘
 * "Documento" funde las viejas columnas "Concepto" + "Comprobante" en una sola: tipo de
 * comprobante + número (ej. "Factura 0001-00012"), con el número de reserva como link
 * chico debajo (formatEtiquetaDocumentoExtracto, en estadoCuentaFormatting.js).
 *
 * Historia (2026-07-01): antes este componente armaba el extracto EN EL
 * NAVEGADOR, cruzando /account/payments + /account/invoices con un techo de
 * 500 movimientos cada uno; por eso el saldo podía no cerrar con el resumen
 * de arriba. Ahora el servidor ya entrega las líneas con su saldo corriente
 * (una fuente autoritativa: venta confirmada de las reservas en firme, la
 * misma que usa el resumen del encabezado), así que no hay techo ni fusión
 * en el cliente, y el cartel de "puede diferir" ya no es necesario.
 *
 * Sin acciones por renglón (a propósito): este extracto es una vista de
 * SOLO LECTURA que cruza todas las reservas del cliente (como un resumen
 * bancario). Para ver el PDF de una factura, eliminar un cobro o anular un
 * recibo, el usuario entra a la reserva puntual (el link de "Concepto" lo
 * lleva ahí) — esas acciones ya viven en el extracto de la reserva
 * (EstadoCuentaExtracto.jsx, dentro de ReservaDetailPage), con el contexto
 * completo del comprobante/recibo.
 *
 * Props:
 *   - estadoCuenta: CustomerAccountStatementDto | null — { currencies: [...] }
 *   - loading: boolean — el padre está cargando el extracto
 *   - error: string | null — mensaje de error si falló la carga
 *   - onRetry: () => void — reintentar la carga tras un error
 *   - onNuevaCobranza(): abre el modal para registrar un nuevo cobro
 *   - canRegistrarCobranza: boolean — si el usuario tiene permiso para registrar cobros
 */
import { Link } from "react-router-dom";
import { BookOpen, Loader2, Plus, RefreshCw } from "lucide-react";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { formatEtiquetaDocumentoExtracto, formatCierreExtracto } from "../lib/estadoCuentaFormatting";
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

// ─── Componente principal ─────────────────────────────────────────────────────

export function EstadoCuentaClienteTab({
  estadoCuenta,
  loading,
  error,
  onRetry,
  onNuevaCobranza,
  canRegistrarCobranza,
}) {
  const bloques = estadoCuenta?.currencies ?? [];
  const totalLineas = bloques.reduce((acc, bloque) => acc + (bloque.lines?.length ?? 0), 0);

  if (loading) {
    return (
      <div className="flex items-center justify-center gap-2 py-12 text-sm text-slate-400" data-testid="extracto-loading">
        <Loader2 className="h-5 w-5 animate-spin" />
        Cargando estado de cuenta...
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex flex-col items-center gap-3 py-12 text-center" data-testid="extracto-error">
        <p className="text-sm text-rose-600 dark:text-rose-400">{error}</p>
        <button
          type="button"
          onClick={onRetry}
          className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
        >
          <RefreshCw className="h-3.5 w-3.5" />
          Reintentar
        </button>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Encabezado: título del extracto + botón "Nuevo cobro" */}
      <div className="flex items-center justify-between gap-4">
        <div className="flex items-center gap-2 text-slate-700 dark:text-slate-300">
          <BookOpen className="h-5 w-5" />
          <span className="font-semibold">Extracto de cuenta</span>
        </div>
        {canRegistrarCobranza && (
          <button
            type="button"
            onClick={onNuevaCobranza}
            className="flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-semibold text-white shadow-sm shadow-emerald-500/20 hover:bg-emerald-700 transition-colors"
            data-testid="btn-nueva-cobranza"
          >
            <Plus className="h-4 w-4" />
            Nuevo cobro
          </button>
        )}
      </div>

      {/* Estado vacío: aún no hay movimientos en ninguna moneda */}
      {totalLineas === 0 && (
        <div
          className="rounded-xl border border-slate-200 bg-white py-12 text-center dark:border-slate-800 dark:bg-slate-900"
          data-testid="extracto-vacio"
        >
          <BookOpen className="mx-auto mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Todavía no hay movimientos en esta cuenta.
          </p>
        </div>
      )}

      {/*
        Un bloque por moneda (el orden lo decide el servidor).
        Regla multimoneda: ARS y USD nunca se mezclan ni se suman.
        Si solo hay movimientos en una moneda, solo aparece ese bloque.
      */}
      {bloques.map((bloque) => (
        <BloqueExtractoCliente key={bloque.currency} bloque={bloque} />
      ))}
    </div>
  );
}

// ─── Bloque de una moneda ─────────────────────────────────────────────────────

/**
 * Tabla del extracto para una moneda.
 *
 * Fix de revisión (2026-07-17, spec §1/§3): el cierre "Saldo al día (debe/a favor): $X"
 * va AL PIE del bloque (como en el mockup firmado), no en la cabecera. Antes la
 * cabecera tenía un chip "Debe: $X" que además decía siempre "Debe" aunque el saldo
 * fuera a favor — mentira cuando `closingBalance` es negativo. La cabecera ahora solo
 * identifica la moneda (badge + nombre); el "Crédito no aplicado" que vivía acá se
 * mudó a la foto de saldo (FotoDeSaldoCuenta, spec §7.3) y no se duplica.
 */
function BloqueExtractoCliente({ bloque }) {
  const saldoCierre = bloque.closingBalance ?? 0;
  const nombreMoneda = bloque.currency === "USD" ? "Dólares" : "Pesos";

  return (
    <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      {/* Cabecera: solo identifica la moneda (badge + nombre) */}
      <div className="flex items-center gap-2 border-b border-slate-100 bg-slate-50/30 px-5 py-3 dark:border-slate-800 dark:bg-slate-800/10">
        <CurrencyBadge currency={bloque.currency} />
        <span className="text-sm font-bold text-slate-700 dark:text-slate-300">
          {nombreMoneda}
        </span>
      </div>

      {/* Tabla de movimientos del bloque: 5 columnas (Tanda D2, spec §3) */}
      <DataGrid density="compact" minWidth="720px">
        <DataGridHeader>
          <DataGridHeaderRow>
            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
            <DataGridHeaderCell>Documento</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Debe</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Haber</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Saldo</DataGridHeaderCell>
          </DataGridHeaderRow>
        </DataGridHeader>
        <DataGridBody>
          {bloque.lines?.length > 0 ? (
            bloque.lines.map((linea, idx) => (
              <FilaExtractoCliente
                // sourcePublicId puede repetirse conceptualmente entre bloques (moneda distinta),
                // así que la key combina moneda + fuente + índice como último respaldo.
                key={`${bloque.currency}-${linea.sourcePublicId ?? idx}-${idx}`}
                linea={linea}
                currency={bloque.currency}
              />
            ))
          ) : (
            <DataGridEmptyState colSpan={5} title="Sin movimientos en esta moneda." />
          )}
        </DataGridBody>
      </DataGrid>

      {/* Cierre AL PIE del bloque (spec §1/§3, mockup firmado): "Saldo al día (debe/a
          favor): $X" — la etiqueta cambia sola según el signo (nunca dice "debe" con un
          saldo a favor). formatCierreExtracto es la función pura y testeada que decide
          el texto exacto; este componente solo lo pinta. */}
      <div
        className="border-t border-slate-100 bg-slate-50/30 px-5 py-2.5 text-right dark:border-slate-800 dark:bg-slate-800/10"
        data-testid={`extracto-cierre-${bloque.currency}`}
      >
        <span
          className={`text-sm font-extrabold ${
            saldoCierre > 0.01
              ? "text-rose-600 dark:text-rose-500"
              : saldoCierre < -0.01
              ? "text-emerald-600 dark:text-emerald-500"
              : "text-slate-400 dark:text-slate-600"
          }`}
        >
          {formatCierreExtracto(saldoCierre, bloque.currency)}
        </span>
      </div>
    </div>
  );
}

// ─── Fila del extracto ────────────────────────────────────────────────────────

/**
 * Una fila del extracto del cliente.
 * `kind` ("Invoice"/"DebitNote"/"CreditNote"/"Payment"/"CreditApplication") decide el
 * texto de "Documento" (formatEtiquetaDocumentoExtracto) y el estilo — nunca se muestra
 * el token crudo. `sourcePublicId`/`reservaPublicId` son GUID internos: solo se usan
 * para key/link, nunca se muestran como texto — el texto visible de la reserva es
 * `numeroReserva`.
 */
function FilaExtractoCliente({ linea, currency }) {
  const esCargo = linea.charge > 0;
  const esAbono = linea.credit > 0;

  return (
    <DataGridRow>
      {/* Fecha del movimiento */}
      <DataGridCell className="text-slate-500 dark:text-slate-400">
        {/* fix 2026-07-22: mismo bug que EstadoCuentaExtracto.jsx/SupplierExtractoSection.jsx
            (cobros fechados corrían un día menos). formatDate() no convierte a hora local del
            navegador una fecha de negocio (día elegido por el usuario) — ver lib/utils.js. */}
        {linea.date ? formatDate(linea.date) : "—"}
      </DataGridCell>

      {/* Documento (Tanda D2, spec §3): fusión de "Concepto" + "Comprobante" en una sola
          columna — tipo + número (ej. "Factura 0001-00012"). Negrita para cargos (deuda),
          normal para abonos. El número de reserva es un link chico debajo: lleva a la
          ficha, donde viven las acciones puntuales (ver factura, eliminar cobro, anular
          recibo) — este extracto es de solo lectura, sin botones por renglón. */}
      <DataGridCell>
        <span className={esCargo ? "font-medium text-slate-800 dark:text-slate-200" : "text-slate-600 dark:text-slate-400"}>
          {formatEtiquetaDocumentoExtracto(linea)}
        </span>
        {linea.reservaPublicId && linea.numeroReserva && (
          <Link
            to={`/reservas/${linea.reservaPublicId}`}
            className="block text-[10px] text-indigo-600 hover:underline dark:text-indigo-400"
          >
            {linea.numeroReserva}
          </Link>
        )}
      </DataGridCell>

      {/* Debe: visible solo para cargos (venta/multa); formateado en la moneda del bloque */}
      <DataGridCell align="right">
        {esCargo ? (
          <span className="font-bold text-slate-800 dark:text-slate-200">
            {formatCurrency(linea.charge, currency)}
          </span>
        ) : (
          <span className="text-slate-300 dark:text-slate-700">—</span>
        )}
      </DataGridCell>

      {/* Haber: visible solo para abonos (cobro/NC/saldo a favor aplicado); formateado
          en la moneda del bloque */}
      <DataGridCell align="right">
        {esAbono ? (
          <span className="font-bold text-emerald-600 dark:text-emerald-500">
            {formatCurrency(linea.credit, currency)}
          </span>
        ) : (
          <span className="text-slate-300 dark:text-slate-700">—</span>
        )}
      </DataGridCell>

      {/* Saldo corriente del bloque (calculado por el servidor): rojo si debe, verde si a favor, gris si cero */}
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
          {formatCurrency(linea.runningBalance ?? 0, currency)}
        </span>
      </DataGridCell>
    </DataGridRow>
  );
}

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
 * Estructura visual (mirroring de SupplierExtractoSection.jsx, ya aprobado):
 *   ┌── Pesos ($) ───────────────────────────────────────────────┐
 *   │  Fecha | Concepto | Comprobante | Cargo | Abono | Saldo    │
 *   └────────────────────────────────────────────────────────────┘
 *   ┌── Dólares (US$) ──────────────────────────────────────────┐
 *   │  ...                                                       │
 *   └────────────────────────────────────────────────────────────┘
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
 * Muestra cabecera con el saldo de cierre (closingBalance, calculado por el servidor)
 * y tabla de líneas. Estructura idéntica al BloqueExtractoProveedor de SupplierExtractoSection.
 */
function BloqueExtractoCliente({ bloque }) {
  const saldoCierre = bloque.closingBalance ?? 0;
  const creditoNoAplicado = bloque.unappliedCredit ?? 0;
  const nombreMoneda = bloque.currency === "USD" ? "Dólares" : "Pesos";

  return (
    <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      {/* Cabecera: badge de moneda + nombre + saldo de cierre */}
      <div className="flex items-center justify-between gap-3 border-b border-slate-100 bg-slate-50/30 px-5 py-3 dark:border-slate-800 dark:bg-slate-800/10">
        <div className="flex items-center gap-2">
          <CurrencyBadge currency={bloque.currency} />
          <span className="text-sm font-bold text-slate-700 dark:text-slate-300">
            {nombreMoneda}
          </span>
        </div>
        <div className="flex flex-wrap items-center justify-end gap-x-4 gap-y-1">
          <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400">Debe</span>
          <span
            className={`text-sm font-extrabold ${
              saldoCierre > 0
                ? "text-rose-600 dark:text-rose-500"
                : saldoCierre < 0
                ? "text-emerald-600 dark:text-emerald-500"
                : "text-slate-400 dark:text-slate-600"
            }`}
            data-testid={`extracto-saldo-${bloque.currency}`}
          >
            {formatCurrency(saldoCierre, bloque.currency)}
          </span>
          {creditoNoAplicado > 0 && (
            <>
              <span className="text-[10px] font-bold uppercase tracking-wider text-amber-600">Crédito no aplicado</span>
              <span className="text-sm font-extrabold text-amber-600" data-testid={`extracto-credito-no-aplicado-${bloque.currency}`}>
                {formatCurrency(creditoNoAplicado, bloque.currency)}
              </span>
            </>
          )}
        </div>
      </div>

      {/* Tabla de movimientos del bloque */}
      <DataGrid density="compact" minWidth="820px">
        <DataGridHeader>
          <DataGridHeaderRow>
            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
            <DataGridHeaderCell>Concepto</DataGridHeaderCell>
            <DataGridHeaderCell>Comprobante</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Cargo</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Abono</DataGridHeaderCell>
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
            <DataGridEmptyState colSpan={6} title="Sin movimientos en esta moneda." />
          )}
        </DataGridBody>
      </DataGrid>
    </div>
  );
}

// ─── Fila del extracto ────────────────────────────────────────────────────────

/**
 * Una fila del extracto del cliente.
 * `kind` ("Sale"/"Payment") solo se usa para decidir estilos (nunca se muestra como texto).
 * `sourcePublicId`/`reservaPublicId` son GUID internos: solo se usan para key/link,
 * nunca se muestran como texto — el texto visible de la reserva es `numeroReserva`.
 */
function FilaExtractoCliente({ linea, currency }) {
  const esCargo = linea.charge > 0;
  const esAbono = linea.credit > 0;

  return (
    <DataGridRow>
      {/* Fecha del movimiento */}
      <DataGridCell className="text-slate-500 dark:text-slate-400">
        {linea.date ? new Date(linea.date).toLocaleDateString("es-AR") : "—"}
      </DataGridCell>

      {/* Concepto: negrita para cargos (deuda), normal para abonos.
          Si el movimiento tiene reserva asociada, un link chiquito lleva a esa reserva
          (ahí viven las acciones puntuales: ver factura, eliminar cobro, anular recibo). */}
      <DataGridCell>
        <span className={esCargo ? "font-medium text-slate-800 dark:text-slate-200" : "text-slate-600 dark:text-slate-400"}>
          {linea.description || "—"}
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

      {/* Comprobante (nº de recibo u otra referencia); "—" si no hay */}
      <DataGridCell className="font-mono text-xs text-slate-500 dark:text-slate-400">
        {linea.documentRef || "—"}
      </DataGridCell>

      {/* Cargo: visible solo para ventas confirmadas; formateado en la moneda del bloque */}
      <DataGridCell align="right">
        {esCargo ? (
          <span className="font-bold text-slate-800 dark:text-slate-200">
            {formatCurrency(linea.charge, currency)}
          </span>
        ) : (
          <span className="text-slate-300 dark:text-slate-700">—</span>
        )}
      </DataGridCell>

      {/* Abono: visible solo para cobros; formateado en la moneda del bloque */}
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

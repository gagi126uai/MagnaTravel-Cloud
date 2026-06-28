/**
 * Solapa "Estado de cuenta" de la cuenta corriente del cliente.
 *
 * Muestra un extracto cronológico estilo libro mayor donde:
 *   - Las facturas y Notas de Débito son CARGOS (aumentan la deuda del cliente).
 *   - Los cobros y Notas de Crédito son ABONOS (reducen la deuda del cliente).
 *   - Hay UN BLOQUE POR MONEDA: el saldo en pesos y el saldo en dólares nunca
 *     se suman ni se mezclan (regla multimoneda del sistema).
 *   - Cada bloque muestra su propio saldo corriente acumulado.
 *
 * Estructura visual (mirroring de SupplierExtractoSection.jsx):
 *   ┌── EXTRACTO — Pesos ($) ──────────────────────────────────┐
 *   │  Fecha | Concepto | Comprobante/Ref. | Cargo | Abono | Saldo │
 *   └────────────────────────────────────────────────────────────┘
 *   ┌── EXTRACTO — Dólares (US$) ──────────────────────────────┐
 *   │  ...                                                      │
 *   └────────────────────────────────────────────────────────────┘
 *
 * Si solo hay movimientos en una moneda, solo se muestra ese bloque.
 *
 * Por qué la fusión es client-side:
 *   No existe un endpoint GET /customers/{id}/account/statement (a diferencia de
 *   GET /suppliers/{id}/account/statement que sí existe). Este componente carga los
 *   pagos e invoices por separado, los fusiona y agrupa por moneda en el cliente.
 *   La lógica pura de fusión está en lib/estadoCuentaCliente.js (testeable con node --test).
 *   TODO: cuando el backend cree ese endpoint, reemplazar por una llamada directa
 *   (igual que SupplierExtractoSection y EstadoCuentaExtracto de la reserva).
 *
 * Pagos cruzados (imputación multimoneda):
 *   El backend expone `imputedCurrency`/`imputedAmount` en el DTO de pago.
 *   El abono que mueve el saldo de un bloque es el imputedAmount en la imputedCurrency.
 *   Para no confundir al usuario, se muestra un texto secundario ("pagó US$ 50")
 *   cuando el efectivo recibido difiere de la moneda del saldo cancelado.
 *
 * Props:
 *   - customerPublicId: string — publicId del cliente
 *   - refreshKey: number — el padre lo incrementa al registrar/eliminar un cobro para
 *       que el extracto se recargue automáticamente
 *   - onVerFactura(invoice): abre el PDF del comprobante
 *   - onEliminarPago(payment): muestra confirmación y elimina el pago
 *   - onAnularRecibo(payment): muestra confirmación y anula el recibo de pago
 *   - onNuevaCobranza(): abre el modal para registrar un nuevo cobro
 *   - canRegistrarCobranza: boolean — si el usuario tiene permiso para registrar cobros
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { BookOpen, Eye, Loader2, Plus, RefreshCw, Trash2, XCircle } from "lucide-react";
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
import {
  construirLineas,
  ordenarLineasPorFecha,
  agruparPorMoneda,
  calcularSaldoCorrienteDeGrupo,
} from "../lib/estadoCuentaCliente";

// ─── Componente principal ─────────────────────────────────────────────────────

export function EstadoCuentaClienteTab({
  customerPublicId,
  refreshKey,
  onVerFactura,
  onEliminarPago,
  onAnularRecibo,
  onNuevaCobranza,
  canRegistrarCobranza,
}) {
  const [pagos, setPagos] = useState([]);
  const [comprobantes, setComprobantes] = useState([]);
  const [cargando, setCargando] = useState(true);
  const [error, setError] = useState(null);

  /**
   * Carga en paralelo todos los pagos y comprobantes del cliente.
   * pageSize=500 cubre el volumen típico de un cliente.
   *
   * refreshKey como dependencia: el padre lo incrementa al registrar/eliminar un cobro,
   * lo que dispara una nueva carga aquí sin que el usuario tenga que refrescar la página.
   */
  const cargarDatos = useCallback(async () => {
    setCargando(true);
    setError(null);
    try {
      const paramsPagos = new URLSearchParams({
        page: "1", pageSize: "500", sortBy: "paidAt", sortDir: "asc",
      });
      const paramsComprobantes = new URLSearchParams({
        page: "1", pageSize: "500", sortBy: "createdAt", sortDir: "asc",
      });

      // Cargamos pagos y comprobantes en paralelo para no esperar dos requests en serie
      const [resPagos, resComprobantes] = await Promise.all([
        api.get(`/customers/${customerPublicId}/account/payments?${paramsPagos.toString()}`),
        api.get(`/customers/${customerPublicId}/account/invoices?${paramsComprobantes.toString()}`),
      ]);

      setPagos(resPagos?.items ?? []);
      setComprobantes(resComprobantes?.items ?? []);
    } catch (err) {
      setError(getApiErrorMessage(err) || "No se pudo cargar el estado de cuenta.");
    } finally {
      setCargando(false);
    }
  }, [customerPublicId, refreshKey]); // eslint-disable-line react-hooks/exhaustive-deps

  // Carga al montar y cuando cambia el cliente o refreshKey
  useEffect(() => {
    cargarDatos();
  }, [cargarDatos]);

  /**
   * Construye los grupos por moneda con saldo corriente por grupo.
   * useMemo evita recalcular en cada render si los datos no cambiaron.
   *
   * Pipeline:
   *   1. construirLineas: combina pagos+comprobantes en una lista plana, cada una con currency
   *   2. ordenarLineasPorFecha: ordena cronológicamente (ASC)
   *   3. agruparPorMoneda: separa en bloques { currency, lineas[] } (ARS primero)
   *   4. calcularSaldoCorrienteDeGrupo: agrega runningBalance dentro de cada bloque
   */
  const grupos = useMemo(() => {
    const lineasCrudas = construirLineas(pagos, comprobantes);
    const lineasOrdenadas = ordenarLineasPorFecha(lineasCrudas);
    const bloques = agruparPorMoneda(lineasOrdenadas);
    return bloques.map((bloque) => ({
      currency: bloque.currency,
      lineas: calcularSaldoCorrienteDeGrupo(bloque.lineas),
    }));
  }, [pagos, comprobantes]);

  const totalLineas = grupos.reduce((acc, g) => acc + g.lineas.length, 0);

  if (cargando) {
    return (
      <div className="flex items-center justify-center gap-2 py-12 text-sm text-slate-400">
        <Loader2 className="h-5 w-5 animate-spin" />
        Cargando estado de cuenta...
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex flex-col items-center gap-3 py-12 text-center">
        <p className="text-sm text-rose-600 dark:text-rose-400">{error}</p>
        <button
          type="button"
          onClick={cargarDatos}
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
        Un bloque por moneda (ARS primero, luego USD).
        Regla multimoneda: ARS y USD nunca se mezclan ni se suman.
        Si solo hay movimientos en una moneda, solo aparece ese bloque.
      */}
      {grupos.map((grupo) => (
        <BloqueExtractoCliente
          key={grupo.currency}
          currency={grupo.currency}
          lineas={grupo.lineas}
          onVerFactura={onVerFactura}
          onEliminarPago={onEliminarPago}
          onAnularRecibo={onAnularRecibo}
        />
      ))}

      {/*
        Aclaración honesta: el saldo del extracto refleja comprobantes emitidos + cobros registrados.
        Puede diferir del resumen del encabezado si hay servicios confirmados aún sin facturar.
      */}
      {totalLineas > 0 && (
        <p className="text-[10px] text-slate-400 dark:text-slate-500 text-center">
          El saldo refleja los comprobantes emitidos y cobros registrados.
          Puede diferir del resumen superior si hay servicios confirmados aún sin facturar.
        </p>
      )}
    </div>
  );
}

// ─── Bloque de una moneda ─────────────────────────────────────────────────────

/**
 * Tabla del extracto para una moneda.
 * Muestra cabecera con el saldo de cierre y tabla de líneas.
 * Estructura idéntica al BloqueExtractoProveedor de SupplierExtractoSection.
 */
function BloqueExtractoCliente({ currency, lineas, onVerFactura, onEliminarPago, onAnularRecibo }) {
  // Saldo de cierre = balance de la última línea del bloque
  const saldoCierre = lineas.length > 0 ? lineas[lineas.length - 1].runningBalance : 0;
  const nombreMoneda = currency === "USD" ? "Dólares" : "Pesos";

  return (
    <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
      {/* Cabecera: badge de moneda + nombre + saldo de cierre */}
      <div className="flex items-center justify-between gap-3 border-b border-slate-100 bg-slate-50/30 px-5 py-3 dark:border-slate-800 dark:bg-slate-800/10">
        <div className="flex items-center gap-2">
          <CurrencyBadge currency={currency} />
          <span className="text-sm font-bold text-slate-700 dark:text-slate-300">
            {nombreMoneda}
          </span>
        </div>
        <div className="flex items-center gap-2">
          <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400">Saldo</span>
          <span
            className={`text-sm font-extrabold ${
              saldoCierre > 0
                ? "text-rose-600 dark:text-rose-500"
                : saldoCierre < 0
                ? "text-emerald-600 dark:text-emerald-500"
                : "text-slate-400 dark:text-slate-600"
            }`}
            data-testid={`extracto-saldo-${currency}`}
          >
            {formatCurrency(saldoCierre, currency)}
          </span>
        </div>
      </div>

      {/* Tabla de movimientos del bloque */}
      <DataGrid density="compact" minWidth="900px">
        <DataGridHeader>
          <DataGridHeaderRow>
            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
            <DataGridHeaderCell>Concepto</DataGridHeaderCell>
            <DataGridHeaderCell>Comprobante / Ref.</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Cargo</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Abono</DataGridHeaderCell>
            <DataGridHeaderCell align="right">Saldo</DataGridHeaderCell>
            <DataGridHeaderCell>Acciones</DataGridHeaderCell>
          </DataGridHeaderRow>
        </DataGridHeader>
        <DataGridBody>
          {lineas.length === 0 ? (
            <DataGridEmptyState colSpan={7} title="Sin movimientos en esta moneda." />
          ) : (
            lineas.map((linea, idx) => (
              <FilaExtractoCliente
                key={`${linea.kind}-${idx}`}
                linea={linea}
                currency={currency}
                onVerFactura={onVerFactura}
                onEliminarPago={onEliminarPago}
                onAnularRecibo={onAnularRecibo}
              />
            ))
          )}
        </DataGridBody>
      </DataGrid>
    </div>
  );
}

// ─── Fila del extracto ────────────────────────────────────────────────────────

/**
 * Una fila del extracto del cliente.
 * Los montos se formatean en la moneda del bloque (prop `currency`).
 * Las acciones dependen del tipo de línea: comprobante → Ver PDF; cobro → acciones de pago.
 */
function FilaExtractoCliente({ linea, currency, onVerFactura, onEliminarPago, onAnularRecibo }) {
  const esCargo = linea.charge > 0;
  const esAbono = linea.credit > 0;

  return (
    <DataGridRow>
      {/* Fecha del movimiento */}
      <DataGridCell className="text-slate-500 dark:text-slate-400">
        {linea.date ? new Date(linea.date).toLocaleDateString("es-AR") : "—"}
      </DataGridCell>

      {/* Concepto: negrita para cargos (deuda), normal para abonos.
          Para pagos cruzados (el cliente pagó en una moneda distinta a la del saldo),
          se muestra una línea secundaria sutil con el efectivo real recibido.
          Ej: "Cobro · Transferencia — R-1001" + "(pagó US$ 50)" debajo.
          El saldo del bloque usa el imputedAmount; este detalle es solo informativo. */}
      <DataGridCell>
        <span className={esCargo ? "font-medium text-slate-800 dark:text-slate-200" : "text-slate-600 dark:text-slate-400"}>
          {linea.description || "—"}
        </span>
        {linea.isCrossCurrency && linea.cashCurrency && (
          <span
            className="block text-[10px] text-slate-400 dark:text-slate-500"
            title={`Efectivo recibido en ${linea.cashCurrency}`}
          >
            pagó {formatCurrency(linea.cashAmount, linea.cashCurrency)}
          </span>
        )}
      </DataGridCell>

      {/* Número de comprobante o referencia de pago */}
      <DataGridCell className="font-mono text-xs text-slate-500 dark:text-slate-400">
        {linea.documentRef || "—"}
      </DataGridCell>

      {/* Cargo: visible solo para facturas/ND; formateado en la moneda del bloque */}
      <DataGridCell align="right">
        {esCargo ? (
          <span className="font-bold text-slate-800 dark:text-slate-200">
            {formatCurrency(linea.charge, currency)}
          </span>
        ) : (
          <span className="text-slate-300 dark:text-slate-700">—</span>
        )}
      </DataGridCell>

      {/* Abono: visible solo para cobros/NC; formateado en la moneda del bloque */}
      <DataGridCell align="right">
        {esAbono ? (
          <span className="font-bold text-emerald-600 dark:text-emerald-500">
            {formatCurrency(linea.credit, currency)}
          </span>
        ) : (
          <span className="text-slate-300 dark:text-slate-700">—</span>
        )}
      </DataGridCell>

      {/* Saldo corriente del bloque: rojo si debe, verde si a favor, gris si cero */}
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

      {/* Acciones por tipo de línea */}
      <DataGridCell>
        {linea.kind === "comprobante" ? (
          <button
            type="button"
            onClick={() => onVerFactura && onVerFactura(linea.source)}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-2 py-1 text-xs font-semibold text-slate-600 transition-colors hover:bg-slate-100 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
            aria-label={`Ver PDF de ${linea.description}`}
            data-testid={`ver-comprobante-${getPublicId(linea.source)}`}
          >
            <Eye className="h-3.5 w-3.5" />
            Ver
          </button>
        ) : linea.kind === "cobro" ? (
          <div className="flex items-center gap-1">
            {/* Anular recibo: solo si tiene recibo emitido */}
            {linea.source?.receiptPublicId && linea.source?.receiptStatus === "Issued" && (
              <button
                type="button"
                onClick={() => onAnularRecibo && onAnularRecibo(linea.source)}
                className="inline-flex rounded-lg p-1.5 text-slate-400 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-900/20 transition-colors"
                title="Anular comprobante de pago"
                aria-label="Anular comprobante de pago"
                data-testid={`anular-recibo-${getPublicId(linea.source)}`}
              >
                <XCircle className="h-3.5 w-3.5" />
              </button>
            )}
            {/* Eliminar cobro */}
            <button
              type="button"
              onClick={() => onEliminarPago && onEliminarPago(linea.source)}
              className="inline-flex rounded-lg p-1.5 text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-900/20 transition-colors"
              title="Eliminar cobro"
              aria-label="Eliminar cobro"
              data-testid={`eliminar-pago-${getPublicId(linea.source)}`}
            >
              <Trash2 className="h-3.5 w-3.5" />
            </button>
          </div>
        ) : (
          <span className="text-xs text-slate-400">—</span>
        )}
      </DataGridCell>
    </DataGridRow>
  );
}

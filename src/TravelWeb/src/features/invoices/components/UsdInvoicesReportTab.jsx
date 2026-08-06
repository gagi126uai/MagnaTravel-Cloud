/**
 * Solapa "Facturas en dólares" de Reportes (spec firmada 2026-08-06, Parte B).
 *
 * Muestra, para el período elegido en la barra de fechas de Reportes (la misma que
 * ya usan las otras solapas — no se agrega un segundo selector), una fila por cada
 * factura de venta emitida en moneda extranjera: cuánto dice el comprobante en
 * pesos y cuánto entró realmente cobrado contra esa factura. Esa diferencia es
 * NORMAL (se cobra a un dólar y se factura al techo del día) y el contador la
 * necesita ordenada — el vendedor común no llega hasta acá: toda la pantalla de
 * Reportes ya está detrás del permiso `reportes.view` (ver App.jsx).
 *
 * Solo lectura: no hay ningún botón de acción por fila, solo los dos links
 * (comprobante y reserva) que llevan a donde esos datos ya viven.
 *
 * "Exportar Excel" NO tiene botón propio acá (fix bloqueante, review post-
 * implementación): la spec dice literal "reusa el botón que la pantalla ya
 * tiene arriba" — ReportsPage.jsx es quien decide, según la solapa activa, si
 * ese botón exporta el reporte combinado o este reporte de dólares.
 */
import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { RefreshCw } from "lucide-react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatCurrency, formatDate } from "../../../lib/utils";
import {
  DataGrid,
  DataGridBody,
  DataGridCell,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { SkeletonTable } from "../../../components/ui/skeleton";
import { formatMontoOGuion, formatDiferenciaConSigno, derivarVistaUsdInvoicesReport } from "../lib/usdInvoicesReport";

// Helper de formato local: los pesos reusan formatCurrency (mismo formato que el
// resto de Reportes); el tipo de cambio de la factura NO es un monto de moneda —
// es una tasa — así que se formatea como número simple, sin símbolo.
const formatearPesos = (valor) => formatCurrency(valor, "ARS");
const formatearTipoDeCambio = (valor) =>
  Number(valor).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/**
 * Arma el HTML de la pestaña de previsualización del comprobante (mismo patrón
 * EXACTO que `CustomerAccountPage.jsx` / `FacturacionPage.jsx`: se copia acá para
 * no acoplar esta solapa de Reportes a esas pantallas — son funciones puras sin
 * React, así que copiarlas no arrastra ningún estado ajeno).
 */
const escapeHtml = (value) =>
  String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");

const renderComprobantePreview = (previewWindow, { title, body }) => {
  if (!previewWindow || previewWindow.closed) return;
  previewWindow.document.open();
  previewWindow.document.write(`<!doctype html><html lang="es"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" /><title>${escapeHtml(title)}</title><style>:root{color-scheme:light;font-family:Inter,system-ui,sans-serif;background:#e2e8f0;color:#0f172a}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:linear-gradient(180deg,#f8fafc 0%,#e2e8f0 100%)}.shell{min-height:100vh;display:flex;flex-direction:column}.header{padding:16px 20px;border-bottom:1px solid #cbd5e1;background:rgba(255,255,255,.96);backdrop-filter:blur(10px)}.eyebrow{margin:0 0 6px;font-size:11px;font-weight:800;letter-spacing:.14em;text-transform:uppercase;color:#4f46e5}.title{margin:0;font-size:20px;font-weight:700}.subtitle{margin:6px 0 0;font-size:14px;color:#475569}.content{flex:1;padding:20px}.panel{height:calc(100vh - 117px);border:1px solid #cbd5e1;border-radius:18px;overflow:hidden;background:#fff;box-shadow:0 20px 50px rgba(15,23,42,.15)}.state{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:12px;padding:24px;text-align:center}.state-title{margin:0;font-size:18px;font-weight:700}.state-text{margin:0;max-width:480px;color:#475569;line-height:1.5}.spinner{width:42px;height:42px;border:4px solid #cbd5e1;border-top-color:#4f46e5;border-radius:999px;animation:spin .9s linear infinite}iframe{width:100%;height:100%;border:0;background:#fff}@keyframes spin{to{transform:rotate(360deg)}}</style></head><body>${body}</body></html>`);
  previewWindow.document.close();
};

export function UsdInvoicesReportTab({ dateFrom, dateTo }) {
  const [filas, setFilas] = useState([]);
  const [totales, setTotales] = useState(null);
  const [cargando, setCargando] = useState(true);
  const [error, setError] = useState(null);

  const cargarReporte = useCallback(async () => {
    setCargando(true);
    setError(null);
    try {
      const respuesta = await api.get(`/reports/usd-invoices?from=${dateFrom}&to=${dateTo}`);
      setFilas(respuesta?.filas ?? []);
      setTotales(respuesta?.totales ?? null);
    } catch (err) {
      setError(getApiErrorMessage(err) || "No se pudieron cargar las facturas en dólares.");
    } finally {
      setCargando(false);
    }
  }, [dateFrom, dateTo]);

  useEffect(() => {
    cargarReporte();
  }, [cargarReporte]);

  /**
   * Abre el comprobante en una pestaña nueva. Fix bloqueante (review post-
   * implementación): la ventana se abre EN EL MISMO TICK del click (síncrono, sin
   * await antes) — si se abre recién después de que resuelve el fetch, el
   * navegador la trata como un popup no pedido por el usuario y la bloquea sin
   * ningún aviso. Mismo patrón que `CustomerAccountPage.handleOpenInvoicePreview`:
   * primero un shell con spinner, después se reemplaza por el PDF (o el aviso de
   * error) cuando la respuesta llega.
   */
  const handleAbrirComprobante = async (fila) => {
    const previewWindow = window.open("", "_blank");
    if (!previewWindow) {
      showError("El navegador bloqueó la apertura de la factura.");
      return;
    }
    previewWindow.opener = null;

    const titulo = fila.comprobante;

    renderComprobantePreview(previewWindow, {
      title: titulo,
      body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(titulo)}</h1><p class="subtitle">Preparando la factura para mostrarla en esta pestaña.</p></div><div class="content"><div class="panel"><div class="state"><div class="spinner"></div><p class="state-title">Cargando factura...</p></div></div></div></div>`,
    });

    try {
      const blob = await api.get(`/invoices/${fila.comprobanteId}/pdf`, { responseType: "blob" });
      if (!(blob instanceof Blob) || blob.size === 0) throw new Error("La factura no devolvió un PDF válido.");

      const pdfUrl = URL.createObjectURL(blob);
      // Mismo precedente: el objectURL se revoca cuando el usuario cierra la
      // pestaña de la vista previa, no antes (si no, el iframe se queda sin nada
      // que mostrar apenas termina de cargar).
      const releaseTimer = window.setInterval(() => {
        if (previewWindow.closed) { URL.revokeObjectURL(pdfUrl); window.clearInterval(releaseTimer); }
      }, 1000);

      renderComprobantePreview(previewWindow, {
        title: titulo,
        body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(titulo)}</h1><p class="subtitle">Vista del comprobante emitido.</p></div><div class="content"><div class="panel"><iframe src="${pdfUrl}" title="${escapeHtml(titulo)}"></iframe></div></div></div>`,
      });
    } catch {
      // No se interpola el error crudo del backend: texto fijo en español, mismo
      // criterio que el precedente (gate de exposición de datos internos).
      renderComprobantePreview(previewWindow, {
        title: titulo,
        body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(titulo)}</h1><p class="subtitle">No fue posible abrir el comprobante.</p></div><div class="content"><div class="panel"><div class="state"><p class="state-title">No se pudo cargar el comprobante</p><p class="state-text">No se pudo abrir el comprobante. Probá de nuevo en un momento.</p></div></div></div></div>`,
      });
    }
  };

  const { hayFilas, totalesFormateados } = derivarVistaUsdInvoicesReport({ filas, totales }, formatearPesos);

  return (
    <div className="space-y-4">
      {/* Cargando: mismo esqueleto gris de tabla que ya usan las otras solapas de Reportes. */}
      {cargando && <SkeletonTable rows={5} cols={8} />}

      {/* Error: mismo aviso + reintentar que usa la solapa de Facturación del cliente
          (la otra pantalla de Reportes/Cuenta corriente que ya resuelve este estado).
          role="alert" (item 9): un error de carga tiene que anunciarse solo a quien
          usa lector de pantalla, no solo verse en rojo. */}
      {!cargando && error && (
        <div className="flex flex-col items-center gap-3 py-12 text-center" role="alert" data-testid="usd-invoices-error">
          <p className="text-sm text-rose-600 dark:text-rose-400">{error}</p>
          <button
            type="button"
            onClick={cargarReporte}
            className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
          >
            <RefreshCw className="h-3.5 w-3.5" />
            Reintentar
          </button>
        </div>
      )}

      {!cargando && !error && (
        <>
          {/* Tabla de escritorio */}
          <DataGrid density="compact" minWidth="1080px">
            <DataGridHeader>
              <DataGridHeaderRow>
                <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                <DataGridHeaderCell>Comprobante</DataGridHeaderCell>
                <DataGridHeaderCell>Cliente</DataGridHeaderCell>
                <DataGridHeaderCell align="right">Moneda extranjera</DataGridHeaderCell>
                <DataGridHeaderCell align="right">TC Factura</DataGridHeaderCell>
                <DataGridHeaderCell align="right">Pesos de la factura</DataGridHeaderCell>
                <DataGridHeaderCell align="right">Pesos cobrados</DataGridHeaderCell>
                <DataGridHeaderCell align="right">Diferencia</DataGridHeaderCell>
              </DataGridHeaderRow>
            </DataGridHeader>
            <DataGridBody>
              {!hayFilas ? (
                // "Sin dibujo ni botón" (spec): a propósito NO se usa DataGridEmptyState/
                // ListEmptyState acá — esos siempre dibujan un ícono, y la spec pide un
                // renglón de texto gris, nada más.
                <tr>
                  <td
                    colSpan={8}
                    className="py-8 text-center text-sm text-muted-foreground"
                    data-testid="usd-invoices-vacio"
                  >
                    No hay facturas en dólares en este período.
                  </td>
                </tr>
              ) : (
                filas.map((fila) => (
                  <DataGridRow key={fila.comprobanteId} data-testid={`usd-invoices-fila-${fila.comprobanteId}`}>
                    <DataGridCell>{formatDate(fila.fecha)}</DataGridCell>
                    <DataGridCell>
                      <button
                        type="button"
                        onClick={() => handleAbrirComprobante(fila)}
                        className="text-left font-mono font-semibold text-indigo-600 hover:underline dark:text-indigo-400"
                        data-testid={`ver-comprobante-${fila.comprobanteId}`}
                      >
                        {fila.comprobante}
                      </button>
                      {fila.numeroReserva && fila.reservaId && (
                        <div className="mt-0.5">
                          <Link
                            to={`/reservas/${fila.reservaId}`}
                            className="text-xs text-slate-500 hover:underline dark:text-slate-400"
                          >
                            {fila.numeroReserva}
                          </Link>
                        </div>
                      )}
                    </DataGridCell>
                    <DataGridCell>{fila.cliente}</DataGridCell>
                    {/* Item 9 (fix): la moneda extranjera de la fila es la que manda el
                        backend (fila.moneda) — antes esto estaba hardcodeado a "USD" y una
                        factura en euros se hubiera mostrado con el signo de dólar. */}
                    <DataGridCell align="right">
                      {formatCurrency(fila.montoEnMonedaExtranjera, fila.moneda)}
                    </DataGridCell>
                    <DataGridCell align="right">{formatearTipoDeCambio(fila.tipoCambioFactura)}</DataGridCell>
                    <DataGridCell align="right" className="font-semibold text-slate-900 dark:text-white">
                      {formatearPesos(fila.pesosDeLaFactura)}
                    </DataGridCell>
                    <DataGridCell align="right">{formatMontoOGuion(fila.pesosCobrados, formatearPesos)}</DataGridCell>
                    <DataGridCell align="right">{formatDiferenciaConSigno(fila.diferencia, formatearPesos)}</DataGridCell>
                  </DataGridRow>
                ))
              )}
            </DataGridBody>
            {hayFilas && totalesFormateados && (
              <tfoot className="border-t border-slate-200 bg-slate-50/60 dark:border-slate-800 dark:bg-slate-950/70">
                <tr data-testid="usd-invoices-totales">
                  <td
                    colSpan={5}
                    className="px-4 py-3 text-right text-[11px] font-bold uppercase tracking-[0.14em] text-slate-500 dark:text-slate-400"
                  >
                    Total del período
                  </td>
                  <td className="px-4 py-3 text-right font-bold text-slate-900 dark:text-white">
                    {totalesFormateados.pesosDeLaFactura}
                  </td>
                  <td className="px-4 py-3 text-right font-bold text-slate-900 dark:text-white">
                    {totalesFormateados.pesosCobrados}
                  </td>
                  <td className="px-4 py-3 text-right font-bold text-slate-900 dark:text-white">
                    {totalesFormateados.diferencia}
                  </td>
                </tr>
              </tfoot>
            )}
          </DataGrid>

          {/* Cards de mobile (la tabla se oculta debajo de md, ver DataGrid) */}
          {!hayFilas ? (
            <p className="py-8 text-center text-sm text-muted-foreground md:hidden" data-testid="usd-invoices-vacio-mobile">
              No hay facturas en dólares en este período.
            </p>
          ) : (
            <MobileRecordList>
              {filas.map((fila) => (
                <MobileRecordCard
                  key={fila.comprobanteId}
                  title={fila.comprobante}
                  subtitle={fila.cliente}
                  meta={
                    <>
                      <div className="text-xs text-slate-500 dark:text-slate-400">
                        {formatDate(fila.fecha)} · {formatCurrency(fila.montoEnMonedaExtranjera, fila.moneda)} · TC {formatearTipoDeCambio(fila.tipoCambioFactura)}
                      </div>
                      <div className="text-xs text-slate-500 dark:text-slate-400">
                        Pesos de la factura {formatearPesos(fila.pesosDeLaFactura)} · Cobrado {formatMontoOGuion(fila.pesosCobrados, formatearPesos)}
                      </div>
                      <div className="text-xs text-slate-500 dark:text-slate-400">
                        Diferencia {formatDiferenciaConSigno(fila.diferencia, formatearPesos)}
                      </div>
                    </>
                  }
                  footerActions={
                    <div className="flex items-center gap-3">
                      <button
                        type="button"
                        onClick={() => handleAbrirComprobante(fila)}
                        className="text-xs font-semibold text-indigo-600 hover:underline dark:text-indigo-400"
                      >
                        Ver comprobante
                      </button>
                      {fila.numeroReserva && fila.reservaId && (
                        <Link
                          to={`/reservas/${fila.reservaId}`}
                          className="text-xs font-semibold text-slate-600 hover:underline dark:text-slate-300"
                        >
                          {fila.numeroReserva}
                        </Link>
                      )}
                    </div>
                  }
                />
              ))}
            </MobileRecordList>
          )}
        </>
      )}
    </div>
  );
}

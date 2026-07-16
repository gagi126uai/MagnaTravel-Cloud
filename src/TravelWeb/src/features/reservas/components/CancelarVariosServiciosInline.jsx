/**
 * Sección inline "Anular varios servicios" dentro del detalle de reserva.
 *
 * Aparece debajo de la lista de servicios (no es modal) al presionar el botón
 * "Anular varios". Permite tildar múltiples servicios anulables de una vez,
 * con un único motivo y mostrando el total a devolver al cliente POR MONEDA.
 *
 * Vocabulario (regla del dueño, 2026-07-16): "Anular" es la palabra correcta para
 * dejar un servicio sin efecto ("Cancelar" en este producto significa que el cliente
 * pagó el total). Los textos que ve el usuario en este componente dicen "Anular";
 * el nombre del componente y sus identificadores internos (endpoint cancel-service,
 * handleConfirmar, etc.) se mantienen como están, son solo código.
 *
 * Flujo:
 *   1. El usuario tilda los servicios que quiere anular (de distintos operadores OK).
 *   2. Escribe un motivo único para toda la tanda (mín 10 caracteres).
 *   3. Presiona "Confirmar" → se llama POST /cancellations/cancel-service UNO POR UNO
 *      de forma secuencial (no hay endpoint batch).
 *   4. Al terminar (éxito total, parcial o fallo total), la sección NO se cierra sola.
 *      Se muestra el resultado por servicio: cuáles se anularon OK y cuáles fallaron
 *      con el motivo del error. El usuario lee y cierra él mismo con el botón "Cerrar".
 *   5. Si vuelve 409 (bloqueo fiscal), se muestra el cartel de bloqueo (no toast).
 *   6. Al terminar se recarga la reserva (via onCancelacionTerminada), para que los
 *      servicios anulados OK desaparezcan de la lista del padre.
 *
 * Decisión UX: NUNCA cerrar automáticamente si hubo algún fallo.
 *   El caso de éxito PARCIAL es justo cuando el usuario MÁS necesita saber qué quedó
 *   sin anular. Cerrar sola en ese caso haría creer que todo salió bien.
 *   Por consistencia, tampoco cerramos sola en éxito total: el usuario lee el
 *   resumen verde y cierra cuando esté listo.
 *
 * Bloqueo fiscal a nivel reserva:
 *   Si reserva.serviceCancellationBlockReason != null, NINGÚN servicio se puede anular:
 *   - Los checkboxes aparecen deshabilitados.
 *   - Se muestra el motivo del bloqueo arriba del listado.
 *   - El botón "Confirmar" queda deshabilitado.
 *   (Es all-or-nothing porque el candado es de toda la reserva, no por servicio.)
 *
 * Regla multimoneda DURA: NUNCA se suman pesos con dólares.
 * El total a devolver se agrupa y muestra POR MONEDA.
 *
 * Factura de la devolución (2026-07-16): si hay 2+ facturas activas y TODOS los
 * servicios tildados están adentro de la MISMA única factura (según
 * InvoiceDto.ServicePublicIds), se preselecciona sola en el desplegable con un
 * texto aclaratorio — ver sugerirFacturaParaServicios en lib/serviceInvoiceMatch.js.
 * El usuario siempre puede cambiarla a mano.
 *
 * Props:
 *   serviciosCancelables   — array de servicios candidatos (los "con proveedor" activos)
 *   reservaPublicId        — GUID de la reserva
 *   blockReason            — string|null; si no es null, toda la reserva está bloqueada
 *   onCerrar               — callback: el usuario cerró la sección (botón Cerrar / X)
 *   onCancelacionTerminada — callback: el proceso terminó y hubo al menos un éxito;
 *                            el padre debe recargar los datos (sin cerrar la sección)
 */

import { useState, useRef, useEffect, useMemo, useCallback } from "react";
import { X, Loader2, Ban, CheckCircle2, AlertTriangle } from "lucide-react";
import { cancellationsApi } from "../../cancellations/api/cancellationsApi";
import { getReservationServicePublicId } from "../lib/reservationServiceModel";
import { sugerirFacturaParaServicios } from "../lib/serviceInvoiceMatch";
import { formatCurrency } from "../../../lib/utils";

// Mínimo de caracteres para el motivo (igual que en ModalBorrarVsCancelar del ServiceList).
const MOTIVO_MIN_CHARS = 10;

// Mapeo de recordKind (front) a serviceTable (backend CancelServiceRequest).
// Verificado contra useReservaDetail.js handleCancelService.
const RECORD_KIND_TO_SERVICE_TABLE = {
  hotel: "Hotel",
  flight: "Flight",
  transfer: "Transfer",
  package: "Package",
  assistance: "Assistance",
  generic: "Generic",
};

/**
 * Calcula el total de salePrice por moneda para los servicios seleccionados.
 * Regla dura: NO mezcla monedas distintas en un mismo número.
 *
 * @param {Array} servicios - Lista de servicios con salePrice y currency
 * @returns {Object} - ej. { ARS: 50000, USD: 300 }
 */
function calcularTotalPorMoneda(servicios) {
  return servicios.reduce((acumulador, svc) => {
    const moneda = svc.currency || "ARS";
    acumulador[moneda] = (acumulador[moneda] || 0) + (svc.salePrice || 0);
    return acumulador;
  }, {});
}

export function CancelarVariosServiciosInline({
  serviciosCancelables,
  reservaPublicId,
  saleInvoices = [],
  blockReason,
  onCerrar,
  onCancelacionTerminada,
}) {
  // Conjunto de publicIds de servicios tildados por el usuario.
  const [seleccionados, setSeleccionados] = useState(new Set());

  // Texto del motivo único para toda la tanda.
  const [motivo, setMotivo] = useState("");
  const [targetInvoicePublicId, setTargetInvoicePublicId] = useState(
    saleInvoices.length === 1 ? saleInvoices[0].publicId : ""
  );

  // Estado del proceso de cancelación secuencial.
  // null = inactivo; objeto cuando está corriendo o terminó.
  const [procesoEstado, setProcesoEstado] = useState(null);
  // {
  //   enProceso: bool,
  //   resultados: [{ svc, ok: bool, mensajeError?: string, esBloqueo409: bool }],
  // }

  const motivoRef = useRef(null);

  // Al montar la sección inline, el foco va al textarea del motivo para que el
  // usuario pueda empezar a escribir sin tener que hacer clic manualmente.
  useEffect(() => {
    if (motivoRef.current) {
      motivoRef.current.focus();
    }
  // useEffect con [] corre solo al montar. La ref es estable.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const motivoValido = motivo.trim().length >= MOTIVO_MIN_CHARS;

  // Lista de servicios seleccionados (objetos completos, para calcular el total).
  const serviciosSeleccionados = serviciosCancelables.filter((svc) =>
    seleccionados.has(getReservationServicePublicId(svc))
  );

  // Total a devolver por moneda (solo de los seleccionados).
  // Regla dura: nunca mezclar monedas.
  const totalPorMoneda = calcularTotalPorMoneda(serviciosSeleccionados);
  const monedasConTotal = Object.entries(totalPorMoneda);

  // Trazabilidad de origen (2026-07-16): si TODOS los servicios tildados están en
  // los renglones de una única factura activa, la sugerimos sola. useMemo porque
  // seleccionados (el Set de tildes) cambia en cada click y no queremos recalcular
  // el matching contra saleInvoices en cada render que no toque la selección.
  const publicIdFacturaSugerida = useMemo(
    () => sugerirFacturaParaServicios(Array.from(seleccionados), saleInvoices),
    [seleccionados, saleInvoices]
  );

  // Preseleccionamos la factura sugerida cada vez que cambia (por ejemplo, el
  // usuario tilda otro servicio y ahora todos coinciden con una sola factura).
  // Si deja de haber una única coincidencia (sugerencia null), NO borramos lo que
  // el usuario ya haya elegido a mano — solo dejamos de imponer la sugerencia.
  useEffect(() => {
    if (publicIdFacturaSugerida) {
      setTargetInvoicePublicId(publicIdFacturaSugerida);
    }
  }, [publicIdFacturaSugerida]);

  const handleToggleServicio = useCallback((publicId) => {
    setSeleccionados((prev) => {
      const next = new Set(prev);
      if (next.has(publicId)) {
        next.delete(publicId);
      } else {
        next.add(publicId);
      }
      return next;
    });
  }, []);

  const handleSeleccionarTodos = useCallback(() => {
    if (seleccionados.size === serviciosCancelables.length) {
      // Si ya están todos seleccionados, deseleccionamos todo.
      setSeleccionados(new Set());
    } else {
      const todos = new Set(serviciosCancelables.map(getReservationServicePublicId));
      setSeleccionados(todos);
    }
  }, [seleccionados.size, serviciosCancelables]);

  /**
   * Confirma la cancelación: llama al backend UNO POR UNO de forma secuencial.
   * No hay endpoint batch — el diseño intencional del ADR-025.
   *
   * Si alguno falla, registramos el error pero continuamos con los demás.
   * El usuario ve cuáles se cancelaron OK y cuáles fallaron.
   */
  const handleConfirmar = useCallback(async () => {
    if (serviciosSeleccionados.length === 0 || !motivoValido || blockReason) return;

    setProcesoEstado({ enProceso: true, resultados: [] });

    const resultados = [];

    for (const svc of serviciosSeleccionados) {
      const serviceTable = RECORD_KIND_TO_SERVICE_TABLE[svc.recordKind];
      const servicePublicId = getReservationServicePublicId(svc);

      if (!serviceTable || !servicePublicId) {
        resultados.push({
          svc,
          ok: false,
          mensajeError: "Tipo de servicio no reconocido.",
          esBloqueo409: false,
        });
        continue;
      }

      try {
        await cancellationsApi.cancelService({
          reservaPublicId,
          serviceTable,
          servicePublicId,
          reason: motivo.trim(),
          ...(targetInvoicePublicId ? { targetInvoicePublicId } : {}),
          ...(Number(svc.salePrice) > 0 ? { confirmedGrossCreditAmount: Number(svc.salePrice) } : {}),
        });
        resultados.push({ svc, ok: true, esBloqueo409: false });
      } catch (error) {
        // 409 = bloqueo fiscal (factura con CAE viva o voucher emitido).
        // Lo mostramos de forma distinta al resto de errores.
        // El cliente api.js (fetch nativo) pone el status en error.status directamente.
        const esBloqueo409 = error?.status === 409;
        const mensajeError = extraerMensajeError(error, "No se pudo anular este servicio.");
        resultados.push({ svc, ok: false, mensajeError, esBloqueo409 });
      }

      // Actualizamos resultados en tiempo real para que el usuario vea el progreso.
      setProcesoEstado({ enProceso: true, resultados: [...resultados] });
    }

    setProcesoEstado({ enProceso: false, resultados });

    // Si al menos uno se canceló con éxito, le avisamos al padre para que recargue
    // los datos de la reserva (el servicio cancelado OK desaparece de la lista).
    // IMPORTANTE: NO cerramos la sección aquí — el usuario necesita leer el resultado,
    // especialmente en éxito parcial donde algún servicio quedó sin cancelar.
    const hayExitos = resultados.some((r) => r.ok);
    if (hayExitos) {
      onCancelacionTerminada();
    }
    // La sección sigue visible con procesoEstado.enProceso = false.
    // El usuario lee el resultado y cierra él mismo con el botón "Cerrar".
  }, [serviciosSeleccionados, motivoValido, blockReason, reservaPublicId, motivo, onCancelacionTerminada, targetInvoicePublicId]);

  const procesoTerminado = procesoEstado && !procesoEstado.enProceso;

  // Usamos la función pura clasificarResultadoFinal para determinar el resultado.
  // Si el proceso no terminó aún, la clasificación es vacía (valores en false/0).
  const { todosOk, algunoFallo } = procesoTerminado
    ? clasificarResultadoFinal(procesoEstado.resultados)
    : { todosOk: false, algunoFallo: false };

  // La sección está bloqueada por candado fiscal de la reserva (all-or-nothing).
  const estaBloqueada = Boolean(blockReason);

  return (
    <section
      data-testid="seccion-cancelar-varios"
      className="rounded-2xl border border-amber-200 bg-amber-50 dark:border-amber-900/40 dark:bg-amber-950/20"
      aria-label="Anular varios servicios"
    >
      {/* ─ Header de la sección ─────────────────────────────────────────── */}
      <div className="flex items-center justify-between border-b border-amber-200 px-6 py-4 dark:border-amber-900/40">
        <h3 className="font-bold text-slate-900 dark:text-white text-base">
          Anular varios servicios
        </h3>
        <button
          type="button"
          onClick={onCerrar}
          disabled={procesoEstado?.enProceso}
          aria-label="Cerrar sección de anular varios"
          className="text-slate-400 hover:text-slate-600 transition-colors disabled:opacity-50 dark:hover:text-slate-200"
        >
          <X className="h-5 w-5" />
        </button>
      </div>

      <div className="p-6 space-y-5">
        {/* ─ Bloqueo fiscal a nivel reserva ─────────────────────────────── */}
        {estaBloqueada && (
          <div
            className="flex items-start gap-3 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 dark:border-rose-900/40 dark:bg-rose-950/20"
            role="alert"
            data-testid="bloqueo-cancelacion-reserva"
          >
            <Ban className="mt-0.5 h-4 w-4 flex-shrink-0 text-rose-600 dark:text-rose-400" />
            <div className="space-y-1 text-sm">
              <p className="font-bold text-rose-800 dark:text-rose-200">
                No se puede anular: la reserva tiene un bloqueo fiscal activo
              </p>
              <p className="text-rose-700 dark:text-rose-300">{blockReason}</p>
              <p className="text-xs text-rose-600 dark:text-rose-400">
                Para anular servicios, primero hay que resolver la factura o el voucher que genera el bloqueo.
              </p>
            </div>
          </div>
        )}

        {/* ─ Lista de servicios cancelables con checkboxes ─────────────── */}
        {serviciosCancelables.length === 0 ? (
          <p className="text-sm text-slate-500 dark:text-slate-400">
            No hay servicios que se puedan anular en esta reserva.
          </p>
        ) : (
          <div className="space-y-2">
            <div className="flex items-center justify-between">
              <span className="text-xs font-semibold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                Servicios a anular
              </span>
              {/* Atajo seleccionar/deseleccionar todos */}
              {!estaBloqueada && !procesoEstado?.enProceso && !procesoTerminado && (
                <button
                  type="button"
                  onClick={handleSeleccionarTodos}
                  className="text-xs text-indigo-600 hover:underline dark:text-indigo-400"
                >
                  {seleccionados.size === serviciosCancelables.length ? "Deseleccionar todos" : "Seleccionar todos"}
                </button>
              )}
            </div>

            <div className="divide-y divide-amber-100 rounded-xl border border-amber-200 bg-white dark:divide-amber-900/30 dark:border-amber-900/40 dark:bg-slate-900">
              {serviciosCancelables.map((svc) => {
                const publicId = getReservationServicePublicId(svc);
                const estaSeleccionado = seleccionados.has(publicId);
                const estaCancelando = procesoEstado?.enProceso && estaSeleccionado;

                // Resultado individual cuando el proceso ya terminó
                const resultadoItem = procesoTerminado
                  ? procesoEstado.resultados.find((r) => getReservationServicePublicId(r.svc) === publicId)
                  : null;

                return (
                  <label
                    key={publicId}
                    className={`flex items-center gap-3 px-4 py-3 cursor-pointer transition-colors ${
                      estaBloqueada || procesoEstado?.enProceso || procesoTerminado
                        ? "cursor-not-allowed"
                        : "hover:bg-amber-50/80 dark:hover:bg-amber-900/10"
                    }`}
                    data-testid={`checkbox-servicio-${publicId}`}
                  >
                    <input
                      type="checkbox"
                      checked={estaSeleccionado}
                      onChange={() => handleToggleServicio(publicId)}
                      disabled={estaBloqueada || Boolean(procesoEstado?.enProceso) || procesoTerminado}
                      className="h-4 w-4 rounded border-slate-300 text-amber-600 focus:ring-amber-500 disabled:opacity-50"
                      aria-label={`Seleccionar servicio ${svc.name}`}
                    />

                    {/* Nombre e info del servicio */}
                    <div className="flex-1 min-w-0">
                      <span className="text-sm font-semibold text-slate-900 dark:text-white line-clamp-1">
                        {svc.name}
                      </span>
                      <div className="flex flex-wrap items-center gap-x-3 gap-y-0.5 mt-0.5">
                        <span className="text-xs text-slate-500 dark:text-slate-400">
                          {svc.displayType || svc.recordKind}
                        </span>
                        {svc.supplierName && (
                          <span className="text-xs text-slate-400 dark:text-slate-500">
                            {svc.supplierName}
                          </span>
                        )}
                      </div>
                    </div>

                    {/* Precio de venta con moneda (base del total a devolver) */}
                    <span className="text-sm font-bold text-slate-800 dark:text-slate-200 font-mono whitespace-nowrap">
                      {formatCurrency(svc.salePrice || 0, svc.currency || "ARS")}
                      {/* Moneda explicita para que quede claro en reservas multimoneda */}
                      {svc.currency && svc.currency !== "ARS" && (
                        <span className="ml-1 text-xs font-normal text-slate-500">{svc.currency}</span>
                      )}
                    </span>

                    {/* Indicador de resultado por servicio (durante y tras el proceso) */}
                    {estaCancelando && (
                      <Loader2 className="h-4 w-4 animate-spin text-amber-500 flex-shrink-0" />
                    )}
                    {resultadoItem?.ok && (
                      <CheckCircle2 className="h-4 w-4 text-emerald-500 flex-shrink-0" aria-label="Anulado OK" />
                    )}
                    {resultadoItem && !resultadoItem.ok && (
                      <span
                        className="text-xs text-rose-600 dark:text-rose-400 flex-shrink-0"
                        title={resultadoItem.mensajeError}
                      >
                        ✗ Error
                      </span>
                    )}
                  </label>
                );
              })}
            </div>
          </div>
        )}

        {/* ─ Total por moneda (se actualiza al tildar) ─────────────────── */}
        {serviciosSeleccionados.length > 0 && !procesoTerminado && (
          <div className="rounded-xl border border-amber-200 bg-amber-100/60 px-4 py-3 dark:border-amber-900/40 dark:bg-amber-950/30">
            <p className="text-xs font-semibold uppercase tracking-wider text-amber-700 dark:text-amber-300 mb-1">
              Total a devolver al cliente
            </p>
            {monedasConTotal.length === 0 ? (
              <span className="text-sm text-slate-500">—</span>
            ) : (
              // Regla dura multimoneda: una línea por moneda, nunca mezcladas.
              <div className="flex flex-wrap gap-x-4 gap-y-0.5">
                {monedasConTotal.map(([moneda, total]) => (
                  <span key={moneda} className="text-base font-black text-slate-900 dark:text-white font-mono">
                    {formatCurrency(total, moneda)}
                    {" "}
                    <span className="text-xs font-bold text-amber-700 dark:text-amber-300">{moneda}</span>
                  </span>
                ))}
              </div>
            )}
            <p className="text-[10px] text-slate-500 dark:text-slate-400 mt-1">
              Suma de precios de venta de los servicios seleccionados
              {monedasConTotal.length > 1 && " · Monedas separadas (no se mezclan)"}
            </p>
          </div>
        )}

        {saleInvoices.length > 1 && serviciosSeleccionados.length > 0 && !procesoTerminado && (
          <div>
            <label htmlFor="factura-destino-cancelar-varios" className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
              Factura de estas devoluciones
            </label>
            <select
              id="factura-destino-cancelar-varios"
              value={targetInvoicePublicId}
              onChange={(e) => setTargetInvoicePublicId(e.target.value)}
              disabled={Boolean(procesoEstado?.enProceso)}
              className="w-full rounded-lg border border-amber-200 bg-white px-3 py-2 text-sm dark:border-amber-700 dark:bg-slate-800 dark:text-white"
              data-testid="select-factura-cancelar-varios"
              aria-describedby={
                // Accesibilidad: el lector de pantalla anuncia la aclaración de la
                // sugerencia como descripción del propio desplegable.
                publicIdFacturaSugerida && targetInvoicePublicId === publicIdFacturaSugerida
                  ? "hint-factura-sugerida-cancelar-varios"
                  : undefined
              }
            >
              <option value="">Elegí una factura</option>
              {saleInvoices.map((invoice) => <option key={invoice.publicId} value={invoice.publicId}>{invoice.label}</option>)}
            </select>
            {/* Solo mientras la selección actual siga siendo la sugerida: si el
                usuario elige otra factura a mano, el texto deja de aplicar. */}
            {publicIdFacturaSugerida && targetInvoicePublicId === publicIdFacturaSugerida && (
              <p id="hint-factura-sugerida-cancelar-varios" className="mt-1 text-xs text-emerald-600 dark:text-emerald-400" data-testid="hint-factura-sugerida">
                {serviciosSeleccionados.length === 1
                  ? "Este servicio está incluido en esta factura."
                  : "Estos servicios están incluidos en esta factura."}
              </p>
            )}
            <p className="mt-1 text-xs text-slate-500">Si los servicios corresponden a facturas distintas, anulalos en tandas separadas.</p>
          </div>
        )}

        {/* ─ Resultado del proceso (cuando ya terminó) ─────────────────── */}
        {procesoTerminado && (
          <div
            className={`rounded-xl border px-4 py-3 text-sm ${
              todosOk
                ? "border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-900/40 dark:bg-emerald-950/20 dark:text-emerald-200"
                : algunoFallo
                ? "border-amber-200 bg-amber-50 text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-200"
                : "border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-200"
            }`}
            role="status"
            aria-live="polite"
          >
            {todosOk ? (
              <p className="font-bold">
                Todos los servicios se anularon correctamente. La reserva se actualizó.
              </p>
            ) : (
              <>
                <p className="font-bold mb-2">
                  {procesoEstado.resultados.filter((r) => r.ok).length} de{" "}
                  {procesoEstado.resultados.length} servicios anulados.
                </p>
                {procesoEstado.resultados
                  .filter((r) => !r.ok)
                  .map((r, i) => (
                    <div key={i} className="flex items-start gap-2 text-xs mt-1">
                      <AlertTriangle className="h-3.5 w-3.5 flex-shrink-0 mt-0.5" />
                      <span>
                        <strong>{r.svc.name}:</strong>{" "}
                        {r.esBloqueo409
                          ? `Bloqueo fiscal — ${r.mensajeError}`
                          : r.mensajeError}
                      </span>
                    </div>
                  ))}
              </>
            )}
          </div>
        )}

        {/* ─ Textarea de motivo (único para toda la tanda) ─────────────── */}
        {!procesoTerminado && (
          <div>
            <label
              htmlFor="motivo-cancelar-varios"
              className="mb-1 block text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400"
            >
              Motivo de la anulación
            </label>
            <textarea
              id="motivo-cancelar-varios"
              ref={motivoRef}
              value={motivo}
              onChange={(e) => setMotivo(e.target.value)}
              placeholder="¿Por qué se anulan estos servicios? (se aplica a todos los seleccionados)"
              rows={3}
              disabled={estaBloqueada || Boolean(procesoEstado?.enProceso)}
              className="w-full rounded-lg border border-amber-200 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-amber-500 focus:outline-none disabled:opacity-50 dark:border-amber-700 dark:bg-slate-800 dark:text-white"
            />
            {/* Helper del mínimo: solo cuando el usuario empezó a tipear pero no llega aún */}
            {motivo.length > 0 && !motivoValido && (
              <p className="mt-1 text-xs text-amber-600 dark:text-amber-400">
                Mínimo {MOTIVO_MIN_CHARS} caracteres ({motivo.trim().length}/{MOTIVO_MIN_CHARS})
              </p>
            )}
          </div>
        )}

        {/* ─ Botones de acción ─────────────────────────────────────────── */}
        <div className="flex flex-col sm:flex-row justify-end gap-3 pt-2">
          {procesoTerminado ? (
            // Una vez terminado el proceso, solo el botón de cerrar
            <button
              type="button"
              onClick={onCerrar}
              className="rounded-lg bg-slate-100 px-5 py-2.5 text-sm font-bold text-slate-700 hover:bg-slate-200 transition-colors dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700"
            >
              Cerrar
            </button>
          ) : (
            <>
              <button
                type="button"
                onClick={onCerrar}
                disabled={Boolean(procesoEstado?.enProceso)}
                className="rounded-lg px-4 py-2 text-sm font-bold text-slate-600 hover:bg-slate-100 disabled:opacity-50 transition-colors dark:text-slate-300 dark:hover:bg-slate-800"
              >
                Cancelar
              </button>
              <button
                type="button"
                onClick={handleConfirmar}
                disabled={
                  estaBloqueada ||
                  serviciosSeleccionados.length === 0 ||
                  !motivoValido ||
                  (saleInvoices.length > 1 && !targetInvoicePublicId) ||
                  Boolean(procesoEstado?.enProceso)
                }
                data-testid="btn-confirmar-cancelar-varios"
                className="inline-flex items-center gap-2 rounded-lg bg-amber-600 px-5 py-2.5 text-sm font-bold text-white transition-colors hover:bg-amber-700 disabled:opacity-50"
              >
                {procesoEstado?.enProceso && <Loader2 className="h-4 w-4 animate-spin" />}
                {procesoEstado?.enProceso
                  ? `Anulando...`
                  : `Confirmar anulación (${serviciosSeleccionados.length} servicio${serviciosSeleccionados.length !== 1 ? "s" : ""})`}
              </button>
            </>
          )}
        </div>
      </div>
    </section>
  );
}

// ============================================================================
// Helpers internos
// ============================================================================

/**
 * Extrae el mensaje de error legible de una respuesta de la API.
 *
 * El cliente api.js (fetch nativo, NO axios) lanza errores con esta forma:
 *   error.message  → string normalizado por normalizeMessage (campo principal)
 *   error.status   → número HTTP (ej. 409, 500)
 *   error.code     → string|null
 *   error.payload  → body JSON completo del backend (puede traer title, message, etc.)
 *
 * La lectura de error.message directamente ya devuelve el mensaje listo.
 * Revisamos también error.payload por si el backend trae detalles adicionales.
 */
function extraerMensajeError(error, fallback) {
  if (!error) return fallback;

  // error.message ya viene normalizado por parseErrorResponse en api.js.
  // Es la fuente más confiable para este cliente.
  if (typeof error.message === "string" && error.message) return error.message;

  // Revisamos el payload (body JSON del backend) como respaldo.
  const payload = error?.payload;
  if (typeof payload?.message === "string" && payload.message) return payload.message;
  if (typeof payload?.title === "string" && payload.title) return payload.title;
  if (typeof payload === "string" && payload) return payload;

  return fallback;
}

/**
 * Clasifica el resultado final del proceso de cancelación secuencial.
 * Función pura — no depende del estado de React, fácil de testear.
 *
 * @param {Array} resultados - Array de { svc, ok, mensajeError?, esBloqueo409 }
 * @returns {{ todosOk: boolean, algunoFallo: boolean, totalExitos: number, totalFallos: number }}
 */
export function clasificarResultadoFinal(resultados) {
  if (!resultados || resultados.length === 0) {
    return { todosOk: false, algunoFallo: false, totalExitos: 0, totalFallos: 0 };
  }

  const totalExitos = resultados.filter((r) => r.ok).length;
  const totalFallos = resultados.filter((r) => !r.ok).length;
  const todosOk = totalFallos === 0;
  // algunoFallo = true cuando hay al menos un fallo, sin importar si también hubo éxitos.
  // Esto incluye el caso de éxito parcial (algunos OK + algunos fallaron).
  const algunoFallo = totalFallos > 0;

  return { todosOk, algunoFallo, totalExitos, totalFallos };
}

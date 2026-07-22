/**
 * Ficha EN LÍNEA para corregir a qué reserva va un reembolso del operador ya registrado
 * (Tanda P2, spec docs/ux/2026-07-22-p2-deshacer-reasociar-reembolso.md, sección 6).
 *
 * Se abre debajo de la fila en el bloque "Reembolsos ya registrados" cuando el usuario
 * aprieta "Corregir reserva". Dos partes: elegir la reserva destino (P3=A: lista filtrada,
 * no buscador libre) + motivo obligatorio con contador.
 *
 * El destino se arma reusando el MISMO fetch que "Registrar reembolso recibido"
 * (getPendingBySupplier) — la lista de anulaciones pendientes de este operador es
 * exactamente el universo de destinos válidos, filtrado a la moneda del reembolso y
 * excluyendo la reserva a la que ya está imputado (filtrarDestinosParaCorregir).
 *
 * Al confirmar: PATCH /operator-refunds/allocations/{id}/reassociate con
 * { newBookingCancellationPublicId, reason }.
 *
 * Props:
 *   - item: OperatorRefundRegisteredItemDto — la fila que se está corrigiendo.
 *   - supplierId: string — publicId del proveedor (para buscar los destinos posibles).
 *   - onCerrar: () => void — cierra la ficha sin guardar.
 *   - onCompletado: () => void — se llama al corregir con éxito; el padre recarga la
 *     solapa entera (los dos bloques + el encabezado).
 */

import { useCallback, useEffect, useState } from "react";
import { ArrowRightLeft, Loader2, X } from "lucide-react";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatCurrency } from "../../../lib/utils";
import { operatorRefundsApi } from "../api/operatorRefundsApi";
import {
  MOTIVO_ACCION_REEMBOLSO_MIN,
  construirPayloadCorregir,
  esErrorCreditoYaUsado,
  filtrarDestinosParaCorregir,
  puedeConfirmarCorregir,
  validarMotivoAccionReembolso,
} from "../lib/operatorRefundRegisteredLogic";
import { construirTextoCuentaReembolso } from "../lib/supplierPageLogic";
import { ErrorAccionReembolsoBanner } from "./ErrorAccionReembolsoBanner";

export function CorregirReembolsoInline({ item, supplierId, onCerrar, onCompletado }) {
  // ─── Carga de destinos posibles (misma fuente que "Registrar reembolso recibido") ──
  const [loadingDestinos, setLoadingDestinos] = useState(true);
  const [errorCargaDestinos, setErrorCargaDestinos] = useState(null);
  const [destinos, setDestinos] = useState([]);

  const cargarDestinos = useCallback(async () => {
    setLoadingDestinos(true);
    setErrorCargaDestinos(null);
    try {
      const itemsPendientes = await operatorRefundsApi.getPendingBySupplier(supplierId);
      const candidatos = filtrarDestinosParaCorregir(itemsPendientes, {
        currency: item.currency,
        reservaPublicIdActual: item.reservaPublicId,
      });
      setDestinos(candidatos);
    } catch (error) {
      setErrorCargaDestinos(getApiErrorMessage(error, "No se pudo cargar la lista de reservas."));
    } finally {
      setLoadingDestinos(false);
    }
  }, [supplierId, item.currency, item.reservaPublicId]);

  // Carga al montar la ficha (se remonta cada vez que el usuario abre "Corregir reserva").
  useEffect(() => {
    cargarDestinos();
  }, [cargarDestinos]);

  // ─── Estado del formulario ──────────────────────────────────────────────────
  const [destinoElegido, setDestinoElegido] = useState(null);
  const [motivo, setMotivo] = useState("");
  const [guardando, setGuardando] = useState(false);
  const [errorMensaje, setErrorMensaje] = useState(null);
  const [mostrarBotonCuentaCliente, setMostrarBotonCuentaCliente] = useState(false);

  const motivoValido = validarMotivoAccionReembolso(motivo) === null;
  const puedeConfirmar = puedeConfirmarCorregir({ destinoElegido, motivo, submitting: guardando });

  const handleConfirmar = async () => {
    if (!puedeConfirmar) return;

    setGuardando(true);
    setErrorMensaje(null);
    setMostrarBotonCuentaCliente(false);

    try {
      const payload = construirPayloadCorregir(destinoElegido, motivo);
      await operatorRefundsApi.reassociateAllocation(item.publicId, payload);
      showSuccess(`Listo. El reembolso ahora está en la reserva #${destinoElegido.numeroReserva}.`);
      onCompletado();
    } catch (error) {
      // Ficha queda abierta con el destino y el motivo intactos para poder reintentar.
      setErrorMensaje(getApiErrorMessage(error, "No se pudo corregir la reserva del reembolso."));
      setMostrarBotonCuentaCliente(esErrorCreditoYaUsado(error));
    } finally {
      setGuardando(false);
    }
  };

  // ─── Render: cargando destinos ──────────────────────────────────────────────
  if (loadingDestinos) {
    return (
      <div
        className="mt-3 rounded-xl border-2 border-indigo-200 bg-indigo-50/60 p-4 dark:border-indigo-900/40 dark:bg-indigo-950/20"
        data-testid={`form-corregir-reembolso-${item.publicId}`}
      >
        <div className="flex items-center gap-2 text-xs text-slate-500">
          <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
          Buscando reservas anuladas de este operador…
        </div>
      </div>
    );
  }

  // ─── Render: error al cargar destinos ───────────────────────────────────────
  if (errorCargaDestinos) {
    return (
      <div
        className="mt-3 rounded-xl border-2 border-rose-200 bg-rose-50/40 p-4 space-y-2 dark:border-rose-900/40 dark:bg-rose-950/10"
        data-testid={`form-corregir-reembolso-${item.publicId}`}
      >
        <p className="text-xs text-rose-700 dark:text-rose-300" role="alert">{errorCargaDestinos}</p>
        <div className="flex justify-end gap-2">
          <button type="button" onClick={cargarDestinos} className="text-xs text-indigo-600 hover:underline dark:text-indigo-400">
            Reintentar
          </button>
          <button type="button" onClick={onCerrar} className="text-xs text-slate-500 hover:underline">
            Cerrar
          </button>
        </div>
      </div>
    );
  }

  // ─── Render: formulario principal ───────────────────────────────────────────
  return (
    <div
      className="mt-3 rounded-xl border-2 border-indigo-200 bg-indigo-50/60 p-4 space-y-3 dark:border-indigo-900/40 dark:bg-indigo-950/20"
      data-testid={`form-corregir-reembolso-${item.publicId}`}
    >
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <ArrowRightLeft className="h-4 w-4 text-indigo-600 dark:text-indigo-400" aria-hidden="true" />
          <h4 className="text-sm font-bold text-slate-900 dark:text-white">
            Corregir a qué reserva va este reembolso
          </h4>
        </div>
        <button
          type="button"
          onClick={onCerrar}
          disabled={guardando}
          className="rounded p-1 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200 disabled:opacity-50"
          aria-label="Cerrar ficha de corregir reserva"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <p className="text-xs text-slate-600 dark:text-slate-400">
        Hoy este reembolso
        {!item.amountsMasked && ` de ${formatCurrency(item.netAmount, item.currency)}`}
        {" "}está imputado a la reserva #{item.numeroReserva}. Elegí la reserva correcta (solo
        aparecen las anulaciones de este operador que esperan un reembolso en la misma moneda):
      </p>

      <ErrorAccionReembolsoBanner
        mensaje={errorMensaje}
        mostrarBotonCuentaCliente={mostrarBotonCuentaCliente}
        clientePublicId={item.clientePublicId}
        onClose={() => setErrorMensaje(null)}
      />

      {destinos.length === 0 ? (
        // Sin destinos elegibles (spec §6): cartel neutro, no se puede continuar, sugiere Deshacer.
        <p
          className="rounded-lg border border-slate-200 bg-white px-3 py-2 text-xs text-slate-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300"
          data-testid="reembolso-corregir-sin-destinos"
        >
          No hay otra reserva anulada de este operador esperando un reembolso en esta moneda.
          Si la reserva de este reembolso está mal, usá "Deshacer" y volvé a cargarlo.
        </p>
      ) : (
        <>
          <div
            className="max-h-48 overflow-y-auto rounded-lg border border-slate-200 dark:border-slate-700 divide-y divide-slate-100 dark:divide-slate-800"
            role="radiogroup"
            aria-label="Reserva destino del reembolso"
          >
            {destinos.map((destino) => {
              const estaElegido = destinoElegido?.key === destino.key;
              return (
                <button
                  key={destino.key}
                  type="button"
                  role="radio"
                  aria-checked={estaElegido}
                  onClick={() => setDestinoElegido(destino)}
                  disabled={guardando}
                  className={`w-full text-left px-3 py-2 flex flex-col gap-0.5 transition-colors ${
                    estaElegido
                      ? "bg-indigo-100 dark:bg-indigo-900/30"
                      : "bg-white dark:bg-slate-800 hover:bg-slate-50 dark:hover:bg-slate-700/50"
                  } disabled:opacity-50`}
                  data-testid={`reembolso-corregir-destino-${destino.bookingCancellationPublicId}`}
                >
                  <span className="text-sm font-semibold text-slate-800 dark:text-slate-200">
                    Reserva #{destino.numeroReserva || "—"}
                    {destino.clienteNombre && (
                      <span className="font-normal text-slate-500 dark:text-slate-400"> · {destino.clienteNombre}</span>
                    )}
                  </span>
                  <span className="text-xs text-slate-500 dark:text-slate-400">
                    {construirTextoCuentaReembolso(destino)}
                  </span>
                </button>
              );
            })}
          </div>

          <div>
            <label htmlFor={`corregir-motivo-${item.publicId}`} className="sr-only">
              Motivo de la corrección
            </label>
            <textarea
              id={`corregir-motivo-${item.publicId}`}
              value={motivo}
              onChange={(e) => setMotivo(e.target.value)}
              placeholder="Contá por qué lo corregís (mínimo 20 caracteres)…"
              rows={2}
              disabled={guardando}
              className="w-full rounded-lg border border-indigo-200 bg-white px-3 py-2 text-xs dark:border-indigo-800 dark:bg-slate-900 dark:text-white disabled:opacity-60 focus:outline-none focus:ring-1 focus:ring-indigo-400 resize-none"
              aria-required="true"
              data-testid="reembolso-corregir-motivo"
            />
            <p className={`text-[10px] mt-0.5 ${motivoValido ? "text-slate-400" : "text-amber-600 dark:text-amber-400"}`}>
              {motivo.trim().length} / {MOTIVO_ACCION_REEMBOLSO_MIN} caracteres mínimos
            </p>
          </div>
        </>
      )}

      <div className="flex items-center justify-end gap-2">
        <button
          type="button"
          onClick={onCerrar}
          disabled={guardando}
          className="rounded-lg border border-slate-300 px-3 py-1.5 text-xs font-semibold text-slate-600 hover:bg-slate-100 dark:border-slate-700 dark:text-slate-400 dark:hover:bg-slate-800 disabled:opacity-50 transition-colors"
        >
          Cancelar
        </button>
        {destinos.length > 0 && (
          <button
            type="button"
            onClick={handleConfirmar}
            disabled={!puedeConfirmar}
            data-testid="reembolso-corregir-confirmar"
            className="flex items-center gap-2 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-bold text-white hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {guardando && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
            {guardando
              ? "Moviendo…"
              : destinoElegido
                ? `Mover a la reserva #${destinoElegido.numeroReserva}`
                : "Mover a la reserva"}
          </button>
        )}
      </div>
    </div>
  );
}

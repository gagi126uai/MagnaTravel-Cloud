/**
 * Cartel de la ficha para los pasos "trabados" o "en curso" de la multa del operador
 * (spec "el paso de multa vive en la ficha", 2026-07-08). Traduce
 * `reserva.operatorPenaltySituation` a UN cartel con, como máximo, UNA acción puntual —
 * nunca reenvía a otra pantalla ("bandeja") a resolver algo.
 *
 * Cubre dos de las cinco familias de operatorPenaltyBanner.js:
 *   - "accionTrabada" (DebitNoteFailed / DebitNoteNeedsAmountCurrency / ConfirmedNoDebitNote):
 *     cartel naranja con un botón puntual (Reintentar / Corregir monto y moneda / Emitir
 *     la nota ahora), gateado por los permisos que ya vienen resueltos del backend
 *     (canRetryDebitNote / canCorrectAmountCurrency).
 *   - "procesando" (DebitNoteQueued): cartel informativo ámbar, sin botón — se está
 *     emitiendo, no hay nada para hacer todavía.
 *
 * Las familias "pregunta" (PendingDecision) y "waived" (Waived) NO se dibujan acá: ya
 * tienen su propio bloque en ReservaDetailPage (los botones "Sí cobró/No cobró" y el
 * rastro + "Deshacer" respectivamente) — este componente no las duplica.
 *
 * Reemplaza al viejo cartel "Ir a resolver" que mandaba a la bandeja back-office
 * (/pendientes-afip?tab=multas): ahora se resuelve DIRECTO acá, sin navegar a otra pantalla.
 */

import { useState } from "react";
import { Loader2 } from "lucide-react";
import { familiaDeEstadoMulta, copyAccionTrabada, slugDeEstadoMulta } from "../operatorPenaltyBanner";
import { ConfirmarMultaOperadorInline } from "./ConfirmarMultaOperadorInline";
import { cancellationsApi } from "../api/cancellationsApi";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";

/**
 * Props:
 *   - reservaPublicId: GUID de la reserva (para buscar la cancelación vigente recién al
 *     apretar el botón — el GUID de la cancelación no viaja en el DTO de la reserva).
 *   - reservaNumero: número de negocio, para el header del panel de corrección.
 *   - situacion: reserva.operatorPenaltySituation (obligatorio no-nulo; el padre decide
 *     cuándo montar este componente según familiaDeEstadoMulta(situacion.state)).
 *   - monedaSugerida: moneda con la que arranca el selector del modo "corregir", si hace falta.
 *   - onResuelto: callback tras una acción exitosa (el padre refresca la reserva).
 */
export function OperatorPenaltyStepPanel({ reservaPublicId, reservaNumero, situacion, monedaSugerida, onResuelto }) {
  const [buscando, setBuscando] = useState(false);
  const [showCorregir, setShowCorregir] = useState(false);
  const [cancellationPublicId, setCancellationPublicId] = useState(null);

  const familia = familiaDeEstadoMulta(situacion.state);
  const testId = `banner-multa-${slugDeEstadoMulta(situacion.state)}`;

  if (familia === "procesando") {
    return (
      <div
        className="rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-800 dark:border-amber-900/40 dark:bg-amber-950/20 dark:text-amber-200"
        data-testid={testId}
        role="status"
      >
        <strong className="font-bold">Anulada — se está emitiendo la multa al cliente.</strong> Puede demorar unos minutos, no hace falta que hagas nada.
      </div>
    );
  }

  if (familia !== "accionTrabada") return null;

  const { mensaje, accion, textoBoton } = copyAccionTrabada({
    state: situacion.state,
    canRetryDebitNote: situacion.canRetryDebitNote,
    canCorrectAmountCurrency: situacion.canCorrectAmountCurrency,
  });

  // Modo "corregir" abierto: el panel inline reemplaza al cartel, mismo patrón que el resto
  // de los paneles de multa de la ficha (ConfirmarMultaOperadorInline / CerrarSinMultaInline).
  if (showCorregir && cancellationPublicId) {
    return (
      <ConfirmarMultaOperadorInline
        cancellationPublicId={cancellationPublicId}
        reservaNumero={reservaNumero}
        modo="corregir"
        monedaSugerida={situacion.currency ?? monedaSugerida}
        // El monto que ya estaba cargado (y quedó trabado) se precarga para corregir,
        // no para tipear de cero.
        montoInicial={situacion.amount ?? undefined}
        onConfirmado={() => {
          setShowCorregir(false);
          onResuelto();
        }}
        onCerrar={() => setShowCorregir(false)}
      />
    );
  }

  const handleClickAccion = async () => {
    setBuscando(true);
    try {
      // El GUID de la cancelación no viaja en el DTO de la reserva: se busca recién al
      // apretar el botón (mismo patrón que buscarCancelacionYAbrirPanel en ReservaDetailPage).
      let publicId = cancellationPublicId;
      if (!publicId) {
        const cancelacion = await cancellationsApi.getByReserva(reservaPublicId);
        if (!cancelacion?.publicId) {
          showError(
            "No se encontró la cancelación de esta reserva. Actualizá la página y volvé a intentar.",
            "Sin cancelación"
          );
          return;
        }
        publicId = cancelacion.publicId;
        setCancellationPublicId(publicId);
      }

      if (accion === "corregir") {
        setShowCorregir(true);
        return;
      }

      // "reintentar" (DebitNoteFailed) y "emitir" (ConfirmedNoDebitNote) usan el MISMO
      // endpoint: el backend decide qué corresponde según el estado real de la multa.
      await cancellationsApi.retryDebitNote(publicId);
      showSuccess("Listo. Se está reintentando el cargo al cliente.", "Reintentando");
      onResuelto();
    } catch (error) {
      showError(getApiErrorMessage(error, "No se pudo reintentar el cargo al cliente. Probá de nuevo."));
    } finally {
      setBuscando(false);
    }
  };

  return (
    <div
      className="rounded-xl border border-orange-300 bg-orange-50 p-4 text-sm text-orange-900 dark:border-orange-700/50 dark:bg-orange-950/30 dark:text-orange-200"
      data-testid={testId}
      role="status"
    >
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <span>
          <strong className="font-bold">{mensaje}</strong>
        </span>
        {/* Sin permiso, copyAccionTrabada devuelve accion=null: cartel informativo, sin botón. */}
        {accion && (
          <button
            type="button"
            onClick={handleClickAccion}
            disabled={buscando}
            data-testid={`btn-multa-${accion}`}
            className="inline-flex items-center gap-1.5 rounded-lg border border-orange-400 bg-orange-100 px-3 py-2 text-xs font-bold text-orange-800 hover:bg-orange-200 dark:border-orange-700 dark:bg-orange-900/40 dark:text-orange-200 dark:hover:bg-orange-900/60 transition-colors flex-shrink-0 disabled:opacity-50"
          >
            {buscando && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
            {textoBoton}
          </button>
        )}
      </div>
    </div>
  );
}

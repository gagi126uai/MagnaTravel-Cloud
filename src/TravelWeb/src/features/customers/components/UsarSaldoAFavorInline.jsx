/**
 * Ficha EN LÍNEA para usar el saldo a favor de un cliente.
 *
 * Se despliega debajo del cartel "A FAVOR" en CustomerAccountPage,
 * con el mismo patrón visual que RegistrarCobroInline y EmitirFacturaInline.
 *
 * Flujo:
 *   1. Al abrir: llama GET /api/customers/{publicId}/available-credit.
 *   2. Filtra los entries a la moneda del cartel que abrió la ficha (prop `moneda`).
 *      Nunca se muestran entries de otra moneda — un cartel = una moneda.
 *   3. El usuario elige un entry, un destino (efectivo / transferencia / dejar), y un monto.
 *   4. Al confirmar: POST /api/client-credit-entries/{entryPublicId}/withdrawals.
 *   5. En error 409 del backend (tope Ley 25.345): muestra el mensaje del backend sin modificar.
 *   6. Al éxito: llama onConfirmado() → la página refresca el overview (el cartel baja).
 *
 * Permiso requerido: cobranzas.edit (verificado en el padre; este componente no se monta si no hay permiso).
 *
 * REGLA: NO se ofrece "Aplicar a otra reserva" (kind 3 = AppliedToNewBooking) porque
 * el backend aún no conecta el pago en la reserva destino. Se agrega en FC4.
 */

import { useCallback, useEffect, useState } from "react";
import { ArrowDownToLine, Loader2, Wallet, X } from "lucide-react";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import {
  DESTINOS_RETIRO,
  armarPayloadRetiro,
  formatearDescripcionEntry,
  validarMontoRetiro,
} from "../lib/creditWithdrawalLogic";

export function UsarSaldoAFavorInline({ publicId, moneda, onConfirmado, onCancelar }) {
  // --- Estado de carga de los entries disponibles ---
  const [entries, setEntries] = useState([]);
  const [loadingEntries, setLoadingEntries] = useState(true);
  const [errorCarga, setErrorCarga] = useState(null);

  // --- Estado del formulario ---
  const [entrySeleccionado, setEntrySeleccionado] = useState(null);
  const [kindDestino, setKindDestino]             = useState(2); // default: Transferencia
  const [monto, setMonto]                         = useState("");
  const [referencia, setReferencia]               = useState(""); // solo para transferencia
  const [errorValidacion, setErrorValidacion]     = useState(null);

  // --- Estado del submit ---
  const [guardando, setGuardando]     = useState(false);
  const [errorGuardar, setErrorGuardar] = useState(null);

  // Carga los entries al montar.
  // useEffect con [] corre solo una vez: cuando la ficha se despliega.
  useEffect(() => {
    async function cargarEntries() {
      setLoadingEntries(true);
      setErrorCarga(null);
      try {
        const datos = await api.get(`/customers/${publicId}/available-credit`);

        // El backend devuelve solo entries con remainingBalance > 0, orden FIFO.
        // Filtramos por la moneda del cartel que abrió esta ficha: nunca mezclamos ARS y USD.
        const todosLosEntries = Array.isArray(datos) ? datos : [];
        const entriesDeLaMoneda = moneda
          ? todosLosEntries.filter((entry) => entry.currency === moneda)
          : todosLosEntries;

        setEntries(entriesDeLaMoneda);

        // Preseleccionar el primer entry de la moneda correspondiente
        if (entriesDeLaMoneda.length > 0) {
          const primerEntry = entriesDeLaMoneda[0];
          setEntrySeleccionado(primerEntry);
          // Por defecto, el monto es el saldo total del entry (retiro completo)
          setMonto(String(primerEntry.remainingBalance));
        }
      } catch (error) {
        // Distinguimos 503 (base de datos no disponible) del resto de errores para dar un mensaje más claro
        const mensaje = isDatabaseUnavailableError(error)
          ? "El servidor no está disponible. Esperá unos segundos y volvé a intentar."
          : getApiErrorMessage(error, "No se pudo cargar el saldo disponible. Intentá de nuevo.");
        setErrorCarga(mensaje);
        console.error(error);
      } finally {
        setLoadingEntries(false);
      }
    }

    cargarEntries();
  }, [publicId, moneda]);

  // Cuando el usuario cambia el entry, actualizamos el monto al saldo del nuevo entry
  const handleCambiarEntry = useCallback((entryPublicId) => {
    const entry = entries.find((e) => e.entryPublicId === entryPublicId);
    if (entry) {
      setEntrySeleccionado(entry);
      setMonto(String(entry.remainingBalance));
      setErrorValidacion(null);
      setErrorGuardar(null);
    }
  }, [entries]);

  const handleConfirmar = async () => {
    setErrorGuardar(null);

    // Para kind = 0 (dejar como crédito) no hace falta validar monto
    if (kindDestino !== 0) {
      const errorMonto = validarMontoRetiro(monto, entrySeleccionado?.remainingBalance ?? 0);
      if (errorMonto) {
        setErrorValidacion(errorMonto);
        return;
      }
    }
    setErrorValidacion(null);

    if (!entrySeleccionado) {
      setErrorGuardar("Seleccioná un saldo a usar.");
      return;
    }

    setGuardando(true);
    try {
      const payload = armarPayloadRetiro(kindDestino, monto, {
        reference: referencia || undefined,
      });

      await api.post(
        `/client-credit-entries/${entrySeleccionado.entryPublicId}/withdrawals`,
        payload
      );

      showSuccess(
        kindDestino === 0
          ? "El aviso de saldo fue cerrado."
          : "El retiro fue registrado exitosamente."
      );

      onConfirmado();
    } catch (error) {
      // El backend puede devolver 409 cuando el monto supera el tope de la Ley 25.345
      // para devolución en efectivo. En ese caso mostramos el mensaje tal cual viene.
      setErrorGuardar(
        getApiErrorMessage(
          error,
          "No se pudo registrar el retiro. Revisá la conexión y volvé a intentar."
        )
      );
    } finally {
      setGuardando(false);
    }
  };

  // ─── Render: estado de carga ────────────────────────────────────────────────
  if (loadingEntries) {
    return (
      <div
        className="rounded-xl border-2 border-emerald-200 bg-emerald-50/40 dark:border-emerald-900/40 dark:bg-emerald-950/10 p-5"
        data-testid="usar-saldo-inline"
        data-state="loading"
      >
        <div className="flex items-center gap-2 text-sm text-slate-500">
          <Loader2 className="h-4 w-4 animate-spin" />
          Cargando saldo disponible...
        </div>
      </div>
    );
  }

  // ─── Render: error al cargar ────────────────────────────────────────────────
  if (errorCarga) {
    return (
      <div
        className="rounded-xl border-2 border-rose-200 bg-rose-50/40 dark:border-rose-900/40 dark:bg-rose-950/10 p-5 space-y-3"
        data-testid="usar-saldo-inline"
        data-state="error"
      >
        <p className="text-sm text-rose-700 dark:text-rose-300" role="alert">{errorCarga}</p>
        <div className="flex justify-end">
          <button
            type="button"
            onClick={onCancelar}
            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700"
          >
            Cerrar
          </button>
        </div>
      </div>
    );
  }

  // ─── Render: sin entries disponibles ────────────────────────────────────────
  if (entries.length === 0) {
    return (
      <div
        className="rounded-xl border-2 border-slate-200 bg-slate-50/40 dark:border-slate-700 dark:bg-slate-900/20 p-5 space-y-3"
        data-testid="usar-saldo-inline"
        data-state="empty"
      >
        <p className="text-sm text-slate-500">No hay saldo a favor disponible en este momento.</p>
        <div className="flex justify-end">
          <button
            type="button"
            onClick={onCancelar}
            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700"
          >
            Cerrar
          </button>
        </div>
      </div>
    );
  }

  // ─── Render: formulario principal ───────────────────────────────────────────
  return (
    <div
      className="rounded-xl border-2 border-emerald-200 bg-emerald-50/40 dark:border-emerald-900/40 dark:bg-emerald-950/10 p-5 space-y-4"
      data-testid="usar-saldo-inline"
      data-state="ready"
    >
      {/* Cabecera */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Wallet className="h-4 w-4 text-emerald-600" />
          <h4 className="text-sm font-bold text-slate-900 dark:text-white">Usar saldo a favor</h4>
        </div>
        <button
          type="button"
          onClick={onCancelar}
          className="rounded p-1 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200"
          aria-label="Cerrar ficha de saldo a favor"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      {/* Selector de entry (si hay más de uno, el usuario elige cuál usar) */}
      {entries.length > 1 && (
        <div className="space-y-1">
          <label
            htmlFor="entry-selector"
            className="text-xs font-semibold text-slate-600 dark:text-slate-400"
          >
            Saldo a usar
          </label>
          <select
            id="entry-selector"
            value={entrySeleccionado?.entryPublicId ?? ""}
            onChange={(e) => handleCambiarEntry(e.target.value)}
            disabled={guardando}
            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
            data-testid="saldo-entry-selector"
          >
            {entries.map((entry) => (
              <option key={entry.entryPublicId} value={entry.entryPublicId}>
                {formatearDescripcionEntry(entry)}
              </option>
            ))}
          </select>
        </div>
      )}

      {/* Si hay exactamente un entry, lo mostramos solo como texto (sin select) */}
      {entries.length === 1 && entrySeleccionado && (
        <p className="text-sm text-slate-700 dark:text-slate-300">
          {formatearDescripcionEntry(entrySeleccionado)}
        </p>
      )}

      {/* Destino del retiro */}
      <div className="space-y-1">
        <label
          htmlFor="destino-retiro"
          className="text-xs font-semibold text-slate-600 dark:text-slate-400"
        >
          Destino
        </label>
        <select
          id="destino-retiro"
          value={kindDestino}
          onChange={(e) => {
            setKindDestino(Number(e.target.value));
            setErrorValidacion(null);
            setErrorGuardar(null);
          }}
          disabled={guardando}
          className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
          data-testid="saldo-destino-selector"
        >
          {DESTINOS_RETIRO.map((destino) => (
            <option key={destino.kind} value={destino.kind}>
              {destino.label}
            </option>
          ))}
        </select>
      </div>

      {/* Monto: visible solo cuando el destino mueve plata (kind ≠ 0) */}
      {kindDestino !== 0 && (
        <div className="space-y-1">
          <label
            htmlFor="monto-retiro"
            className="text-xs font-semibold text-slate-600 dark:text-slate-400"
          >
            Monto a retirar
            {entrySeleccionado && (
              <span className="ml-1 font-normal text-slate-400">
                (máx. {entrySeleccionado.currency === "USD" ? "US$" : "$"}
                {Number(entrySeleccionado.remainingBalance).toLocaleString("es-AR", {
                  minimumFractionDigits: 2,
                  maximumFractionDigits: 2,
                })})
              </span>
            )}
          </label>
          <input
            id="monto-retiro"
            type="number"
            step="0.01"
            min="0.01"
            max={entrySeleccionado?.remainingBalance ?? undefined}
            value={monto}
            onChange={(e) => {
              setMonto(e.target.value);
              setErrorValidacion(null);
              setErrorGuardar(null);
            }}
            disabled={guardando}
            placeholder="0,00"
            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
            data-testid="saldo-monto"
          />
          {errorValidacion && (
            <p className="text-xs text-rose-600 dark:text-rose-400" role="alert">
              {errorValidacion}
            </p>
          )}
        </div>
      )}

      {/* Referencia: campo opcional, solo visible para transferencia (kind 2) */}
      {kindDestino === 2 && (
        <div className="space-y-1">
          <label
            htmlFor="referencia-retiro"
            className="text-xs font-semibold text-slate-600 dark:text-slate-400"
          >
            Referencia de transferencia (opcional)
          </label>
          <input
            id="referencia-retiro"
            type="text"
            value={referencia}
            onChange={(e) => setReferencia(e.target.value)}
            disabled={guardando}
            placeholder="Número de comprobante, CBU, etc."
            className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
            data-testid="saldo-referencia"
          />
        </div>
      )}

      {/* Aviso especial para efectivo: el backend puede devolver 409 por tope Ley 25.345.
          Lo dejamos como nota preventiva para que el cajero lo sepa antes de confirmar. */}
      {kindDestino === 1 && (
        <p className="text-xs text-amber-700 dark:text-amber-400 bg-amber-50 dark:bg-amber-950/20 border border-amber-200 dark:border-amber-900/40 rounded-lg px-3 py-2">
          Ley 25.345: los pagos en efectivo tienen un tope legal. Si el monto lo supera, el sistema lo va a rechazar y te va a indicar que uses transferencia.
        </p>
      )}

      {/* Mensaje de error del backend (incluido 409 del tope de efectivo) */}
      {errorGuardar && (
        <div
          className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-xs text-rose-700 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
          role="alert"
          data-testid="saldo-error"
        >
          {errorGuardar}
        </div>
      )}

      {/* Botones: anti-doble-click con disabled={guardando} */}
      <div className="flex justify-end gap-3 pt-1">
        <button
          type="button"
          onClick={onCancelar}
          disabled={guardando}
          className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 dark:hover:bg-slate-700 disabled:opacity-50 transition-colors"
        >
          Cancelar
        </button>
        <button
          type="button"
          onClick={handleConfirmar}
          disabled={guardando}
          className="flex items-center gap-2 rounded-lg bg-emerald-600 px-4 py-2 text-sm font-bold text-white shadow-sm hover:bg-emerald-700 disabled:opacity-50 transition-colors"
          data-testid="saldo-confirmar"
        >
          {guardando ? (
            <>
              <Loader2 className="h-4 w-4 animate-spin" />
              Procesando…
            </>
          ) : (
            <>
              <ArrowDownToLine className="h-4 w-4" />
              {kindDestino === 0 ? "Cerrar aviso" : "Confirmar retiro"}
            </>
          )}
        </button>
      </div>
    </div>
  );
}

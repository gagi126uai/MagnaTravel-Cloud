/**
 * Ficha EN LÍNEA para aplicar el saldo a favor con un proveedor a otra reserva.
 *
 * Se despliega debajo del cartel verde "A FAVOR CON ESTE PROVEEDOR EN $"
 * en SupplierAccountPage, con el mismo patrón visual que PagarProveedorInline.
 *
 * Flujo:
 *   1. Al abrir: carga las reservas con deuda en la moneda correspondiente,
 *      filtrando desde GET /api/suppliers/{id}/account/debt-by-reserva.
 *   2. El usuario busca y selecciona una reserva destino.
 *      Monto sugerido = min(deuda de esa reserva en la moneda, saldo disponible).
 *   3. Al confirmar: POST /api/suppliers/{id}/credit/apply {currency, amount, targetReservaPublicId}.
 *   4. El backend valida topes (no cruzar monedas, no superar saldo disponible)
 *      y devuelve 409 con mensaje descriptivo si hay algún problema de negocio.
 *
 * Enmascarado: los montos de deuda se enmascaran con "—" si el usuario
 * no tiene permiso cobranzas.see_cost. El monto a aplicar sí se muestra
 * siempre (el usuario tiene que ingresarlo).
 *
 * Permiso requerido: tesoreria.supplier_payments (verificado en el padre).
 *
 * Props:
 *   - supplierId: string — publicId del proveedor
 *   - moneda: "ARS" | "USD" — moneda del cartel verde que abrió la ficha
 *   - saldoDisponible: number — AvailableBalance del overview de crédito
 *   - onAplicado: () => void — callback al éxito (la página recarga)
 *   - onCancelar: () => void
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { Loader2, Search, TrendingUp, X } from "lucide-react";
import { api } from "../../../api";
import { hasPermission } from "../../../auth";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";

export function UsarSaldoOperadorInline({
  supplierId,
  moneda,
  saldoDisponible = 0,
  onAplicado,
  onCancelar,
}) {
  // El usuario puede ver montos de costo solo si tiene este permiso.
  // Los montos de deuda de cada reserva se enmascaran si no lo tiene.
  const puedeVerMontos = hasPermission("cobranzas.see_cost");

  // ─── Carga de reservas destino ─────────────────────────────────────────────
  const [reservasConDeuda, setReservasConDeuda] = useState([]);
  const [loadingReservas, setLoadingReservas] = useState(true);
  const [errorCarga, setErrorCarga] = useState(null);

  // ─── Estado del formulario ──────────────────────────────────────────────────
  const [busquedaReserva, setBusquedaReserva] = useState("");
  const [reservaDestinoSeleccionada, setReservaDestinoSeleccionada] = useState(null);
  const [monto, setMonto] = useState("");
  const [errorValidacion, setErrorValidacion] = useState(null);

  // ─── Estado del submit ──────────────────────────────────────────────────────
  const [guardando, setGuardando] = useState(false);
  const [errorGuardar, setErrorGuardar] = useState(null);

  // Carga las reservas con deuda en la moneda de esta ficha.
  // Usamos el endpoint que ya existe para el proveedor: devuelve reservas con
  // sus deudas desglosadas por moneda, que es exactamente lo que necesitamos.
  const cargarReservas = useCallback(async () => {
    setLoadingReservas(true);
    setErrorCarga(null);
    try {
      const response = await api.get(`/suppliers/${supplierId}/account/debt-by-reserva`);
      const todasLasReservas = response?.reservas ?? [];

      // Filtramos solo las reservas que tienen deuda en ESTA moneda (balance > 0 en la moneda del cartel).
      // Regla multimoneda: si el cartel es de pesos, solo mostramos reservas con deuda en pesos.
      const conDeudaEnMoneda = todasLasReservas.filter((r) => {
        const lineaMoneda = (r.currencies ?? []).find((c) => c.currency === moneda);
        return lineaMoneda && (lineaMoneda.balance ?? 0) > 0;
      });

      setReservasConDeuda(conDeudaEnMoneda);
    } catch (error) {
      setErrorCarga(
        getApiErrorMessage(error, "No se pudo cargar la lista de reservas. Intentá de nuevo.")
      );
      console.error("[UsarSaldoOperadorInline] Error cargando reservas:", error);
    } finally {
      setLoadingReservas(false);
    }
  }, [supplierId, moneda]);

  // Carga al montar. useEffect con dependencia en cargarReservas para que si
  // cambia el proveedor o la moneda, recargue automáticamente.
  useEffect(() => {
    cargarReservas();
  }, [cargarReservas]);

  // Cuando el usuario selecciona una reserva, precargamos el monto sugerido.
  // Regla (spec): monto sugerido = min("lo que debe el destino", "saldo disponible").
  // Editável hacia abajo (el usuario puede reducirlo, no aumentarlo).
  const handleSeleccionarReserva = useCallback((reserva) => {
    setReservaDestinoSeleccionada(reserva);
    setErrorValidacion(null);
    setErrorGuardar(null);

    // Calculamos la deuda de esta reserva en la moneda de la ficha
    const lineaMoneda = (reserva.currencies ?? []).find((c) => c.currency === moneda);
    const deudaReservaDestino = lineaMoneda ? (lineaMoneda.balance ?? 0) : 0;

    // El monto sugerido es el menor de los dos: no se puede aplicar más de lo que se debe
    // ni más de lo que hay disponible en el saldo a favor
    const montoSugerido = Math.min(deudaReservaDestino, saldoDisponible);
    setMonto(String(montoSugerido > 0 ? montoSugerido : saldoDisponible));
  }, [moneda, saldoDisponible]);

  // Filtra las reservas según el texto del buscador (client-side, sin nueva llamada).
  const reservasFiltradas = useMemo(() => {
    if (!busquedaReserva.trim()) return reservasConDeuda;
    const texto = busquedaReserva.toLowerCase().trim();
    return reservasConDeuda.filter(
      (r) =>
        (r.numeroReserva ?? "").toLowerCase().includes(texto) ||
        (r.fileName ?? "").toLowerCase().includes(texto)
    );
  }, [reservasConDeuda, busquedaReserva]);

  const handleConfirmar = async () => {
    setErrorGuardar(null);
    setErrorValidacion(null);

    if (!reservaDestinoSeleccionada) {
      setErrorValidacion("Elegí una reserva destino antes de confirmar.");
      return;
    }

    const montoNum = parseFloat(monto);
    if (!monto || isNaN(montoNum) || montoNum <= 0) {
      setErrorValidacion("El monto tiene que ser mayor a 0.");
      return;
    }
    if (montoNum > saldoDisponible) {
      setErrorValidacion(
        `El monto no puede superar el saldo disponible (${formatCurrency(saldoDisponible, moneda)}).`
      );
      return;
    }

    setGuardando(true);
    try {
      // Usamos .reservaPublicId directo porque las reservas vienen de debt-by-reserva,
      // cuyo DTO tiene ese campo con ese nombre (no "publicId"). getPublicId no lo lee.
      await api.post(`/suppliers/${supplierId}/credit/apply`, {
        currency: moneda,
        amount: montoNum,
        targetReservaPublicId: reservaDestinoSeleccionada.reservaPublicId,
      });

      showSuccess(
        `El saldo fue aplicado a la reserva ${reservaDestinoSeleccionada.numeroReserva ?? ""}.`
      );
      onAplicado();
    } catch (error) {
      // El backend devuelve 409 con mensaje descriptivo cuando se intenta cruzar monedas,
      // superar el saldo disponible, o aplicar a una reserva de otro proveedor.
      setErrorGuardar(
        getApiErrorMessage(error, "No se pudo aplicar el saldo. Revisá la conexión y volvé a intentar.")
      );
    } finally {
      setGuardando(false);
    }
  };

  // ─── Render: cargando reservas ─────────────────────────────────────────────
  if (loadingReservas) {
    return (
      <div
        className="rounded-xl border-2 border-emerald-200 bg-emerald-50/40 dark:border-emerald-900/40 dark:bg-emerald-950/10 p-5"
        data-testid="usar-saldo-operador-inline"
        data-state="loading"
      >
        <div className="flex items-center gap-2 text-sm text-slate-500">
          <Loader2 className="h-4 w-4 animate-spin" />
          Cargando reservas disponibles...
        </div>
      </div>
    );
  }

  // ─── Render: error al cargar ────────────────────────────────────────────────
  if (errorCarga) {
    return (
      <div
        className="rounded-xl border-2 border-rose-200 bg-rose-50/40 dark:border-rose-900/40 dark:bg-rose-950/10 p-5 space-y-3"
        data-testid="usar-saldo-operador-inline"
        data-state="error"
      >
        <p className="text-sm text-rose-700 dark:text-rose-300" role="alert">{errorCarga}</p>
        <div className="flex justify-end gap-2">
          <button
            type="button"
            onClick={cargarReservas}
            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 transition-colors"
          >
            Reintentar
          </button>
          <button
            type="button"
            onClick={onCancelar}
            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 transition-colors"
          >
            Cerrar
          </button>
        </div>
      </div>
    );
  }

  // ─── Render: sin reservas con deuda en esta moneda ─────────────────────────
  if (reservasConDeuda.length === 0) {
    return (
      <div
        className="rounded-xl border-2 border-slate-200 bg-slate-50/40 dark:border-slate-700 dark:bg-slate-900/20 p-5 space-y-3"
        data-testid="usar-saldo-operador-inline"
        data-state="empty"
      >
        <p className="text-sm text-slate-600 dark:text-slate-400">
          No hay reservas con deuda en {moneda === "USD" ? "dólares" : "pesos"} para este proveedor.
        </p>
        <div className="flex justify-end">
          <button
            type="button"
            onClick={onCancelar}
            className="rounded-lg border border-slate-200 bg-white px-4 py-2 text-sm font-medium text-slate-700 hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-200 dark:border-slate-700 transition-colors"
          >
            Cerrar
          </button>
        </div>
      </div>
    );
  }

  // ─── Render: formulario principal ──────────────────────────────────────────
  return (
    <div
      className="rounded-xl border-2 border-emerald-200 bg-emerald-50/40 dark:border-emerald-900/40 dark:bg-emerald-950/10 p-5 space-y-4"
      data-testid="usar-saldo-operador-inline"
      data-state="ready"
    >
      {/* Cabecera */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <TrendingUp className="h-4 w-4 text-emerald-600" />
          <h4 className="text-sm font-bold text-slate-900 dark:text-white">
            Aplicar saldo a favor a una reserva
          </h4>
        </div>
        <button
          type="button"
          onClick={onCancelar}
          className="rounded p-1 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200"
          aria-label="Cerrar ficha de aplicación de saldo"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      {/* Saldo disponible como referencia visual */}
      <div className="rounded-lg bg-emerald-100/60 dark:bg-emerald-900/20 border border-emerald-200 dark:border-emerald-900/40 px-4 py-2">
        <span className="text-xs font-semibold text-emerald-700 dark:text-emerald-400">
          Saldo disponible:{" "}
          {puedeVerMontos
            ? formatCurrency(saldoDisponible, moneda)
            : <span className="text-slate-400" title="Sin permiso para ver montos">—</span>
          }
        </span>
      </div>

      {/* Buscador de reserva destino */}
      <div className="space-y-1">
        <label className="text-xs font-semibold text-slate-600 dark:text-slate-400">
          Reserva destino
          <span className="ml-1 font-normal text-slate-400">
            ({reservasConDeuda.length} con deuda en {moneda === "USD" ? "US$" : "$"})
          </span>
        </label>
        <div className="relative">
          <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400 pointer-events-none" />
          <input
            type="text"
            placeholder="Buscar por número de reserva o archivo..."
            value={busquedaReserva}
            onChange={(e) => {
              setBusquedaReserva(e.target.value);
              if (reservaDestinoSeleccionada) setReservaDestinoSeleccionada(null);
            }}
            disabled={guardando}
            className="w-full rounded-lg border border-slate-200 bg-white py-2 pl-9 pr-3 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
            data-testid="saldo-operador-buscador"
          />
        </div>
      </div>

      {/* Lista de reservas destino */}
      {reservasFiltradas.length === 0 ? (
        <p className="text-xs text-center text-slate-500 dark:text-slate-400 py-2">
          No hay reservas que coincidan con la búsqueda.
        </p>
      ) : (
        <div
          className="max-h-48 overflow-y-auto rounded-lg border border-slate-200 dark:border-slate-700 divide-y divide-slate-100 dark:divide-slate-800"
          role="listbox"
          aria-label="Reservas destino con deuda"
        >
          {reservasFiltradas.map((reserva) => {
            const reservaId = String(reserva.reservaPublicId ?? getPublicId(reserva));
            const estaSeleccionada = getPublicId(reservaDestinoSeleccionada) === reservaId ||
              String(reservaDestinoSeleccionada?.reservaPublicId) === reservaId;

            // Deuda de esta reserva en la moneda del cartel (para mostrar cuánto le debe el proveedor aquí)
            const lineaMoneda = (reserva.currencies ?? []).find((c) => c.currency === moneda);
            const deudaEnMoneda = lineaMoneda ? (lineaMoneda.balance ?? 0) : 0;

            return (
              <button
                key={reservaId}
                type="button"
                onClick={() => handleSeleccionarReserva({ ...reserva, reservaPublicId: reservaId })}
                disabled={guardando}
                role="option"
                aria-selected={estaSeleccionada}
                className={`w-full text-left px-4 py-2.5 flex items-center justify-between gap-3 transition-colors ${
                  estaSeleccionada
                    ? "bg-emerald-100 dark:bg-emerald-900/30"
                    : "bg-white dark:bg-slate-800 hover:bg-slate-50 dark:hover:bg-slate-700/50"
                } disabled:opacity-50`}
                data-testid={`reserva-destino-${reservaId}`}
              >
                <div className="min-w-0">
                  <p className="text-sm font-semibold text-slate-800 dark:text-slate-200 truncate">
                    {reserva.numeroReserva ?? "—"}
                  </p>
                  {reserva.fileName && (
                    <p className="text-xs text-slate-500 dark:text-slate-400 truncate">
                      {reserva.fileName}
                    </p>
                  )}
                </div>
                {/* Deuda en la moneda: muestra cuánto se puede reducir con el saldo */}
                <div className="flex-shrink-0 text-right">
                  <span className="text-xs font-bold text-rose-600 dark:text-rose-400">
                    {puedeVerMontos ? `Debe: ${formatCurrency(deudaEnMoneda, moneda)}` : "Debe: —"}
                  </span>
                </div>
              </button>
            );
          })}
        </div>
      )}

      {/* Reserva elegida: confirmación visual */}
      {reservaDestinoSeleccionada && (
        <p className="text-xs text-emerald-700 dark:text-emerald-400">
          Reserva elegida: <strong>{reservaDestinoSeleccionada.numeroReserva ?? "—"}</strong>
          {reservaDestinoSeleccionada.fileName && ` — ${reservaDestinoSeleccionada.fileName}`}
        </p>
      )}

      {/* Monto a aplicar */}
      <div className="space-y-1">
        <label
          htmlFor="monto-saldo-operador"
          className="text-xs font-semibold text-slate-600 dark:text-slate-400"
        >
          Monto a aplicar
          <span className="ml-1 font-normal text-slate-400">
            (máx. {formatCurrency(saldoDisponible, moneda)})
          </span>
        </label>
        <input
          id="monto-saldo-operador"
          type="number"
          step="0.01"
          min="0.01"
          max={saldoDisponible}
          value={monto}
          onChange={(e) => {
            setMonto(e.target.value);
            setErrorValidacion(null);
            setErrorGuardar(null);
          }}
          disabled={guardando}
          placeholder="0,00"
          className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
          data-testid="saldo-operador-monto"
        />
        {errorValidacion && (
          <p className="text-xs text-rose-600 dark:text-rose-400" role="alert">
            {errorValidacion}
          </p>
        )}
      </div>

      {/* Línea de resumen antes de confirmar */}
      {reservaDestinoSeleccionada && monto && parseFloat(monto) > 0 && (
        <div className="rounded-lg bg-emerald-50 dark:bg-emerald-950/20 border border-emerald-200 dark:border-emerald-900/40 px-4 py-2.5">
          <p className="text-xs text-emerald-700 dark:text-emerald-400">
            Se van a aplicar{" "}
            <strong>{formatCurrency(parseFloat(monto), moneda)}</strong> del saldo a favor
            a la reserva <strong>{reservaDestinoSeleccionada.numeroReserva ?? "—"}</strong>.
          </p>
        </div>
      )}

      {/* Error del backend */}
      {errorGuardar && (
        <div
          className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-xs text-rose-700 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
          role="alert"
          data-testid="saldo-operador-error"
        >
          {errorGuardar}
        </div>
      )}

      {/* Botones: anti-doble-click */}
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
          data-testid="saldo-operador-confirmar"
        >
          {guardando ? (
            <>
              <Loader2 className="h-4 w-4 animate-spin" />
              Aplicando…
            </>
          ) : (
            <>
              <TrendingUp className="h-4 w-4" />
              Aplicar saldo
            </>
          )}
        </button>
      </div>
    </div>
  );
}

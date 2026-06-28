/**
 * Ficha EN LÍNEA para usar el saldo a favor de un cliente.
 *
 * Se despliega debajo del cartel "A FAVOR" en CustomerAccountPage,
 * con el mismo patrón visual que RegistrarCobroInline y EmitirFacturaInline.
 *
 * Flujo 1 — Retiro (destinos 0/1/2):
 *   Llama GET /api/customers/{publicId}/available-credit para obtener los bolsillos.
 *   El usuario elige bolsillo, destino (efectivo/transferencia/dejar) y monto.
 *   Al confirmar: POST /api/client-credit-entries/{entryPublicId}/withdrawals.
 *   En 409 efectivo → tope Ley 25.345: muestra mensaje del backend sin modificar.
 *
 * Flujo 2 — Aplicar a otra reserva (destino 3 = AppliedToNewBooking):
 *   Muestra buscador + lista de reservas del cliente con saldo deudor.
 *   El usuario elige reserva destino y monto (presugerido = saldo disponible).
 *   Al confirmar: POST /api/customers/{publicId}/credit/apply {currency, amount, targetReservaPublicId}.
 *   El backend drena los bolsillos en FIFO y registra el pago en la reserva destino.
 *
 * NOTA (endpoint pendiente): para el flujo 2, idealmente el picker debería
 * mostrar solo reservas con deuda en la moneda específica. El endpoint
 * GET /customers/{id}/account/debt-by-reserva NO existe en el backend para
 * clientes (solo para proveedores). Se usa el endpoint paginator existente
 * GET /customers/{id}/account/reservas como fallback; no filtra por moneda.
 * Gaston agrega el endpoint cuando pueda. Ver prop `reservasConDeuda`.
 *
 * Permiso requerido: cobranzas.edit (verificado en el padre; este componente
 * no se monta si no hay permiso).
 *
 * Props:
 *   - publicId: string — publicId del cliente
 *   - moneda: "ARS" | "USD" — moneda del cartel que abrió la ficha
 *   - saldoDisponible: number — balance disponible para aplicar (del cartel padre)
 *   - reservasConDeuda: Array — lista de reservas con balance > 0 (del padre)
 *   - onConfirmado: () => void — callback al éxito (la página recarga)
 *   - onCancelar: () => void
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { ArrowDownToLine, Loader2, Search, Wallet, X } from "lucide-react";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import {
  DESTINOS_RETIRO,
  armarPayloadAplicacion,
  armarPayloadRetiro,
  formatearDescripcionEntry,
  validarAplicacion,
  validarMontoRetiro,
} from "../lib/creditWithdrawalLogic";
import { RecuadroCuentaBancaria } from "../../bank-accounts/components/RecuadroCuentaBancaria";
import { OWNER_TYPE } from "../../bank-accounts/lib/bankAccountLogic";

export function UsarSaldoAFavorInline({
  publicId,
  moneda,
  saldoDisponible = 0,
  reservasConDeuda = [],
  onConfirmado,
  onCancelar,
}) {
  // ─── Estado de carga de los bolsillos (flujo 0/1/2) ─────────────────────────
  const [entries, setEntries] = useState([]);
  const [loadingEntries, setLoadingEntries] = useState(true);
  const [errorCarga, setErrorCarga] = useState(null);

  // ─── Estado compartido del formulario ────────────────────────────────────────
  const [entrySeleccionado, setEntrySeleccionado] = useState(null);
  const [kindDestino, setKindDestino] = useState(2); // default: Transferencia
  const [monto, setMonto] = useState("");
  const [referencia, setReferencia] = useState(""); // solo para transferencia
  const [errorValidacion, setErrorValidacion] = useState(null);

  // ─── Estado específico del flujo 3 (Aplicar a otra reserva) ─────────────────
  const [busquedaReserva, setBusquedaReserva] = useState("");
  const [reservaDestinoSeleccionada, setReservaDestinoSeleccionada] = useState(null);

  // ─── Estado del submit ───────────────────────────────────────────────────────
  const [guardando, setGuardando] = useState(false);
  const [errorGuardar, setErrorGuardar] = useState(null);

  // Carga los bolsillos disponibles al montar (solo se usan en flujos 0/1/2).
  // useEffect con [] corre una sola vez: cuando la ficha se despliega.
  useEffect(() => {
    async function cargarEntries() {
      setLoadingEntries(true);
      setErrorCarga(null);
      try {
        const datos = await api.get(`/customers/${publicId}/available-credit`);

        // El backend devuelve solo bolsillos con remainingBalance > 0, orden FIFO.
        // Filtramos por la moneda del cartel: nunca mezclamos ARS con USD.
        const todosLosEntries = Array.isArray(datos) ? datos : [];
        const entriesDeLaMoneda = moneda
          ? todosLosEntries.filter((entry) => entry.currency === moneda)
          : todosLosEntries;

        setEntries(entriesDeLaMoneda);

        if (entriesDeLaMoneda.length > 0) {
          const primerEntry = entriesDeLaMoneda[0];
          setEntrySeleccionado(primerEntry);
          setMonto(String(primerEntry.remainingBalance));
        }
      } catch (error) {
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

  // Cuando el usuario cambia el bolsillo, actualizamos el monto sugerido
  const handleCambiarEntry = useCallback((entryPublicId) => {
    const entry = entries.find((e) => e.entryPublicId === entryPublicId);
    if (entry) {
      setEntrySeleccionado(entry);
      setMonto(String(entry.remainingBalance));
      setErrorValidacion(null);
      setErrorGuardar(null);
    }
  }, [entries]);

  // Cuando cambia el tipo de destino, limpiamos errores y ajustamos el monto sugerido.
  // Para kind 3 (aplicar a reserva): precargar con el saldo disponible completo.
  // Para kinds 0/1/2: mantener el monto del bolsillo seleccionado.
  const handleCambiarDestino = useCallback((nuevoKind) => {
    setKindDestino(nuevoKind);
    setErrorValidacion(null);
    setErrorGuardar(null);
    setBusquedaReserva("");
    setReservaDestinoSeleccionada(null);

    if (nuevoKind === 3) {
      // Monto sugerido = saldo disponible completo (el usuario puede reducirlo)
      setMonto(String(saldoDisponible));
    } else if (entrySeleccionado) {
      // Volver al monto del bolsillo seleccionado
      setMonto(String(entrySeleccionado.remainingBalance));
    }
  }, [saldoDisponible, entrySeleccionado]);

  // Filtra la lista de reservas destino según lo que el usuario escribe en el buscador.
  // Filtra por numeroReserva o name (client-side, sin nueva llamada al backend).
  const reservasFiltradas = useMemo(() => {
    if (!busquedaReserva.trim()) return reservasConDeuda;
    const texto = busquedaReserva.toLowerCase().trim();
    // Las reservas del nuevo endpoint tienen `fileName` (no `name`).
    // Buscamos en ambos para tolerar formas legacy si llega alguna.
    return reservasConDeuda.filter(
      (r) =>
        (r.numeroReserva || "").toLowerCase().includes(texto) ||
        (r.fileName || "").toLowerCase().includes(texto) ||
        (r.name || "").toLowerCase().includes(texto)
    );
  }, [reservasConDeuda, busquedaReserva]);

  const handleConfirmar = async () => {
    setErrorGuardar(null);
    setErrorValidacion(null);

    if (kindDestino === 3) {
      // ── Flujo "Aplicar a otra reserva" ───────────────────────────────────────
      // Las reservas del nuevo endpoint tienen reservaPublicId (no publicId).
      // getPublicId como fallback para compatibilidad con formas legacy.
      const targetReservaPublicId =
        reservaDestinoSeleccionada?.reservaPublicId ?? getPublicId(reservaDestinoSeleccionada);

      const errorAplicacion = validarAplicacion(monto, saldoDisponible, targetReservaPublicId);
      if (errorAplicacion) {
        setErrorValidacion(errorAplicacion);
        return;
      }

      setGuardando(true);
      try {
        const payload = armarPayloadAplicacion(moneda, monto, targetReservaPublicId);
        await api.post(`/customers/${publicId}/credit/apply`, payload);
        showSuccess(
          `El saldo fue aplicado a la reserva ${reservaDestinoSeleccionada.numeroReserva ?? ""}.`
        );
        onConfirmado();
      } catch (error) {
        setErrorGuardar(
          getApiErrorMessage(error, "No se pudo aplicar el saldo. Revisá la conexión y volvé a intentar.")
        );
      } finally {
        setGuardando(false);
      }
      return;
    }

    // ── Flujo retiro (kind 0/1/2) ─────────────────────────────────────────────
    if (kindDestino !== 0) {
      const errorMonto = validarMontoRetiro(monto, entrySeleccionado?.remainingBalance ?? 0);
      if (errorMonto) {
        setErrorValidacion(errorMonto);
        return;
      }
    }

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
      // para devolución en efectivo. Mostramos el mensaje tal cual viene.
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

  // ─── Render: estado de carga (solo bloquea si el flujo actual necesita entries) ──
  if (loadingEntries && kindDestino !== 3) {
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

  // ─── Render: error al cargar los bolsillos ────────────────────────────────────
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

  // ─── Render: sin bolsillos disponibles (solo si NO es kind 3) ────────────────
  if (entries.length === 0 && kindDestino !== 3) {
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

  // ─── Render: formulario principal ────────────────────────────────────────────
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

      {/* Selector de bolsillo: visible solo para flujo retiro (0/1/2) y cuando hay más de uno */}
      {kindDestino !== 3 && entries.length > 1 && (
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

      {/* Bolsillo único: muestra el nombre sin select */}
      {kindDestino !== 3 && entries.length === 1 && entrySeleccionado && (
        <p className="text-sm text-slate-700 dark:text-slate-300">
          {formatearDescripcionEntry(entrySeleccionado)}
        </p>
      )}

      {/* Selector de destino: siempre visible */}
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
          onChange={(e) => handleCambiarDestino(Number(e.target.value))}
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

      {/* Cuenta bancaria del cliente para hacer la devolución por transferencia.
          Decisión de Gaston: "sí, mostrar el CBU del cliente con Copiar al devolver".
          Solo se muestra cuando el destino elegido es Transferencia (kindDestino = 2).
          Si el cliente no tiene cuenta cargada, mostramos un aviso suave pero NO bloqueamos
          la operación: el agente puede igualmente registrar el retiro con la referencia manual. */}
      {kindDestino === 2 && (
        <RecuadroCuentaBancaria
          ownerType={OWNER_TYPE.Customer}
          ownerId={publicId}
          moneda={moneda}
          titulo="Cuenta del cliente para transferir"
          mensajeSinCuenta="Este cliente no tiene cuenta bancaria cargada."
        />
      )}

      {/* ── FLUJO 3: Picker de reserva destino ─────────────────────────────────── */}
      {kindDestino === 3 && (
        <div className="space-y-3">
          {/* Buscador */}
          <div className="relative">
            <Search className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400 pointer-events-none" />
            <input
              type="text"
              placeholder="Buscar por número de reserva o nombre..."
              value={busquedaReserva}
              onChange={(e) => {
                setBusquedaReserva(e.target.value);
                // Limpiar la selección si el usuario cambia el texto
                if (reservaDestinoSeleccionada) setReservaDestinoSeleccionada(null);
              }}
              disabled={guardando}
              className="w-full rounded-lg border border-slate-200 bg-white py-2 pl-9 pr-3 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-800 dark:text-white disabled:opacity-50"
              data-testid="saldo-buscador-reserva"
            />
          </div>

          {/* Lista de reservas destino */}
          {reservasFiltradas.length === 0 ? (
            <p className="text-xs text-slate-500 dark:text-slate-400 text-center py-3">
              {reservasConDeuda.length === 0
                ? "Este cliente no tiene reservas con saldo pendiente."
                : "No hay reservas que coincidan con la búsqueda."}
            </p>
          ) : (
            <div
              className="max-h-48 overflow-y-auto space-y-1 rounded-lg border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-800 p-1"
              role="listbox"
              aria-label="Reservas destino disponibles"
            >
              {reservasFiltradas.map((reserva) => {
                // Las reservas del nuevo endpoint tienen reservaPublicId (no publicId).
                // Usamos ese campo como ID canónico; getPublicId como fallback para legacy.
                const reservaId = reserva.reservaPublicId ?? getPublicId(reserva);
                const idSeleccionado = reservaDestinoSeleccionada?.reservaPublicId ??
                  getPublicId(reservaDestinoSeleccionada);
                const estaSeleccionada = reservaId != null && String(reservaId) === String(idSeleccionado);

                // Deuda de esta reserva en la moneda del cartel que abrió la ficha.
                // Con el nuevo endpoint, debtByCurrency[] ya está filtrado por moneda en el padre.
                const lineaDeuda = (reserva.debtByCurrency ?? []).find((c) => c.currency === moneda);
                const deudaEnMoneda = lineaDeuda?.amount ?? null;
                const simbolo = moneda === "USD" ? "US$" : "$";

                return (
                  <button
                    key={reservaId}
                    type="button"
                    onClick={() => {
                      setReservaDestinoSeleccionada(reserva);
                      setErrorValidacion(null);
                      // Monto sugerido = min(deuda de esta reserva en la moneda, saldo disponible).
                      // Si deudaEnMoneda no está disponible (endpoint legacy), dejamos el saldo completo.
                      if (deudaEnMoneda != null && deudaEnMoneda > 0) {
                        const sugerido = Math.min(deudaEnMoneda, saldoDisponible);
                        setMonto(String(sugerido));
                      }
                    }}
                    disabled={guardando}
                    role="option"
                    aria-selected={estaSeleccionada}
                    className={`w-full text-left rounded-md px-3 py-2 text-sm transition-colors flex items-center justify-between gap-2 ${
                      estaSeleccionada
                        ? "bg-emerald-100 text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200"
                        : "hover:bg-slate-50 dark:hover:bg-slate-700 text-slate-700 dark:text-slate-300"
                    } disabled:opacity-50`}
                    data-testid={`reserva-opcion-${reservaId}`}
                  >
                    <div className="min-w-0">
                      <span className="font-semibold">{reserva.numeroReserva ?? "—"}</span>
                      {(reserva.fileName || reserva.name) && (
                        <span className="ml-2 text-slate-500 dark:text-slate-400 truncate">
                          {reserva.fileName || reserva.name}
                        </span>
                      )}
                    </div>
                    {/* Monto que debe la reserva en ESTA moneda (viene del nuevo endpoint) */}
                    {deudaEnMoneda != null && (
                      <span className="flex-shrink-0 text-xs font-bold text-rose-600 dark:text-rose-400">
                        Debe: {simbolo}{Number(deudaEnMoneda).toLocaleString("es-AR", {
                          minimumFractionDigits: 2,
                          maximumFractionDigits: 2,
                        })}
                      </span>
                    )}
                  </button>
                );
              })}
            </div>
          )}

          {/* Reserva elegida: confirmación visual de la selección */}
          {reservaDestinoSeleccionada && (
            <p className="text-xs text-emerald-700 dark:text-emerald-400">
              Reserva elegida:{" "}
              <strong>{reservaDestinoSeleccionada.numeroReserva}</strong>
              {(reservaDestinoSeleccionada.fileName || reservaDestinoSeleccionada.name) && (
                ` — ${reservaDestinoSeleccionada.fileName || reservaDestinoSeleccionada.name}`
              )}
            </p>
          )}
        </div>
      )}

      {/* ── MONTO: visible para kinds 0/1/2 cuando hay plata, y siempre para kind 3 ── */}
      {(kindDestino !== 0 || kindDestino === 3) && (
        <div className="space-y-1">
          <label
            htmlFor="monto-retiro"
            className="text-xs font-semibold text-slate-600 dark:text-slate-400"
          >
            {kindDestino === 3 ? "Monto a aplicar" : "Monto a retirar"}
            {/* Para kind 3: referencia del saldo total disponible */}
            {kindDestino === 3 && (
              <span className="ml-1 font-normal text-slate-400">
                (máx. {moneda === "USD" ? "US$" : "$"}
                {Number(saldoDisponible).toLocaleString("es-AR", {
                  minimumFractionDigits: 2,
                  maximumFractionDigits: 2,
                })})
              </span>
            )}
            {/* Para flujo retiro: referencia del saldo del bolsillo seleccionado */}
            {kindDestino !== 3 && kindDestino !== 0 && entrySeleccionado && (
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
            max={kindDestino === 3 ? saldoDisponible : (entrySeleccionado?.remainingBalance ?? undefined)}
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

      {/* Referencia: solo para transferencia (kind 2) */}
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

      {/* Línea de resumen para kind 3: le muestra al usuario exactamente qué va a pasar.
          Solo visible cuando hay una reserva seleccionada y un monto válido. */}
      {kindDestino === 3 && reservaDestinoSeleccionada && monto && parseFloat(monto) > 0 && (
        <div className="rounded-lg bg-emerald-50 dark:bg-emerald-950/20 border border-emerald-200 dark:border-emerald-900/40 px-4 py-2.5">
          <p className="text-xs text-emerald-700 dark:text-emerald-400">
            Se van a aplicar{" "}
            <strong>
              {moneda === "USD" ? "US$" : "$"}
              {Number(parseFloat(monto)).toLocaleString("es-AR", {
                minimumFractionDigits: 2,
                maximumFractionDigits: 2,
              })}
            </strong>{" "}
            del saldo a favor a la reserva{" "}
            <strong>{reservaDestinoSeleccionada.numeroReserva ?? "—"}</strong>.
          </p>
        </div>
      )}

      {/* Nota preventiva para efectivo: el backend puede rechazar con 409 por Ley 25.345 */}
      {kindDestino === 1 && (
        <p className="text-xs text-amber-700 dark:text-amber-400 bg-amber-50 dark:bg-amber-950/20 border border-amber-200 dark:border-amber-900/40 rounded-lg px-3 py-2">
          Ley 25.345: los pagos en efectivo tienen un tope legal. Si el monto lo supera, el sistema lo va a rechazar y te va a indicar que uses transferencia.
        </p>
      )}

      {/* Error del backend (incluye 409 del tope de efectivo y errores de la aplicación) */}
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
              {kindDestino === 0
                ? "Cerrar aviso"
                : kindDestino === 3
                ? "Aplicar saldo"
                : "Confirmar retiro"}
            </>
          )}
        </button>
      </div>
    </div>
  );
}

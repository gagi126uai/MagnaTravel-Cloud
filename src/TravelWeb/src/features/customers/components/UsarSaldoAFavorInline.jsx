/**
 * Ficha EN LÍNEA para usar el saldo a favor de un cliente.
 *
 * Se despliega debajo de la "foto de saldo" en CustomerAccountPage,
 * con el mismo patrón visual que RegistrarCobroInline y EmitirFacturaInline.
 *
 * Flujo 1 — Retiro simple (destinos 0/1/2, SIN multas pendientes en esta moneda):
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
 * Flujo 3 — Aplicar a una multa (destino 4 = KIND_APLICAR_A_MULTA, Tanda D1 2026-07-16):
 *   El usuario elige UNA multa firme de la lista (openPenalties de la previa del
 *   neteo) y un monto (sugerido = mínimo entre lo que falta cobrar y el saldo).
 *   Al confirmar: POST /api/customers/{publicId}/credit/apply-to-penalty.
 *
 * Flujo 4 — Devolver con neteo (destinos 1/2, CON multas firmes pendientes en esta
 * moneda, Tanda D1 2026-07-16):
 *   Antes de mostrar el monto a devolver, se pide GET .../credit/refund-netting-preview
 *   (desglose: saldo a favor − multas abiertas = neto). El usuario NO teclea ningún
 *   monto: confirma el neto que ya calculó el servidor. Al confirmar:
 *   POST /api/customers/{publicId}/credit/refund-with-netting.
 *   Si NO hay multas firmes pendientes en esta moneda, este flujo no se activa y el
 *   destino 1/2 se comporta exactamente como el Flujo 1 de siempre.
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
 *   - pendingPenaltyItems: Array — overview.pendingPenalties.items del padre (solo se usa
 *     para completar el nombre del expediente en el picker de multas; el monto SIEMPRE
 *     sale de la previa del neteo, nunca de acá — ver enriquecerMultasAplicables).
 *   - onConfirmado: () => void — callback al éxito (la página recarga)
 *   - onCancelar: () => void
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { ArrowDownToLine, Loader2, RefreshCw, Search, Wallet, X } from "lucide-react";
import { api } from "../../../api";
import { showSuccess } from "../../../alerts";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";
import {
  DESTINOS_RETIRO,
  KIND_APLICAR_A_MULTA,
  armarPayloadAplicacion,
  armarPayloadAplicacionAMulta,
  armarPayloadRefundConNeteo,
  armarPayloadRetiro,
  armarMensajeExitoNeteo,
  enriquecerMultasAplicables,
  formatearDescripcionEntry,
  montoSugeridoAplicacionAMulta,
  previewsDifierenSignificativamente,
  validarAplicacion,
  validarAplicacionAMulta,
  validarMontoRetiro,
} from "../lib/creditWithdrawalLogic";
import { RecuadroCuentaBancaria } from "../../bank-accounts/components/RecuadroCuentaBancaria";
import { OWNER_TYPE } from "../../bank-accounts/lib/bankAccountLogic";

export function UsarSaldoAFavorInline({
  publicId,
  moneda,
  saldoDisponible = 0,
  reservasConDeuda = [],
  pendingPenaltyItems = [],
  onConfirmado,
  onCancelar,
}) {
  // ─── Estado de carga de los bolsillos (flujo 0/1/2 SIN neteo, y flujo 3) ────────
  const [entries, setEntries] = useState([]);
  const [loadingEntries, setLoadingEntries] = useState(true);
  const [errorCarga, setErrorCarga] = useState(null);

  // ─── Estado compartido del formulario ────────────────────────────────────────
  const [entrySeleccionado, setEntrySeleccionado] = useState(null);
  const [kindDestino, setKindDestino] = useState(2); // default: Transferencia
  const [monto, setMonto] = useState("");
  const [referencia, setReferencia] = useState(""); // solo para transferencia
  const [errorValidacion, setErrorValidacion] = useState(null);

  // ─── Estado específico del flujo 2 (Aplicar a otra reserva) ─────────────────
  const [busquedaReserva, setBusquedaReserva] = useState("");
  const [reservaDestinoSeleccionada, setReservaDestinoSeleccionada] = useState(null);

  // ─── Estado específico del flujo 3 (Aplicar a una multa, Tanda D1) ──────────
  const [multaSeleccionada, setMultaSeleccionada] = useState(null);

  // ─── Estado de la previa del neteo (Tanda D1: sirve para el flujo 3 -lista de
  // multas- Y el flujo 4 -desglose de la devolución-, es UNA sola llamada) ─────
  const [nettingPreview, setNettingPreview] = useState(null);
  const [loadingNettingPreview, setLoadingNettingPreview] = useState(true);
  const [errorNettingPreview, setErrorNettingPreview] = useState(null);
  // Aviso "la cuenta cambió mientras mirabas esta pantalla" (spec §9): se prende cuando,
  // justo antes de confirmar una devolución con neteo, la previa recién pedida difiere
  // de la que el usuario tenía en pantalla.
  const [avisoPreviaDesactualizada, setAvisoPreviaDesactualizada] = useState(false);

  // ─── Estado del submit ───────────────────────────────────────────────────────
  const [guardando, setGuardando] = useState(false);
  const [errorGuardar, setErrorGuardar] = useState(null);

  // Carga los bolsillos disponibles al montar (los usan los flujos 0/1/2-sin-neteo y 3).
  // useEffect con [publicId, moneda]: si el usuario reabre la ficha con otra moneda
  // (poco común, la ficha se remonta por cartel) vuelve a pedir los bolsillos de esa moneda.
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
        // Fix de revisión (2026-07-17, gate de exposición): antes se volcaba el error
        // COMPLETO a la consola (podía traer payload/stack técnico). Log acotado, mismo
        // criterio que el resto de la pantalla (ver loadDeudaClientePorReserva en
        // CustomerAccountPage.jsx): solo el mensaje, nunca el objeto entero.
        console.warn("[UsarSaldoAFavorInline] No se pudo cargar el saldo disponible:", error?.message);
      } finally {
        setLoadingEntries(false);
      }
    }

    cargarEntries();
  }, [publicId, moneda]);

  // Pide la previa del neteo (Tanda D1): sirve TANTO para la lista de multas
  // aplicables (destino 4) COMO para el desglose de la devolución (destinos 1/2)
  // — es la MISMA llamada, así que se pide una sola vez al montar la ficha, no por
  // cada destino que el usuario prueba.
  const cargarNettingPreview = useCallback(async () => {
    setLoadingNettingPreview(true);
    setErrorNettingPreview(null);
    try {
      const datos = await api.get(
        `/customers/${publicId}/credit/refund-netting-preview?currency=${encodeURIComponent(moneda)}`
      );
      setNettingPreview(datos);
      return datos;
    } catch (error) {
      setErrorNettingPreview(
        getApiErrorMessage(error, "No se pudo cargar la información de multas de esta cuenta. Intentá de nuevo.")
      );
      return null;
    } finally {
      setLoadingNettingPreview(false);
    }
  }, [publicId, moneda]);

  useEffect(() => {
    cargarNettingPreview();
  }, [cargarNettingPreview]);

  // Multas aplicables, ya con el nombre del expediente cruzado (ver comentario de la
  // función: el MONTO siempre sale de la previa, nunca del dato bruto de pendingPenalties).
  const multasAplicables = useMemo(
    () => enriquecerMultasAplicables(nettingPreview?.openPenalties, pendingPenaltyItems),
    [nettingPreview, pendingPenaltyItems]
  );
  const hayMultasAplicables = multasAplicables.length > 0;

  // "Modo neteo": el destino elegido es una devolución (efectivo/transferencia) Y esta
  // moneda tiene al menos una multa firme pendiente. Mientras la previa está cargando o
  // falló, NO se sabe todavía — se muestra un estado de carga/error propio en vez de
  // arriesgar a mostrar el formulario viejo por un instante y después cambiarlo de golpe.
  const decidiendoModoDevolucion = (kindDestino === 1 || kindDestino === 2) && loadingNettingPreview;
  const enModoNeteo = (kindDestino === 1 || kindDestino === 2) && !loadingNettingPreview && !errorNettingPreview && hayMultasAplicables;

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
  // Para kind 4 (aplicar a multa): arranca sin monto hasta que el usuario elija la multa.
  // Para kinds 0/1/2: mantener el monto del bolsillo seleccionado (el modo neteo, si
  // corresponde, ignora este monto — usa el neto que calcula el servidor).
  const handleCambiarDestino = useCallback((nuevoKind) => {
    setKindDestino(nuevoKind);
    setErrorValidacion(null);
    setErrorGuardar(null);
    setAvisoPreviaDesactualizada(false);
    setBusquedaReserva("");
    setReservaDestinoSeleccionada(null);
    setMultaSeleccionada(null);

    if (nuevoKind === 3) {
      // Monto sugerido = saldo disponible completo (el usuario puede reducirlo)
      setMonto(String(saldoDisponible));
    } else if (nuevoKind === KIND_APLICAR_A_MULTA) {
      setMonto("");
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

  // Elegir una multa en el picker del flujo 3: precarga el monto sugerido.
  const handleSeleccionarMulta = useCallback((multa) => {
    setMultaSeleccionada(multa);
    setErrorValidacion(null);
    setErrorGuardar(null);
    setMonto(String(montoSugeridoAplicacionAMulta(multa, saldoDisponible)));
  }, [saldoDisponible]);

  // ── Flujo 2: "Aplicar a otra reserva" (destino 3, sin cambios de la Tanda D1) ────
  const handleConfirmarAplicacionReserva = async () => {
    // Las reservas del nuevo endpoint tienen reservaPublicId (no publicId).
    // getPublicId como fallback para compatibilidad con formas legacy.
    const targetReservaPublicId =
      reservaDestinoSeleccionada?.reservaPublicId ?? getPublicId(reservaDestinoSeleccionada);

    const errorAplicacion = validarAplicacion(monto, saldoDisponible, targetReservaPublicId, moneda);
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
  };

  // ── Flujo 3: "Aplicar a una multa" (destino 4, Tanda D1) ─────────────────────────
  const handleConfirmarAplicacionAMulta = async () => {
    const error = validarAplicacionAMulta(monto, multaSeleccionada, saldoDisponible, moneda);
    if (error) {
      setErrorValidacion(error);
      return;
    }

    setGuardando(true);
    try {
      const payload = armarPayloadAplicacionAMulta(moneda, monto, multaSeleccionada.debitNotePublicId);
      await api.post(`/customers/${publicId}/credit/apply-to-penalty`, payload);
      showSuccess(
        `El saldo a favor se aplicó a la multa de la reserva ${multaSeleccionada.numeroReserva ?? ""}.`
      );
      onConfirmado();
    } catch (error) {
      setErrorGuardar(
        getApiErrorMessage(error, "No se pudo aplicar el saldo a la multa. Revisá la conexión y volvé a intentar.")
      );
    } finally {
      setGuardando(false);
    }
  };

  // ── Flujo 4: "Devolver con neteo" (destinos 1/2 CON multas firmes, Tanda D1) ─────
  // Antes de mandar la plata, se vuelve a pedir la previa (revalidación, spec §9): el
  // backend de este endpoint no rechaza con un 409 de "cambió" — SIEMPRE recalcula
  // fresco y aplica ese resultado. Para no confirmar con números viejos, la revalidación
  // se hace ACÁ: si la previa fresca difiere de la que el usuario está mirando, se
  // avisa y se actualiza la pantalla, sin mandar nada todavía (el usuario confirma de
  // nuevo, ahora sí contra los números actualizados).
  const handleConfirmarNeteo = async () => {
    setErrorGuardar(null);
    setAvisoPreviaDesactualizada(false);
    setGuardando(true);
    try {
      const previaFresca = await cargarNettingPreview();
      if (!previaFresca) {
        // cargarNettingPreview ya seteó errorNettingPreview; no hay nada más para hacer acá.
        return;
      }
      if (previewsDifierenSignificativamente(nettingPreview, previaFresca)) {
        setAvisoPreviaDesactualizada(true);
        return;
      }

      const payload = armarPayloadRefundConNeteo(moneda, kindDestino, referencia);
      const resultado = await api.post(`/customers/${publicId}/credit/refund-with-netting`, payload);
      showSuccess(armarMensajeExitoNeteo(resultado, nettingPreview?.openPenalties ?? []));
      onConfirmado();
    } catch (error) {
      setErrorGuardar(
        getApiErrorMessage(error, "No se pudo registrar la devolución. Revisá la conexión y volvé a intentar.")
      );
    } finally {
      setGuardando(false);
    }
  };

  // ── Flujo 1: retiro simple (destinos 0/1/2 SIN multas pendientes en esta moneda) ──
  const handleConfirmarRetiro = async () => {
    if (kindDestino !== 0) {
      const errorMonto = validarMontoRetiro(monto, entrySeleccionado?.remainingBalance ?? 0, entrySeleccionado?.currency ?? moneda);
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

  // Dispatcher único del botón principal: cada destino tiene su propio handler de
  // validación + submit (arriba). Centralizado acá para que el botón y su texto no se
  // desincronicen de a cuál handler corresponde.
  const handleConfirmar = () => {
    setErrorGuardar(null);
    setErrorValidacion(null);
    if (kindDestino === 3) return handleConfirmarAplicacionReserva();
    if (kindDestino === KIND_APLICAR_A_MULTA) return handleConfirmarAplicacionAMulta();
    if (enModoNeteo) return handleConfirmarNeteo();
    return handleConfirmarRetiro();
  };

  // ─── Render: estado de carga (solo bloquea si el flujo actual necesita entries) ──
  if (loadingEntries && kindDestino !== 3 && kindDestino !== KIND_APLICAR_A_MULTA) {
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

  // ─── Render: sin bolsillos disponibles (solo si el flujo actual los necesita) ────
  if (entries.length === 0 && kindDestino !== 3 && kindDestino !== KIND_APLICAR_A_MULTA) {
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

  // Se necesita saber la moneda para el botón: si es "sin multas aplicables" (kind 4)
  // o el estado de carga/error de la previa, esos bloques quedan más abajo, DENTRO del
  // formulario (no reemplazan la ficha entera, a diferencia de los estados de arriba).

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

      {/* Selector de bolsillo: visible solo para el flujo retiro simple (0/1/2 sin
          neteo) y cuando hay más de uno. En modo neteo (obra b) no hay bolsillo que
          elegir: el servidor drena todos en FIFO. */}
      {kindDestino !== 3 && kindDestino !== KIND_APLICAR_A_MULTA && !enModoNeteo && !decidiendoModoDevolucion && entries.length > 1 && (
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
      {kindDestino !== 3 && kindDestino !== KIND_APLICAR_A_MULTA && !enModoNeteo && !decidiendoModoDevolucion && entries.length === 1 && entrySeleccionado && (
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
          Solo se muestra cuando el destino elegido es Transferencia (kindDestino = 2),
          CON o SIN neteo (en los dos casos hay que transferir a la misma cuenta). */}
      {kindDestino === 2 && (
        <RecuadroCuentaBancaria
          ownerType={OWNER_TYPE.Customer}
          ownerId={publicId}
          moneda={moneda}
          titulo="Cuenta del cliente para transferir"
          mensajeSinCuenta="Este cliente no tiene cuenta bancaria cargada."
        />
      )}

      {/* Mientras se decide si el destino 1/2 necesita el modo neteo (todavía no
          resolvió la previa), no se muestra ni el formulario viejo ni el nuevo — evita
          el parpadeo de un formulario que cambia de golpe apenas llega la respuesta. */}
      {decidiendoModoDevolucion && (
        <div className="flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400 py-1">
          <Loader2 className="h-3.5 w-3.5 animate-spin" />
          Cargando…
        </div>
      )}

      {/* Error al cargar la previa del neteo, para los destinos que la necesitan
          (1/2/4). Con "Reintentar" en línea, sin perder la selección del usuario. */}
      {(kindDestino === 1 || kindDestino === 2 || kindDestino === KIND_APLICAR_A_MULTA) &&
        !loadingNettingPreview &&
        errorNettingPreview && (
          <div
            className="flex flex-col gap-2 rounded-lg border border-rose-200 bg-rose-50 p-3 text-xs text-rose-700 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300"
            role="alert"
            data-testid="netting-preview-error"
          >
            <span>{errorNettingPreview}</span>
            <button
              type="button"
              onClick={cargarNettingPreview}
              className="inline-flex items-center gap-1.5 self-start rounded-lg border border-rose-300 bg-white px-3 py-1.5 text-xs font-bold text-rose-700 hover:bg-rose-50 dark:bg-slate-800 dark:text-rose-200 dark:border-rose-800"
            >
              <RefreshCw className="h-3.5 w-3.5" />
              Reintentar
            </button>
          </div>
        )}

      {/* ── FLUJO 4: previa del neteo (destinos 1/2 CON multas firmes) ─────────────── */}
      {enModoNeteo && (
        <div className="space-y-3">
          {avisoPreviaDesactualizada && (
            <div
              className="rounded-lg border border-amber-300 bg-amber-50 p-3 text-xs text-amber-800 dark:border-amber-800 dark:bg-amber-950/30 dark:text-amber-200"
              role="alert"
              data-testid="netting-previa-desactualizada"
            >
              La cuenta cambió mientras mirabas esta pantalla. Actualizamos los números;
              revisá y volvé a confirmar.
            </div>
          )}

          <div>
            <p className="text-xs text-slate-600 dark:text-slate-400 mb-1.5">
              Este cliente tiene una multa sin pagar. Antes de devolverle, se descuenta lo
              que debe:
            </p>
            <div className="space-y-1 rounded-lg border border-slate-200 bg-white dark:border-slate-700 dark:bg-slate-800 p-3 text-sm" data-testid="netting-desglose">
              <div className="flex items-center justify-between">
                <span className="text-slate-600 dark:text-slate-400">Saldo a favor</span>
                <span className="font-semibold text-slate-900 dark:text-white">
                  {formatCurrency(nettingPreview?.availableCredit ?? 0, moneda)}
                </span>
              </div>
              {(nettingPreview?.openPenalties ?? []).map((multa) => (
                <div key={String(multa.debitNotePublicId)} className="flex items-center justify-between text-rose-600 dark:text-rose-400">
                  <span>− Multa {multa.numeroReserva} (por anulación)</span>
                  <span>−{formatCurrency(multa.outstandingAmount, moneda)}</span>
                </div>
              ))}
              <div className="flex items-center justify-between border-t border-slate-200 dark:border-slate-700 pt-1.5 mt-1.5 font-bold text-slate-900 dark:text-white">
                <span>Le devolvés</span>
                <span data-testid="netting-neto">{formatCurrency(nettingPreview?.netToRefund ?? 0, moneda)}</span>
              </div>
            </div>
            {Number(nettingPreview?.netToRefund ?? 0) <= 0 && (
              <p className="mt-1.5 text-xs text-amber-700 dark:text-amber-400">
                Todo el saldo a favor se usa para la multa; no queda nada para devolver.
              </p>
            )}
          </div>

          {/* Referencia: solo para transferencia (kind 2), igual que en el flujo simple */}
          {kindDestino === 2 && (
            <div className="space-y-1">
              <label htmlFor="referencia-retiro-neteo" className="text-xs font-semibold text-slate-600 dark:text-slate-400">
                Referencia de transferencia (opcional)
              </label>
              <input
                id="referencia-retiro-neteo"
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
        </div>
      )}

      {/* ── FLUJO 3: picker de multa (destino 4) ────────────────────────────────────── */}
      {kindDestino === KIND_APLICAR_A_MULTA && !loadingNettingPreview && !errorNettingPreview && (
        <div className="space-y-2">
          <p className="text-xs font-semibold text-slate-600 dark:text-slate-400">¿A qué multa?</p>
          {!hayMultasAplicables ? (
            <p className="text-xs text-slate-500 dark:text-slate-400" data-testid="sin-multas-aplicables">
              No hay multas que puedas saldar con saldo a favor.
              <br />
              (Las multas que todavía no tienen comprobante emitido no se pueden saldar
              hasta que el comprobante salga.)
            </p>
          ) : (
            <div
              className="max-h-48 overflow-y-auto space-y-1 rounded-lg border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-800 p-1"
              role="listbox"
              aria-label="Multas aplicables"
            >
              {multasAplicables.map((multa) => {
                const estaSeleccionada = multaSeleccionada
                  && String(multaSeleccionada.debitNotePublicId) === String(multa.debitNotePublicId);
                return (
                  <button
                    key={String(multa.debitNotePublicId)}
                    type="button"
                    onClick={() => handleSeleccionarMulta(multa)}
                    disabled={guardando}
                    role="option"
                    aria-selected={estaSeleccionada}
                    className={`w-full text-left rounded-md px-3 py-2 text-sm transition-colors flex items-center justify-between gap-2 ${
                      estaSeleccionada
                        ? "bg-emerald-100 text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200"
                        : "hover:bg-slate-50 dark:hover:bg-slate-700 text-slate-700 dark:text-slate-300"
                    } disabled:opacity-50`}
                    data-testid={`multa-opcion-${multa.debitNotePublicId}`}
                  >
                    <div className="min-w-0">
                      <span className="font-semibold">{multa.numeroReserva ?? "—"}</span>
                      {multa.name && <span className="ml-2 text-slate-500 dark:text-slate-400 truncate">{multa.name}</span>}
                      <span className="ml-2 text-slate-400 dark:text-slate-500">— Multa por anulación</span>
                    </div>
                    <span className="flex-shrink-0 text-xs font-bold text-rose-600 dark:text-rose-400">
                      Falta cobrar: {formatCurrency(multa.outstandingAmount, moneda)}
                    </span>
                  </button>
                );
              })}
            </div>
          )}
        </div>
      )}

      {/* ── FLUJO 2: Picker de reserva destino (destino 3, sin cambios) ────────────── */}
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
                        Debe: {formatCurrency(deudaEnMoneda, moneda)}
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

      {/* ── MONTO: kind 3 (aplicar a reserva), kind 4 CON multa elegida, y el flujo
          simple 1/2 (sin neteo). En modo neteo (obra b) NO hay casillero: se devuelve
          el neto completo de una (spec P4=A). */}
      {((kindDestino !== 0 && !enModoNeteo && !decidiendoModoDevolucion && kindDestino !== KIND_APLICAR_A_MULTA)
        || kindDestino === 3
        || (kindDestino === KIND_APLICAR_A_MULTA && multaSeleccionada)) && (
        <div className="space-y-1">
          <label
            htmlFor="monto-retiro"
            className="text-xs font-semibold text-slate-600 dark:text-slate-400"
          >
            {kindDestino === 3 || kindDestino === KIND_APLICAR_A_MULTA ? "Monto a aplicar" : "Monto a retirar"}
            {/* Para kind 3: referencia del saldo total disponible */}
            {kindDestino === 3 && (
              <span className="ml-1 font-normal text-slate-400">
                (máx. {formatCurrency(saldoDisponible, moneda)})
              </span>
            )}
            {/* Para kind 4: referencia del tope real (lo menor entre la multa y el saldo) */}
            {kindDestino === KIND_APLICAR_A_MULTA && multaSeleccionada && (
              <span className="ml-1 font-normal text-slate-400">
                (máx. {formatCurrency(montoSugeridoAplicacionAMulta(multaSeleccionada, saldoDisponible), moneda)})
              </span>
            )}
            {/* Para flujo retiro simple: referencia del saldo del bolsillo seleccionado */}
            {kindDestino !== 3 && kindDestino !== KIND_APLICAR_A_MULTA && kindDestino !== 0 && entrySeleccionado && (
              <span className="ml-1 font-normal text-slate-400">
                (máx. {formatCurrency(entrySeleccionado.remainingBalance, entrySeleccionado.currency)})
              </span>
            )}
          </label>
          <input
            id="monto-retiro"
            type="number"
            step="0.01"
            min="0.01"
            max={
              kindDestino === 3
                ? saldoDisponible
                : kindDestino === KIND_APLICAR_A_MULTA
                ? montoSugeridoAplicacionAMulta(multaSeleccionada, saldoDisponible)
                : (entrySeleccionado?.remainingBalance ?? undefined)
            }
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

      {/* Referencia: solo para transferencia SIN neteo (con neteo, la referencia ya se
          pide dentro del bloque del Flujo 4 de arriba, junto al desglose). */}
      {kindDestino === 2 && !enModoNeteo && !decidiendoModoDevolucion && (
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

      {/* Línea de resumen para kind 3: le muestra al usuario exactamente qué va a pasar. */}
      {kindDestino === 3 && reservaDestinoSeleccionada && monto && parseFloat(monto) > 0 && (
        <div className="rounded-lg bg-emerald-50 dark:bg-emerald-950/20 border border-emerald-200 dark:border-emerald-900/40 px-4 py-2.5">
          <p className="text-xs text-emerald-700 dark:text-emerald-400">
            Se van a aplicar <strong>{formatCurrency(parseFloat(monto), moneda)}</strong>{" "}
            del saldo a favor a la reserva <strong>{reservaDestinoSeleccionada.numeroReserva ?? "—"}</strong>.
          </p>
        </div>
      )}

      {/* Línea de resumen para kind 4: mismo patrón que kind 3 (spec §3.1). */}
      {kindDestino === KIND_APLICAR_A_MULTA && multaSeleccionada && monto && parseFloat(monto) > 0 && (
        <div className="rounded-lg bg-emerald-50 dark:bg-emerald-950/20 border border-emerald-200 dark:border-emerald-900/40 px-4 py-2.5">
          <p className="text-xs text-emerald-700 dark:text-emerald-400">
            Se van a aplicar <strong>{formatCurrency(parseFloat(monto), moneda)}</strong>{" "}
            del saldo a favor a la multa de la reserva <strong>{multaSeleccionada.numeroReserva ?? "—"}</strong>.
          </p>
        </div>
      )}

      {/* Nota preventiva para efectivo: el backend puede rechazar con 409 por Ley 25.345
          (vale tanto para el retiro simple como para el neto de la obra b). */}
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

      {/* Botones: anti-doble-click con disabled={guardando}. En modo neteo o en el
          picker de multa, además se bloquea mientras la previa está cargando/rota. */}
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
          disabled={
            guardando
            || decidiendoModoDevolucion
            || ((kindDestino === 1 || kindDestino === 2 || kindDestino === KIND_APLICAR_A_MULTA) && !!errorNettingPreview)
            || (kindDestino === KIND_APLICAR_A_MULTA && !hayMultasAplicables)
          }
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
                : kindDestino === 3 || kindDestino === KIND_APLICAR_A_MULTA
                ? "Aplicar saldo"
                : enModoNeteo
                ? Number(nettingPreview?.netToRefund ?? 0) > 0
                  ? `Devolver ${formatCurrency(nettingPreview.netToRefund, moneda)}`
                  : "Aplicar a la multa"
                : "Confirmar retiro"}
            </>
          )}
        </button>
      </div>
    </div>
  );
}

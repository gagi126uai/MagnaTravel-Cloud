/**
 * Armado de factura AFIP EN LÍNEA — reemplaza el CreateInvoiceModal (ventana modal).
 *
 * Decisión de UX (guia-ux-gaston.md, 2026-06-13): el armado va en línea, debajo del
 * botón "Emitir factura", igual que el cobro (RegistrarCobroInline) y la carga de
 * servicios (ServiceInlineCard).
 *
 * Al abrirse llama al nuevo endpoint GET /invoices/reserva/{id}/suggested-items para
 * precargar los renglones desde los servicios CONFIRMADOS. El usuario puede editar,
 * borrar o agregar renglones. La factura se emite con POST /invoices (sin cambios en
 * el endpoint de emisión).
 *
 * Regla multimoneda: el backend devuelve grupos por moneda (ARS / USD separados).
 * Si hay grupos de ambas monedas, el usuario elige cuál facturar. NUNCA se mezclan
 * renglones de monedas distintas en un mismo comprobante.
 *
 * Franja amarilla: aparece arriba de los renglones cuando el total de los items NO
 * coincide con lo vendido confirmado en esa moneda. NO bloquea la emisión.
 *
 * Funciones puras exportadas (testeable sin React):
 *   - elegirGrupoPrecarga(grupos, flagMultimonedaOn)
 *   - hayDescuadre(totalItems, suggestedTotal, tolerancia)
 *   - validarUSD(tipoCambio, justificacion) → null | string de error
 */

import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertCircle, Calculator, Plus, Trash2, X } from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatCurrency } from "../../../lib/utils";

// ─── Funciones puras exportables ─────────────────────────────────────────────
// Están fuera del componente para poder testearlas con Node sin necesidad de React.
// El componente las llama internamente; no se duplica lógica.

/**
 * Elige qué grupo de items precargar al abrir el formulario.
 *
 * Regla de seguridad fiscal (fix B1):
 *   Con flag OFF la moneda efectiva siempre es ARS. Por lo tanto SOLO se puede
 *   precargar el grupo ARS. Si la reserva solo tiene servicios en USD, devolvemos
 *   null → el componente arranca con un renglón en cero y muestra un aviso.
 *   NUNCA se carga un grupo USD cuando la moneda efectiva es ARS porque eso
 *   emitiría montos en dólares como si fueran pesos (comprobante fiscal incorrecto).
 *
 *   Con flag ON, la regla original aplica: ARS preferido o el primero disponible.
 *
 * @param {Array}   grupos           - grupos de InvoiceSuggestedItemsResponse
 * @param {boolean} flagMultimonedaOn - valor de enableMultiCurrencyInvoicing
 * @returns {{ currency: string, items: Array, suggestedTotal: number } | null}
 */
export function elegirGrupoPrecarga(grupos, flagMultimonedaOn) {
  if (!Array.isArray(grupos) || grupos.length === 0) return null;

  if (!flagMultimonedaOn) {
    // Con flag OFF: SOLO cargar ARS. Si no hay grupo ARS devolvemos null.
    // No cargar el grupo USD "por defecto" → eso generaría montos en dólares facturados como pesos.
    return grupos.find((g) => g.currency === "ARS") ?? null;
  }

  // Con flag ON: preferir ARS, pero si no existe cargar el primero disponible.
  const grupoARS = grupos.find((g) => g.currency === "ARS");
  return grupoARS ?? grupos[0];
}

/**
 * Determina si hay un descuadre entre lo que el usuario armó y lo que vendió.
 *
 * @param {number} totalItems      - total calculado de los renglones actuales
 * @param {number} suggestedTotal  - total sugerido del grupo de la moneda activa
 * @param {number} [tolerancia=0.5] - margen en la misma moneda para evitar falsas alarmas por redondeo
 * @returns {boolean}
 */
export function hayDescuadre(totalItems, suggestedTotal, tolerancia = 0.5) {
  if (typeof suggestedTotal !== "number" || suggestedTotal <= 0) return false;
  const diferencia = Math.abs(totalItems - suggestedTotal);
  return diferencia > tolerancia;
}

/**
 * Valida los campos de tipo de cambio para facturas en USD.
 *
 * @param {string|number} tipoCambio    - valor ingresado por el usuario
 * @param {string}        justificacion - texto de justificación del TC
 * @returns {string | null} mensaje de error legible, o null si todo está bien
 */
export function validarCamposUSD(tipoCambio, justificacion) {
  const tcNum = Number(tipoCambio);
  if (!tipoCambio || isNaN(tcNum) || tcNum <= 0) {
    return "Ingresá el tipo de cambio para facturas en dólares.";
  }
  if (tcNum === 1) {
    return "El tipo de cambio no puede ser 1. Ingresá el valor en pesos del dólar (ej: 1200).";
  }
  if (!String(justificacion ?? "").trim()) {
    return "Ingresá la justificación del tipo de cambio.";
  }
  return null;
}

// ─── Constantes de dominio ────────────────────────────────────────────────────

// Alícuotas de IVA según catálogo ARCA/AFIP.
// El id coincide con el AlicuotaIvaId que espera el backend.
const VAT_RATES = [
  { id: 3, label: "0%", value: 0 },
  { id: 4, label: "10.5%", value: 0.105 },
  { id: 5, label: "21%", value: 0.21 },
  { id: 6, label: "27%", value: 0.27 },
  { id: 8, label: "5%", value: 0.05 },
  { id: 9, label: "2.5%", value: 0.025 },
];

const TRIBUTE_TYPES = [
  { id: 99, label: "Otras percepciones" },
  { id: 1, label: "Impuestos nacionales" },
  { id: 2, label: "Impuestos provinciales" },
  { id: 3, label: "Impuestos municipales" },
  { id: 4, label: "Impuestos internos" },
];

// Solo ARS/USD en el MVP. Coinicde con CURRENCY_OPTIONS en CreateInvoiceModal.
const MONEDAS_FACTURA = [
  { value: "ARS", label: "Pesos (ARS)" },
  { value: "USD", label: "Dólares (USD)" },
];

// Valor del enum ExchangeRateSource=BNA_VendedorDivisa (ver ExchangeRateSource.cs).
// El backend NO tiene JsonStringEnumConverter, espera el int.
const EXCHANGE_RATE_SOURCE_BNA_VENDEDOR_DIVISA = 6;

/**
 * Crea un renglón vacío listo para agregar a la tabla de items.
 * isMonotributista=true → alícuota 0% (Factura C no discrimina IVA).
 */
function crearItemNuevo(isMonotributista) {
  return {
    description: "Servicios Turísticos",
    quantity: 1,
    unitPrice: 0,
    alicuotaIvaId: isMonotributista ? 3 : 5,
  };
}

/**
 * Convierte un InvoiceItemDto del backend al shape local del formulario.
 * Usamos esta función para que la precarga desde suggested-items sea limpia.
 */
function itemBackendALocal(itemDto) {
  return {
    description: itemDto.description,
    quantity: Number(itemDto.quantity),
    unitPrice: Number(itemDto.unitPrice),
    alicuotaIvaId: Number(itemDto.alicuotaIvaId),
  };
}

export function EmitirFacturaInline({
  reservaId,       // publicId de la reserva (string)
  reserva,         // objeto completo de la reserva (para bloques de deuda, saldo, nombre de cliente)
  clientName,      // nombre del cliente (para mostrar en el encabezado)
  clientCuit,      // CUIT del cliente (para mostrar en el encabezado)
  onFacturaEmitida, // callback sin argumentos: refresca la reserva en el padre
  onCancelar,       // callback cuando el usuario cierra sin emitir
}) {
  // ── Estado de carga de datos ───────────────────────────────────────────────
  const [cargandoSettings, setCargandoSettings] = useState(true);
  const [afipSettings, setAfipSettings] = useState(null);
  const [cargandoSugeridos, setCargandoSugeridos] = useState(true);
  const [gruposSugeridos, setGruposSugeridos] = useState([]); // InvoiceSuggestedItemGroupDto[]

  // ── Estado del formulario ──────────────────────────────────────────────────
  const [items, setItems] = useState([]);
  const [tributes, setTributes] = useState([]);
  const [monedaSeleccionada, setMonedaSeleccionada] = useState("ARS");

  // Campos de tipo de cambio: solo obligatorios cuando monedaSeleccionada === "USD"
  const [tipoCambio, setTipoCambio] = useState("");
  const [justificacionTC, setJustificacionTC] = useState("");

  // Override de emisión cuando la reserva tiene deuda pero canEmitAfipInvoice=true
  const [forceIssue, setForceIssue] = useState(false);
  const [forceReason, setForceReason] = useState("");

  // Aviso del backend al crear la factura (InvoiceDto.Warning), no bloqueante
  const [warningEmision, setWarningEmision] = useState(null);

  const [submitting, setSubmitting] = useState(false);
  const [errorEnvio, setErrorEnvio] = useState(null);

  // ── Derivados de afipSettings ──────────────────────────────────────────────
  const isMonotributista =
    afipSettings?.taxCondition?.trim() === "Monotributo" ||
    afipSettings?.taxCondition?.trim() === "Exento";

  // El flag enableMultiCurrencyInvoicing activa el selector de moneda.
  // Con flag OFF la moneda siempre es ARS.
  const isMultiCurrencyEnabled = Boolean(afipSettings?.enableMultiCurrencyInvoicing);

  // Moneda efectiva: si el flag está OFF, forzamos ARS (transparente para el usuario).
  const monedaEfectiva = isMultiCurrencyEnabled ? monedaSeleccionada : "ARS";
  const esUSD = monedaEfectiva === "USD";

  // ── Derivados de reserva ───────────────────────────────────────────────────
  const requiereOverride = Boolean(
    reserva && !reserva.isEconomicallySettled && reserva.canEmitAfipInvoice
  );
  const bloqueadoPorDeuda = Boolean(
    reserva && !reserva.isEconomicallySettled && !reserva.canEmitAfipInvoice
  );

  // Fix B1: detectar si la reserva SOLO tiene servicios en USD pero el flag está OFF.
  // En ese caso no se pueden precargar los montos (se emitirían como pesos) y hay que
  // mostrar un aviso claro en lugar de dejar los campos en cero sin explicación.
  const soloServiciosUSD =
    !isMultiCurrencyEnabled &&
    gruposSugeridos.length > 0 &&
    gruposSugeridos.every((g) => g.currency !== "ARS");

  // ── Carga de settings AFIP (una vez al montar) ────────────────────────────
  useEffect(() => {
    let cancelado = false;
    (async () => {
      setCargandoSettings(true);
      try {
        const response = await api.get("/afip/settings");
        if (!cancelado) setAfipSettings(response);
      } catch {
        if (!cancelado) showError("No se pudo obtener la configuración de AFIP.");
      } finally {
        if (!cancelado) setCargandoSettings(false);
      }
    })();
    return () => { cancelado = true; };
  }, []);

  // ── Carga de renglones sugeridos desde los servicios confirmados ───────────
  // GET /invoices/reserva/{id}/suggested-items → InvoiceSuggestedItemsResponse
  // Los grupos se guardan en estado para poder mostrar el selector de moneda
  // cuando hay más de un grupo (ARS y USD simultáneos).
  useEffect(() => {
    if (!reservaId) return;
    let cancelado = false;
    (async () => {
      setCargandoSugeridos(true);
      try {
        const response = await api.get(`/invoices/reserva/${reservaId}/suggested-items`);
        if (!cancelado) {
          const grupos = response?.groups ?? [];
          setGruposSugeridos(grupos);

          // Fix B1: elegirGrupoPrecarga aplica la regla de seguridad fiscal:
          // con flag OFF nunca se precarga un grupo USD (eso facturaría dólares como pesos).
          const grupoPrecarga = elegirGrupoPrecarga(grupos, isMultiCurrencyEnabled);

          if (grupoPrecarga) {
            setMonedaSeleccionada(grupoPrecarga.currency);
            setItems(grupoPrecarga.items.map(itemBackendALocal));
          } else {
            // Sin servicios en la moneda facturable (o sin servicios confirmados):
            // arrancamos con un renglón genérico en cero.
            // Si el motivo es "solo hay USD con flag OFF", el componente mostrará
            // el aviso correspondiente usando la variable soloServiciosUSD (ver JSX).
            setItems([crearItemNuevo(isMonotributista)]);
          }
        }
      } catch {
        // No bloquear si falla: el usuario puede cargar los renglones a mano
        if (!cancelado) setItems([crearItemNuevo(isMonotributista)]);
      } finally {
        if (!cancelado) setCargandoSugeridos(false);
      }
    })();
    return () => { cancelado = true; };
  // Dependencia en isMonotributista para que al conocer el régimen el item genérico tenga la alícuota correcta.
  // isMultiCurrencyEnabled viene de afipSettings que se carga antes (cargandoInicial cubre ambos).
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reservaId]);

  // ── Cuando el usuario cambia la moneda seleccionada: recargar items del grupo ──
  // Esto respeta la regla multimoneda: NUNCA mezclar ARS y USD en un comprobante.
  const handleCambiarMoneda = useCallback((nuevaMoneda) => {
    setMonedaSeleccionada(nuevaMoneda);
    setTipoCambio("");
    setJustificacionTC("");

    // Fix N1: al cambiar moneda limpiar errores y warnings previos.
    // Si el usuario tuvo un error en ARS y cambia a USD, el error viejo no aplica.
    setErrorEnvio(null);
    setWarningEmision(null);

    const grupoCorrespondiente = gruposSugeridos.find((g) => g.currency === nuevaMoneda);
    if (grupoCorrespondiente) {
      setItems(grupoCorrespondiente.items.map(itemBackendALocal));
    } else {
      // No hay servicios en esa moneda: item genérico vacío
      setItems([crearItemNuevo(isMonotributista)]);
    }
  }, [gruposSugeridos, isMonotributista]);

  // ── Total calculado en el frontend (neto, IVA, tributos, total) ───────────
  const totales = useMemo(() => {
    const neto = items.reduce(
      (acum, item) => acum + (Number(item.quantity) || 0) * (Number(item.unitPrice) || 0),
      0
    );

    const iva = items.reduce((acum, item) => {
      // Monotributo (Factura C): no discrimina IVA, el total = neto
      if (isMonotributista) return acum;
      const itemNeto = (Number(item.quantity) || 0) * (Number(item.unitPrice) || 0);
      const tasa = VAT_RATES.find((r) => r.id === Number(item.alicuotaIvaId))?.value || 0;
      return acum + itemNeto * tasa;
    }, 0);

    const montTributos = tributes.reduce((acum, t) => acum + (Number(t.importe) || 0), 0);
    const total = neto + iva + montTributos;

    return { neto, iva, montTributos, total };
  }, [isMonotributista, items, tributes]);

  // ── Franja amarilla de descuadre ─────────────────────────────────────────
  // Usamos el suggestedTotal del grupo de la moneda activa como referencia.
  // Si el total de los items editados difiere del sugerido → aviso (no bloqueo).
  const descuadre = useMemo(() => {
    const grupo = gruposSugeridos.find((g) => g.currency === monedaEfectiva);
    if (!grupo || grupo.suggestedTotal <= 0) return null;

    const totalSugerido = Number(grupo.suggestedTotal);
    const diferencia = Math.abs(totales.total - totalSugerido);

    // Tolerancia de $0.50 para evitar falsas alarmas por redondeos de dos decimales
    if (diferencia <= 0.5) return null;

    return {
      totalSugerido,
      diferencia,
      facturasMas: totales.total > totalSugerido,
    };
  }, [gruposSugeridos, monedaEfectiva, totales.total]);

  // ── Manejadores de tabla de items ─────────────────────────────────────────
  const handleItemChange = (index, campo, valor) => {
    setItems((prev) =>
      prev.map((item, i) => (i === index ? { ...item, [campo]: valor } : item))
    );
  };

  const handleAgregarItem = () => {
    setItems((prev) => [...prev, crearItemNuevo(isMonotributista)]);
  };

  const handleEliminarItem = (index) => {
    setItems((prev) => prev.filter((_, i) => i !== index));
  };

  // ── Manejadores de tabla de tributos ──────────────────────────────────────
  const handleTributeChange = (index, campo, valor) => {
    setTributes((prev) =>
      prev.map((t, i) => (i === index ? { ...t, [campo]: valor } : t))
    );
  };

  const handleAgregarTribute = () => {
    setTributes((prev) => [
      ...prev,
      { tributeId: 99, description: "", baseImponible: 0, alicuota: 0, importe: 0 },
    ]);
  };

  const handleEliminarTribute = (index) => {
    setTributes((prev) => prev.filter((_, i) => i !== index));
  };

  // ── Confirmación de warning post-emisión (fix N2) ──────────────────────────
  // El usuario vio el aviso del backend y hace clic en "Entendido".
  // Recién ahí cerramos la sección y refrescamos la reserva en el padre.
  const handleConfirmarWarning = () => {
    showSuccess("Comprobante AFIP encolado correctamente.");
    onFacturaEmitida();
  };

  // ── Submit ─────────────────────────────────────────────────────────────────
  const handleSubmit = async () => {
    setWarningEmision(null);
    setErrorEnvio(null);

    if (items.length === 0) {
      setErrorEnvio("Agregá al menos un renglón.");
      return;
    }
    if (totales.total <= 0) {
      setErrorEnvio("El total debe ser mayor a $0.");
      return;
    }
    if (bloqueadoPorDeuda) {
      setErrorEnvio(reserva?.economicBlockReason || "No se puede facturar: la reserva tiene saldo pendiente.");
      return;
    }
    if (requiereOverride && !forceIssue) {
      setErrorEnvio("Confirmá la emisión por excepción.");
      return;
    }
    if (requiereOverride && forceReason.trim().length < 10) {
      setErrorEnvio("Ingresá un motivo de al menos 10 caracteres.");
      return;
    }
    if (esUSD) {
      // validarCamposUSD es una función pura exportada y testeada.
      const errorUSD = validarCamposUSD(tipoCambio, justificacionTC);
      if (errorUSD) {
        setErrorEnvio(errorUSD);
        return;
      }
    }

    setSubmitting(true);
    try {
      const payload = {
        reservaId: reservaId,
        items: items.map((item) => ({
          description: item.description,
          quantity: Number(item.quantity),
          unitPrice: Number(item.unitPrice),
          total: Number(item.quantity) * Number(item.unitPrice),
          alicuotaIvaId: Number(item.alicuotaIvaId),
        })),
        tributes: tributes.map((t) => ({
          tributeId: Number(t.tributeId),
          description: t.description,
          baseImponible: Number(t.baseImponible),
          alicuota: Number(t.alicuota),
          importe: Number(t.importe),
        })),
        forceIssue: requiereOverride ? forceIssue : false,
        forceReason: requiereOverride ? forceReason.trim() : null,
      };

      // Campos multimoneda: solo se agregan al payload cuando la moneda es USD.
      // Con ARS el payload es idéntico al original (backend usa defaults "PES"/1).
      if (esUSD) {
        payload.monId = "USD";
        payload.monCotiz = Number(tipoCambio);
        // ExchangeRateSource es un enum sin JsonStringEnumConverter → enviar el int
        payload.exchangeRateSource = EXCHANGE_RATE_SOURCE_BNA_VENDEDOR_DIVISA;
        payload.exchangeRateFetchedAt = new Date().toISOString();
        payload.exchangeRateJustification = justificacionTC.trim();
      }

      const response = await api.post("/invoices", payload);

      // Fix N2: si la respuesta trae warning (InvoiceDto.Warning), NO cerrar automáticamente.
      // La factura ya fue encolada en AFIP, pero el backend advierte algo (ej: descuadre con la reserva).
      // Mostramos el warning y el usuario tiene que confirmar con "Entendido" para cerrar.
      // Si NO hay warning, cerramos igual que antes (toast + callback).
      if (response?.warning) {
        setWarningEmision(response.warning);
        // No llamamos onFacturaEmitida() ni showSuccess() aquí.
        // El botón "Entendido" en el JSX es el que llama a handleConfirmarWarning().
        return;
      }

      showSuccess("Comprobante AFIP encolado correctamente.");
      onFacturaEmitida();
    } catch (error) {
      // 409 cuando ya hay una Invoice PENDING para esta reserva.
      // El backend devuelve { message } con texto accionable.
      const mensaje = error?.payload?.message ?? getApiErrorMessage(error) ?? "Error al emitir la factura.";
      setErrorEnvio(mensaje);
    } finally {
      setSubmitting(false);
    }
  };

  // ── Cargando ───────────────────────────────────────────────────────────────
  const cargandoInicial = cargandoSettings || cargandoSugeridos;

  return (
    <div
      data-testid="emitir-factura-inline"
      className="rounded-xl border border-indigo-200 bg-indigo-50 dark:border-indigo-900/40 dark:bg-indigo-950/20 shadow-sm"
    >
      {/* Encabezado de la ficha ─────────────────────────────────────────── */}
      <div className="flex items-center justify-between border-b border-indigo-200 dark:border-indigo-900/40 px-6 py-4">
        <div className="flex items-center gap-3">
          <div className="rounded-lg bg-indigo-100 dark:bg-indigo-900/40 p-2">
            <Calculator className="w-5 h-5 text-indigo-600 dark:text-indigo-400" />
          </div>
          <div>
            <div className="text-sm font-bold text-slate-900 dark:text-white">Nueva Factura AFIP</div>
            <div className="text-xs text-slate-500 dark:text-slate-400">
              {clientName || "Consumidor Final"}
              {clientCuit ? ` · CUIT ${clientCuit}` : ""}
            </div>
          </div>
        </div>
        <button
          type="button"
          onClick={onCancelar}
          className="rounded-lg p-1.5 text-slate-400 hover:bg-indigo-100 hover:text-slate-600 dark:hover:bg-indigo-900/40 dark:hover:text-slate-300 transition-colors"
          title="Cerrar"
          data-testid="btn-cerrar-factura-inline"
        >
          <X className="w-5 h-5" />
        </button>
      </div>

      <div className="px-6 py-5 space-y-6">
        {/* Estado de carga ─────────────────────────────────────────────── */}
        {cargandoInicial && (
          <div className="text-center py-6 text-sm text-slate-500 dark:text-slate-400">
            Cargando renglones desde los servicios confirmados...
          </div>
        )}

        {!cargandoInicial && (
          <>
            {/* Bloque de deuda / override ──────────────────────────────── */}
            {(requiereOverride || bloqueadoPorDeuda) && (
              <div
                className={`rounded-xl border px-4 py-4 ${
                  bloqueadoPorDeuda
                    ? "border-rose-200 bg-rose-50 dark:border-rose-900/40 dark:bg-rose-900/20"
                    : "border-amber-200 bg-amber-50 dark:border-amber-900/40 dark:bg-amber-900/20"
                }`}
                data-testid="bloque-deuda-factura"
              >
                <div
                  className={`flex items-start gap-3 ${
                    bloqueadoPorDeuda
                      ? "text-rose-700 dark:text-rose-300"
                      : "text-amber-700 dark:text-amber-300"
                  }`}
                >
                  <AlertCircle className="w-5 h-5 mt-0.5 flex-shrink-0" />
                  <div className="space-y-1">
                    <div className="font-semibold">
                      {bloqueadoPorDeuda ? "AFIP bloqueado por deuda" : "Emisión por excepción habilitada"}
                    </div>
                    <p className="text-sm">
                      {reserva?.economicBlockReason || "La reserva todavía no está cancelada económicamente."}
                    </p>
                    {typeof reserva?.balance === "number" && (
                      <p className="text-sm font-medium">
                        Saldo pendiente:{" "}
                        {Number(reserva.balance).toLocaleString("es-AR", {
                          style: "currency",
                          currency: "ARS",
                          minimumFractionDigits: 2,
                        })}
                      </p>
                    )}
                  </div>
                </div>

                {requiereOverride && (
                  <div className="mt-4 space-y-3">
                    <label className="flex items-start gap-3 text-sm font-medium text-slate-800 dark:text-slate-100">
                      <input
                        type="checkbox"
                        checked={forceIssue}
                        onChange={(e) => setForceIssue(e.target.checked)}
                        className="mt-1 rounded border-slate-300"
                        data-testid="check-force-issue"
                      />
                      Confirmo que se emite AFIP con deuda pendiente.
                    </label>
                    <div>
                      <label
                        htmlFor="force-reason-inline"
                        className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5"
                      >
                        Motivo del override
                      </label>
                      <textarea
                        id="force-reason-inline"
                        value={forceReason}
                        onChange={(e) => setForceReason(e.target.value)}
                        rows={2}
                        placeholder="Ej: emisión anticipada autorizada por el agente por pedido del cliente."
                        className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white px-3 py-2 text-sm"
                      />
                    </div>
                  </div>
                )}
              </div>
            )}

            {/* Aviso de servicios solo en USD con flag OFF (fix B1) ─────── */}
            {/* Regla: con multimoneda OFF, la moneda efectiva es ARS. Si la reserva
                SOLO tiene servicios en USD no podemos precargar los montos (facturarían
                dólares como pesos). Mostramos un aviso claro para que el operador sepa
                qué hacer. La tabla de renglones queda disponible para carga manual. */}
            {soloServiciosUSD && (
              <div
                data-testid="aviso-solo-usd"
                className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-300"
                role="alert"
              >
                <div className="flex items-start gap-2">
                  <AlertCircle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                  <div>
                    <span className="font-semibold">Esta reserva tiene servicios en dólares.</span>
                    {" "}Para facturarla en dólares hay que activar multimoneda en la configuración de AFIP.
                    Podés ingresar los renglones manualmente en pesos si corresponde.
                  </div>
                </div>
              </div>
            )}

            {/* Franja amarilla de descuadre (no bloquea) ─────────────── */}
            {descuadre && (
              <div
                data-testid="franja-descuadre"
                className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-200"
                role="status"
                aria-live="polite"
              >
                <div className="flex items-start gap-2">
                  <AlertCircle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                  <div>
                    <span className="font-semibold">Total no coincide con lo vendido. </span>
                    {descuadre.facturasMas
                      ? `Estás facturando ${formatCurrency(descuadre.diferencia, monedaEfectiva)} más de lo vendido.`
                      : `Estás facturando ${formatCurrency(descuadre.diferencia, monedaEfectiva)} menos de lo vendido.`}
                    {" "}Podés continuar, pero revisá que sea correcto.
                  </div>
                </div>
                <div className="mt-1 text-xs text-amber-700 dark:text-amber-300">
                  Vendido confirmado ({monedaEfectiva}): <strong>{formatCurrency(descuadre.totalSugerido, monedaEfectiva)}</strong>
                  {" · "}Este comprobante: <strong>{formatCurrency(totales.total, monedaEfectiva)}</strong>
                </div>
              </div>
            )}

            {/* Selector de moneda (solo si enableMultiCurrencyInvoicing=ON) ── */}
            {isMultiCurrencyEnabled && (
              <div
                data-testid="selector-moneda-inline"
                className="rounded-xl border border-indigo-200 dark:border-indigo-800 bg-white dark:bg-slate-900/40 px-4 py-4 space-y-4"
              >
                <span className="text-xs font-semibold text-indigo-600 dark:text-indigo-400 uppercase tracking-wider">
                  Moneda de la factura
                </span>
                <div className="flex items-center gap-4">
                  {MONEDAS_FACTURA.map((opcion) => (
                    <label
                      key={opcion.value}
                      className="flex items-center gap-2 cursor-pointer text-sm font-medium text-slate-700 dark:text-slate-300"
                    >
                      <input
                        type="radio"
                        name="moneda-factura-inline"
                        value={opcion.value}
                        checked={monedaSeleccionada === opcion.value}
                        onChange={() => handleCambiarMoneda(opcion.value)}
                        data-testid={`radio-moneda-${opcion.value}`}
                      />
                      {opcion.label}
                    </label>
                  ))}
                </div>

                {/* Campos de tipo de cambio: solo cuando moneda = USD */}
                {esUSD && (
                  <div className="space-y-3 pt-2 border-t border-indigo-200 dark:border-indigo-700">
                    <div className="flex items-center gap-2 text-xs text-indigo-700 dark:text-indigo-300 bg-indigo-100 dark:bg-indigo-900/40 px-3 py-2 rounded-lg">
                      <AlertCircle className="w-3.5 h-3.5 flex-shrink-0" />
                      Fuente del TC: BNA vendedor divisa (dólar del día hábil anterior)
                    </div>

                    <div className="flex flex-col sm:flex-row gap-3">
                      <div className="w-full sm:w-40">
                        <label
                          htmlFor="tc-inline-input"
                          className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5"
                        >
                          Tipo de cambio ($/USD) *
                        </label>
                        <input
                          id="tc-inline-input"
                          type="number"
                          step="0.01"
                          min="1.01"
                          value={tipoCambio}
                          onChange={(e) => setTipoCambio(e.target.value)}
                          placeholder="Ej: 1200.50"
                          className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                          data-testid="input-tipo-cambio-inline"
                        />
                      </div>
                      <div className="flex-1">
                        <label
                          htmlFor="tc-justificacion-inline"
                          className="block text-xs font-semibold uppercase tracking-wider text-slate-500 mb-1.5"
                        >
                          Justificación del TC *
                        </label>
                        <textarea
                          id="tc-justificacion-inline"
                          value={justificacionTC}
                          onChange={(e) => setJustificacionTC(e.target.value)}
                          rows={2}
                          placeholder="Ej: Dólar vendedor divisa BNA del 13/06/2026, $1200.50 según web oficial."
                          className="w-full rounded-lg border border-slate-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white px-3 py-2 text-sm"
                          data-testid="input-justificacion-tc-inline"
                        />
                      </div>
                    </div>
                  </div>
                )}
              </div>
            )}

            {/* Aviso de Factura C (Monotributo) ─────────────────────── */}
            {isMonotributista && (
              <div className="flex items-center gap-2 text-xs font-semibold text-amber-600 dark:text-amber-400 bg-amber-50 dark:bg-amber-900/20 px-3 py-2 rounded-lg border border-amber-100 dark:border-amber-900/30">
                <AlertCircle className="w-3.5 h-3.5" />
                Factura C (Monotributo): no discrimina IVA — el precio ingresado es el total.
              </div>
            )}

            {/* Tabla de renglones ──────────────────────────────────── */}
            <div>
              <div className="flex items-center justify-between mb-3">
                <h4 className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                  Renglones
                </h4>
                {gruposSugeridos.length > 0 && (
                  <span className="text-xs text-indigo-600 dark:text-indigo-400 font-medium">
                    Precargados desde servicios confirmados — podés editar
                  </span>
                )}
              </div>

              <div className="space-y-2">
                {items.map((item, index) => (
                  <div
                    key={index}
                    className="flex flex-col md:flex-row gap-3 items-end bg-white dark:bg-slate-900/40 p-3 rounded-lg border border-slate-200 dark:border-slate-700"
                    data-testid={`item-renglon-${index}`}
                  >
                    {/* Descripción */}
                    <div className="flex-1">
                      <label className="block text-xs font-medium text-slate-500 mb-1">
                        Descripción
                      </label>
                      <input
                        type="text"
                        value={item.description}
                        onChange={(e) => handleItemChange(index, "description", e.target.value)}
                        className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                        placeholder="Ej. Servicios turísticos"
                        data-testid={`item-descripcion-${index}`}
                      />
                    </div>

                    {/* Cantidad */}
                    <div className="w-24">
                      <label className="block text-xs font-medium text-slate-500 mb-1">Cant.</label>
                      <input
                        type="number"
                        step="0.01"
                        value={item.quantity}
                        onChange={(e) => handleItemChange(index, "quantity", e.target.value)}
                        className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                        data-testid={`item-cantidad-${index}`}
                      />
                    </div>

                    {/* Precio unitario */}
                    <div className="w-32">
                      <label className="block text-xs font-medium text-slate-500 mb-1">
                        Precio {esUSD ? "(USD)" : ""}
                      </label>
                      <div className="relative">
                        <span className="absolute left-2 top-1.5 text-slate-400 text-xs">
                          {esUSD ? "US$" : "$"}
                        </span>
                        <input
                          type="number"
                          step="0.01"
                          value={item.unitPrice}
                          onChange={(e) => handleItemChange(index, "unitPrice", e.target.value)}
                          className="w-full text-sm pl-8 rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                          data-testid={`item-precio-${index}`}
                        />
                      </div>
                    </div>

                    {/* Alícuota IVA: solo cuando NO es Monotributo */}
                    {!isMonotributista && (
                      <div className="w-32">
                        <label className="block text-xs font-medium text-slate-500 mb-1">IVA</label>
                        <select
                          value={item.alicuotaIvaId}
                          onChange={(e) => handleItemChange(index, "alicuotaIvaId", e.target.value)}
                          className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                          data-testid={`item-iva-${index}`}
                        >
                          {VAT_RATES.map((rate) => (
                            <option key={rate.id} value={rate.id}>
                              {rate.label}
                            </option>
                          ))}
                        </select>
                      </div>
                    )}

                    {/* Subtotal del renglón */}
                    <div className="w-32 text-right pb-2 font-medium text-slate-900 dark:text-white text-sm">
                      {formatCurrency(
                        (Number(item.quantity) || 0) * (Number(item.unitPrice) || 0),
                        monedaEfectiva
                      )}
                    </div>

                    {/* Eliminar renglón */}
                    <button
                      type="button"
                      onClick={() => handleEliminarItem(index)}
                      className="p-2 text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-900/20 rounded-md transition-colors"
                      title="Eliminar renglón"
                      data-testid={`btn-eliminar-item-${index}`}
                    >
                      <Trash2 className="w-4 h-4" />
                    </button>
                  </div>
                ))}

                <button
                  type="button"
                  onClick={handleAgregarItem}
                  className="flex items-center gap-2 text-sm text-indigo-600 dark:text-indigo-400 hover:text-indigo-700 font-medium mt-1"
                  data-testid="btn-agregar-item"
                >
                  <Plus className="w-4 h-4" />
                  Agregar renglón
                </button>
              </div>
            </div>

            {/* Tributos / Percepciones ─────────────────────────────── */}
            <div className="pt-2 border-t border-slate-200 dark:border-slate-700">
              <div className="flex items-center justify-between mb-3">
                <h4 className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                  Tributos / Percepciones
                </h4>
              </div>

              <div className="space-y-2">
                {tributes.map((tribute, index) => (
                  <div
                    key={index}
                    className="flex flex-col md:flex-row gap-3 items-end bg-orange-50 dark:bg-orange-900/10 p-3 rounded-lg border border-orange-100 dark:border-orange-900/30"
                    data-testid={`tributo-${index}`}
                  >
                    <div className="w-48">
                      <label className="block text-xs font-medium text-slate-500 mb-1">Tipo</label>
                      <select
                        value={tribute.tributeId}
                        onChange={(e) => handleTributeChange(index, "tributeId", e.target.value)}
                        className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                      >
                        {TRIBUTE_TYPES.map((tipo) => (
                          <option key={tipo.id} value={tipo.id}>
                            {tipo.label}
                          </option>
                        ))}
                      </select>
                    </div>
                    <div className="flex-1">
                      <label className="block text-xs font-medium text-slate-500 mb-1">
                        Descripción
                      </label>
                      <input
                        type="text"
                        value={tribute.description}
                        onChange={(e) => handleTributeChange(index, "description", e.target.value)}
                        className="w-full text-sm rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white"
                        placeholder="Detalle del tributo"
                      />
                    </div>
                    <div className="w-28">
                      <label className="block text-xs font-medium text-slate-500 mb-1">Importe</label>
                      <div className="relative">
                        <span className="absolute left-2 top-1.5 text-slate-400 text-xs">
                          {esUSD ? "US$" : "$"}
                        </span>
                        <input
                          type="number"
                          step="0.01"
                          value={tribute.importe}
                          onChange={(e) => handleTributeChange(index, "importe", e.target.value)}
                          className="w-full text-sm pl-6 rounded-md border-gray-300 dark:border-slate-600 dark:bg-slate-700 dark:text-white text-right"
                        />
                      </div>
                    </div>
                    <button
                      type="button"
                      onClick={() => handleEliminarTribute(index)}
                      className="p-2 text-rose-500 hover:bg-rose-50 dark:hover:bg-rose-900/20 rounded-md transition-colors"
                      title="Eliminar tributo"
                    >
                      <Trash2 className="w-4 h-4" />
                    </button>
                  </div>
                ))}

                <button
                  type="button"
                  onClick={handleAgregarTribute}
                  className="flex items-center gap-2 text-sm text-orange-600 dark:text-orange-400 hover:text-orange-700 font-medium mt-1"
                  data-testid="btn-agregar-tributo"
                >
                  <Plus className="w-4 h-4" />
                  Agregar tributo
                </button>
              </div>
            </div>

            {/* Fix N2: Warning post-emisión — la factura YA fue encolada en AFIP.
                El backend advierte algo (ej: descuadre con la reserva). NO cerramos
                automáticamente para que el usuario lea el aviso. El botón "Entendido"
                llama a handleConfirmarWarning que cierra y refresca la reserva. */}
            {warningEmision && (
              <div
                data-testid="warning-emision"
                className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-4 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-200"
                role="alert"
              >
                <div className="flex items-start gap-2">
                  <AlertCircle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                  <div className="flex-1">
                    <p className="font-semibold mb-1">Comprobante encolado con advertencia</p>
                    <p>{warningEmision}</p>
                  </div>
                </div>
                <div className="mt-3 flex justify-end">
                  <button
                    type="button"
                    onClick={handleConfirmarWarning}
                    data-testid="btn-entendido-warning"
                    className="px-4 py-2 text-sm font-semibold text-white bg-amber-600 rounded-lg hover:bg-amber-700 transition-colors"
                  >
                    Entendido
                  </button>
                </div>
              </div>
            )}

            {/* Error de envío */}
            {errorEnvio && (
              <div
                data-testid="error-envio-factura"
                className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-300"
                role="alert"
              >
                <div className="flex items-start gap-2">
                  <AlertCircle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                  <span>{errorEnvio}</span>
                </div>
              </div>
            )}
          </>
        )}
      </div>

      {/* Pie: totales + botones ─────────────────────────────────────────── */}
      {!cargandoInicial && (
        <div className="border-t border-indigo-200 dark:border-indigo-900/40 bg-white dark:bg-slate-900/20 px-6 py-4">
          <div className="flex flex-col md:flex-row items-start md:items-center justify-between gap-4">

            {/* Totales */}
            <div className="space-y-1 text-sm">
              <div className="text-slate-500 dark:text-slate-400">
                Neto:{" "}
                <span className="font-medium text-slate-900 dark:text-white">
                  {formatCurrency(totales.neto, monedaEfectiva)}
                </span>
              </div>
              {!isMonotributista && (
                <div className="text-slate-500 dark:text-slate-400">
                  IVA:{" "}
                  <span className="font-medium text-slate-900 dark:text-white">
                    {formatCurrency(totales.iva, monedaEfectiva)}
                  </span>
                </div>
              )}
              {totales.montTributos > 0 && (
                <div className="text-orange-600">
                  Tributos:{" "}
                  <span className="font-medium">
                    {formatCurrency(totales.montTributos, monedaEfectiva)}
                  </span>
                </div>
              )}
              <div className="font-black text-lg text-slate-900 dark:text-white">
                Total: {formatCurrency(totales.total, monedaEfectiva)}
              </div>

              {/* Equivalente en pesos para facturas USD */}
              {esUSD && Number(tipoCambio) > 1 && (
                <div
                  data-testid="equivalente-pesos-inline"
                  className="text-xs text-indigo-600 dark:text-indigo-400 font-medium"
                >
                  ≈ {formatCurrency(totales.total * Number(tipoCambio), "ARS")}{" "}
                  (TC ${Number(tipoCambio).toLocaleString("es-AR", {
                    minimumFractionDigits: 2,
                    maximumFractionDigits: 2,
                  })})
                </div>
              )}
            </div>

            {/* Botones */}
            <div className="flex items-center gap-3">
              <button
                type="button"
                onClick={onCancelar}
                className="px-4 py-2 text-sm font-medium text-slate-700 bg-white border border-slate-300 rounded-lg hover:bg-slate-50 dark:bg-slate-800 dark:text-slate-300 dark:border-slate-600 dark:hover:bg-slate-700 transition-colors"
              >
                Cancelar
              </button>
              <button
                type="button"
                onClick={handleSubmit}
                disabled={
                  submitting ||
                  cargandoInicial ||
                  totales.total <= 0 ||
                  bloqueadoPorDeuda ||
                  (requiereOverride && !forceIssue) ||
                  // USD: bloquear hasta que el operador ingrese TC > 1 y justificación
                  (esUSD &&
                    (!(Number(tipoCambio) > 0) ||
                      Number(tipoCambio) === 1 ||
                      !justificacionTC.trim()))
                }
                data-testid="btn-emitir-factura-inline"
                className="px-4 py-2 text-sm font-bold text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 focus:ring-4 focus:ring-indigo-300 dark:focus:ring-indigo-900 disabled:opacity-50 flex items-center gap-2 transition-colors"
              >
                {submitting
                  ? "Emitiendo..."
                  : requiereOverride
                  ? "Emitir por excepción"
                  : esUSD
                  ? "Emitir factura en USD"
                  : "Emitir factura"}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

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
 * H2 (2026-06-24): flujo asíncrono con 3 estados visuales:
 *   PROCESANDO: spinner + texto en criollo (sin jerga).
 *   ÉXITO: cartel verde con tipo + número de factura. Sin recargar la página.
 *   RECHAZO: cartel rojo con motivo de AFIP + botón "Corregir y reintentar"
 *            que vuelve a la ficha con todos los datos intactos.
 * Paso 2: modal de confirmación antes de enviar (último freno antes de algo irreversible).
 * Texto fiscal exacto (verificado contra ARCA): "Una vez emitida no se puede eliminar;
 * solo se corrige o anula con una Nota de Crédito."
 *
 * Funciones puras exportadas (testeable sin React):
 *   - elegirGrupoPrecarga(grupos, flagMultimonedaOn)
 *   - hayDescuadre(totalItems, suggestedTotal, tolerancia)
 *   - validarCamposUSD(tipoCambio, justificacion) → null | string de error
 *   - resolverEstadoFiscal(statusItems) → { estado, factura }
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { AlertCircle, Calculator, CheckCircle2, Loader2, Plus, Trash2, X } from "lucide-react";
import { api } from "../../../api";
import { showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { formatCurrency } from "../../../lib/utils";
// Paso 3 (H2 2026-06-24): helper compartido con ReservaDetailPage para formatear
// el número de comprobante. Evita que el formato "Factura B 0001-00012345" diverga.
import { formatearEtiquetaFactura } from "../lib/invoiceFormatUtils";

// ─── Funciones puras exportables ─────────────────────────────────────────────
// Están fuera del componente para poder testearlas con Node sin necesidad de React.
// El componente las llama internamente; no se duplica lógica.

/**
 * F4-8 (2026-06-26): interpreta el resultado del preflight de emisión de factura.
 *
 * Regla de oro del backend: solo hay UN bloqueo duro — cuando la factura sería
 * Factura A pero el cliente NO tiene CUIT válido (lo rechazaría ARCA con certeza).
 * Todo lo demás viene con Allowed=true aunque la condición sea dudosa.
 *
 * El front NO implementa la regla fiscal: eso lo decide el backend. Acá solo
 * leemos el veredicto y decidimos si bloquear o dejar pasar.
 *
 * @param {object|null} preflight - InvoiceEmissionPreflightDto del backend
 * @returns {{ message: string } | null} - null si no hay bloqueo; objeto con mensaje si bloquea
 */
export function resolverPreflightBloqueo(preflight) {
  if (!preflight) return null;

  // El único bloqueo duro es allowed=false (o severity==="block").
  // El texto del mensaje viene del backend en criollo; si no viene, usamos el default de la spec.
  if (preflight.allowed === false || preflight.severity === "block") {
    const mensajeDefault =
      "Este cliente no es Responsable Inscripto. No corresponde Factura A. " +
      "Revisá el tipo de comprobante o la condición del cliente.";
    return { message: preflight.reason || mensajeDefault };
  }

  // allowed=true o severity="ok"/"warn": sin bloqueo.
  return null;
}

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

/**
 * H2 (2026-06-24): interpreta la respuesta del endpoint de estado fiscal.
 * GET /api/invoices/reserva/{id}/fiscal-status → InvoiceFiscalStatusDto[]
 *
 * Devuelve el estado consolidado de la factura recién emitida:
 *   "InProcess"  → esperando respuesta de AFIP (seguir polling).
 *   "Issued"     → emitida: tipo + punto de venta + número + CAE + vencimiento.
 *   "Rejected"   → rechazada por AFIP: motivo en rejectionReason.
 *   null         → no se encontró la factura en la lista (inesperado, seguir).
 *
 * Recibe el publicId de la factura recién encolada para filtrar correctamente
 * cuando la reserva ya tiene facturas anteriores.
 *
 * @param {Array}  statusItems   - lista de InvoiceFiscalStatusDto del backend
 * @param {string} invoicePublicId - publicId de la factura que acabamos de encolar
 * @returns {{ estado: string|null, factura: object|null }}
 */
export function resolverEstadoFiscal(statusItems, invoicePublicId) {
  if (!Array.isArray(statusItems) || statusItems.length === 0) {
    return { estado: null, factura: null };
  }

  // Buscamos la factura recién emitida por publicId.
  // Si por alguna razón no llegó el publicId (no debería pasar), tomamos la más reciente.
  const factura = invoicePublicId
    ? statusItems.find((f) => String(f.publicId) === String(invoicePublicId))
    : statusItems[statusItems.length - 1];

  if (!factura) return { estado: null, factura: null };

  return { estado: factura.status, factura };
}

/**
 * Construye el label de la factura emitida para mostrar en el cartel de ÉXITO (Paso 4a).
 * Ej: "Factura B 0001-00012345" — tipo + número formateado.
 *
 * Delega a formatearEtiquetaFactura (lib compartida) para usar el mismo formato
 * que InvoicePdfActions en el Estado de Cuenta (Paso 5). Sin divergencia de formato.
 *
 * @param {object|null} factura - InvoiceFiscalStatusDto cuando status === "Issued"
 * @returns {string}
 */
export function labelFacturaEmitida(factura) {
  if (!factura) return "Factura emitida";
  return formatearEtiquetaFactura(
    factura.invoiceType,
    factura.puntoDeVenta,
    factura.numeroComprobante
  );
}

// Tanda 6 (C6, 2026-07-05): traduce el motivo técnico de exclusión (ExcludedSuggestedServiceDto.Reason)
// a una frase en criollo para el aviso del formulario. Los tokens son internos del backend
// (nunca deben llegar crudos a la pantalla — gate de exposición de datos internos).
const MOTIVOS_EXCLUSION_SUGERENCIA = {
  NoResuelto: "todavía no está confirmado",
  Cancelado: "está cancelado",
  PrecioCero: "tiene precio $0",
};

/**
 * Describe en criollo por qué un servicio no entró en la sugerencia de factura.
 * Si el motivo no se reconoce (token nuevo del backend que el front no mapeó todavía),
 * cae a un texto genérico en vez de mostrar el token crudo.
 *
 * @param {string} reason - "NoResuelto" | "Cancelado" | "PrecioCero" (u otro, defensivo)
 * @returns {string}
 */
export function describirMotivoExclusion(reason) {
  return MOTIVOS_EXCLUSION_SUGERENCIA[reason] || "no se pudo incluir en la factura";
}

// ─── Constantes de polling ────────────────────────────────────────────────────

// Intervalo en ms entre cada consulta al endpoint de estado fiscal.
const POLL_INTERVAL_MS = 3000;
// Máximo de intentos antes de dar por finalizado el polling sin resultado.
// 20 intentos × 3s = 60s de espera máxima antes de mostrar el mensaje de "sigue en proceso".
const POLL_MAX_INTENTOS = 20;

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
  // Tanda 6 (C6, 2026-07-05): servicios que quedaron AFUERA de la sugerencia (o entraron en
  // $0), con el motivo. Antes esto pasaba en silencio: el vendedor veía un renglón en $0 o
  // menos renglones de los que esperaba, sin ninguna pista de por qué.
  const [excludedServices, setExcludedServices] = useState([]); // ExcludedSuggestedServiceDto[]

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

  // ── H2 (2026-06-24) + F4-8 (2026-06-26): estados del flujo asíncrono de emisión ──
  // Estados:
  //   "idle"                → formulario normal (estado inicial).
  //   "preflight-cargando"  → verificando si la factura puede emitirse (F4-8).
  //                           El formulario sigue visible, el botón muestra spinner.
  //   "preflight-bloqueado" → el preflight devolvió un bloqueo (ej: Factura A sin CUIT).
  //                           Se muestra el aviso y solo el botón "Volver".
  //   "confirmando"         → modal de confirmación abierto (paso 2 de la spec).
  //   "procesando"          → POST enviado, polling activo (paso 3).
  //   "exito"               → AFIP aprobó, mostramos el número y CAE (paso 4a).
  //   "rechazo"             → AFIP rechazó, mostramos motivo y botón de reintento (paso 4b).
  //   "timeout"             → polling superó el máximo sin respuesta de AFIP.
  const [estadoEmision, setEstadoEmision] = useState("idle");

  // F4-8: resultado del preflight (InvoiceEmissionPreflightDto). null si no se consultó aún.
  const [preflightInfo, setPreflightInfo] = useState(null);

  // Datos de la factura emitida (publicId, invoiceType, puntoDeVenta, numeroComprobante, CAE).
  const [facturaEmitidaData, setFacturaEmitidaData] = useState(null);
  // Motivo de rechazo de AFIP (solo cuando estadoEmision === "rechazo").
  const [motivoRechazo, setMotivoRechazo] = useState(null);

  // Referencia al intervalo de polling. La usamos para limpiarlo al desmontar.
  const pollIntervalRef = useRef(null);
  // Contador de intentos de polling. No necesita ser estado (no dispara re-render).
  const pollIntentosRef = useRef(0);
  // publicId de la factura que acabamos de encolar (para filtrar el poll).
  const facturaEncoladaPublicIdRef = useRef(null);

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
          // C6: servicios que el backend dejó afuera de la sugerencia, con el motivo.
          setExcludedServices(response?.excludedServices ?? []);

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

  // ── H2: limpieza del intervalo de polling al desmontar ────────────────────
  // useEffect con deps vacías: corre solo al montar/desmontar.
  // Garantiza que el interval no quede huérfano si el usuario cierra la ficha
  // mientras el polling está activo (ej: navega a otra página).
  useEffect(() => {
    return () => {
      if (pollIntervalRef.current) {
        clearInterval(pollIntervalRef.current);
      }
    };
  }, []);

  // ── H2: función que arranca el polling de estado fiscal ────────────────────
  /**
   * Inicia el poll a GET /invoices/reserva/{reservaId}/fiscal-status.
   * Se llama justo después de que el POST a /invoices da 200/201.
   *
   * Lógica:
   *   - Cada POLL_INTERVAL_MS consulta el estado.
   *   - Si llega "Issued" → muestra cartel verde + detiene el poll.
   *   - Si llega "Rejected" → muestra cartel rojo con motivo + detiene.
   *   - Si supera POLL_MAX_INTENTOS → muestra mensaje de timeout + detiene.
   *
   * @param {string} invoicePublicId - publicId de la factura recién encolada.
   */
  const iniciarPolling = useCallback((invoicePublicId) => {
    pollIntentosRef.current = 0;
    facturaEncoladaPublicIdRef.current = invoicePublicId;

    // Limpiamos cualquier intervalo anterior por seguridad (no debería haber uno).
    if (pollIntervalRef.current) {
      clearInterval(pollIntervalRef.current);
    }

    pollIntervalRef.current = setInterval(async () => {
      pollIntentosRef.current += 1;

      // Límite de intentos: si se agota sin respuesta de AFIP, mostramos el timeout.
      if (pollIntentosRef.current > POLL_MAX_INTENTOS) {
        clearInterval(pollIntervalRef.current);
        pollIntervalRef.current = null;
        setEstadoEmision("timeout");
        return;
      }

      try {
        const statusItems = await api.get(`/invoices/reserva/${reservaId}/fiscal-status`);
        const { estado, factura } = resolverEstadoFiscal(statusItems, facturaEncoladaPublicIdRef.current);

        if (estado === "Issued") {
          clearInterval(pollIntervalRef.current);
          pollIntervalRef.current = null;
          setFacturaEmitidaData(factura);
          setEstadoEmision("exito");
          // Avisamos al padre para que refresque la reserva y el extracto.
          // Lo hacemos acá (no al cerrar) para que los datos del extracto se actualicen
          // en cuanto llega el CAE, sin esperar a que el usuario haga clic en "Cerrar".
          onFacturaEmitida();
        } else if (estado === "Rejected") {
          clearInterval(pollIntervalRef.current);
          pollIntervalRef.current = null;
          setMotivoRechazo(factura?.rejectionReason || "AFIP no aceptó el comprobante.");
          setEstadoEmision("rechazo");
        }
        // "InProcess" o null → seguimos polling sin cambiar estado.
      } catch {
        // Error de red o 5xx durante el poll: no interrumpir, seguir intentando.
        // Si persiste, el límite de intentos lo frena.
      }
    }, POLL_INTERVAL_MS);
  }, [reservaId, onFacturaEmitida]);

  // ── H2: validación de la ficha antes de abrir el modal de confirmación ─────
  /**
   * F4-8 (2026-06-26): valida el formulario y luego consulta el preflight antes de confirmar.
   *
   * Flujo:
   *   1. Validación del formulario (igual que antes).
   *   2. GET /invoices/reserva/{id}/emission-preflight → verifica si la factura puede emitirse.
   *      Bloqueo duro: Factura A sin CUIT del cliente → muestra aviso y para.
   *      Fallback de error de red: si el endpoint no responde, continúa igual (no bloqueamos
   *      la emisión por un error de preflight — el backend vuelve a validar al emitir).
   *   3. Si pasa: abre el modal de confirmación normal (paso 2).
   *
   * Decisión UX (spec P1): la confirmación es el ÚLTIMO paso antes de algo irreversible.
   * Texto fiscal exacto (verificado contra ARCA — NO modificar):
   * "Una vez emitida no se puede eliminar; solo se corrige o anula con una Nota de Crédito."
   */
  const handleClickEmitir = async () => {
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
      const errorUSD = validarCamposUSD(tipoCambio, justificacionTC);
      if (errorUSD) {
        setErrorEnvio(errorUSD);
        return;
      }
    }

    // F4-8: preflight check — verifica condición fiscal del cliente antes de abrir el modal.
    // Si el endpoint falla (red, 404, etc.) saltamos al modal normal: el backend revalida al emitir.
    setEstadoEmision("preflight-cargando");
    try {
      const preflight = await api.get(`/invoices/reserva/${reservaId}/emission-preflight`);
      setPreflightInfo(preflight);
      const bloqueo = resolverPreflightBloqueo(preflight);
      if (bloqueo) {
        // El backend dice que esta factura no puede emitirse (ej: Factura A sin CUIT).
        setEstadoEmision("preflight-bloqueado");
        return;
      }
    } catch {
      // Error de red o endpoint aún no disponible: seguimos sin bloquear.
      // Fallback conservador: la regla fiscal la revalida el backend al emitir.
      setPreflightInfo(null);
    }

    // Todo válido → abrir el modal de confirmación (paso 2).
    setEstadoEmision("confirmando");
  };

  // ── H2: el usuario canceló en el modal de confirmación ────────────────────
  const handleCancelarConfirmacion = () => {
    setEstadoEmision("idle");
  };

  // ── H2: el usuario confirmó → enviamos a AFIP ─────────────────────────────
  /**
   * POST a /invoices. Al tener éxito, pasa al estado PROCESANDO y arranca el polling.
   * Los errores 409 (ya hay factura pendiente) siguen mostrándose en el error inline.
   */
  const handleConfirmarEmision = async () => {
    setEstadoEmision("procesando");
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

      // Campos multimoneda: solo se agregan cuando la moneda es USD.
      // Con ARS el payload es idéntico al original (backend usa defaults "PES"/1).
      if (esUSD) {
        payload.monId = "USD";
        payload.monCotiz = Number(tipoCambio);
        // ExchangeRateSource es un enum sin JsonStringEnumConverter → enviar el int.
        payload.exchangeRateSource = EXCHANGE_RATE_SOURCE_BNA_VENDEDOR_DIVISA;
        payload.exchangeRateFetchedAt = new Date().toISOString();
        payload.exchangeRateJustification = justificacionTC.trim();
      }

      const response = await api.post("/invoices", payload);

      // Guardamos el warning del backend si viene (InvoiceDto.Warning), pero NO
      // cerramos ni interrumpimos: el polling nos dirá el estado real de AFIP.
      if (response?.warning) {
        setWarningEmision(response.warning);
      }

      // Arrancamos el polling usando el publicId de la factura recién encolada.
      // Si el backend no devuelve el publicId, el poll usa la factura más reciente.
      const invoicePublicId = response?.publicId ?? response?.id ?? null;
      iniciarPolling(invoicePublicId);

    } catch (error) {
      // 409 cuando ya hay una Invoice PENDING para esta reserva.
      // El backend devuelve { message } con texto accionable.
      const mensaje = error?.payload?.message ?? getApiErrorMessage(error) ?? "Error al emitir la factura.";
      setEstadoEmision("idle");
      setErrorEnvio(mensaje);
    } finally {
      setSubmitting(false);
    }
  };

  // ── H2: "Corregir y reintentar" — vuelve a la ficha con todos los datos intactos ──
  // Decisión UX (spec P4): la factura NO salió. El usuario corrige y reenvía.
  // No limpiamos items, tributos, moneda ni tipo de cambio → el usuario corrige solo lo necesario.
  const handleReintentar = () => {
    setEstadoEmision("idle");
    setMotivoRechazo(null);
    setErrorEnvio(null);
    setWarningEmision(null);
    // F4-8: limpiamos el preflight para que en el próximo intento vuelva a verificar.
    setPreflightInfo(null);
  };

  // ── H2: "Cerrar" luego del éxito ──────────────────────────────────────────
  // onFacturaEmitida() ya fue llamado al detectar el estado "Issued" en el poll.
  // Este botón solo cierra la ficha inline visualmente.
  const handleCerrarExito = () => {
    onCancelar();
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

        {/* H2: el contenido del formulario solo se muestra en estado "idle".
            En los demás estados (procesando/éxito/rechazo/timeout) el cuerpo
            de la ficha queda vacío y el estado visual se muestra FUERA del div px-6 py-5. */}
        {/* F4-8: el formulario sigue visible mientras dura el preflight-cargando.
            Solo desaparece cuando el bloqueo ya se decidió o se avanza a confirmando. */}
        {!cargandoInicial && (estadoEmision === "idle" || estadoEmision === "preflight-cargando") && (
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

            {/* Aviso de servicios excluidos de la sugerencia (C6, Tanda 6, 2026-07-05) ─
                Antes esto pasaba en silencio: un servicio no confirmado, cancelado o en $0
                simplemente no generaba renglón (o generaba uno en $0 sin explicación), y el
                vendedor no entendía por qué el total no cerraba. Acá se lo contamos en criollo,
                servicio por servicio, con el motivo mapeado por describirMotivoExclusion
                (nunca el token técnico crudo del backend). */}
            {excludedServices.length > 0 && (
              <div
                data-testid="aviso-servicios-excluidos"
                className="rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-200"
                role="status"
              >
                <div className="flex items-start gap-2">
                  <AlertCircle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                  <div className="space-y-1">
                    <span className="font-semibold">
                      {excludedServices.length === 1
                        ? "Un servicio no entró en esta factura:"
                        : `${excludedServices.length} servicios no entraron en esta factura:`}
                    </span>
                    <ul className="list-disc space-y-0.5 pl-5">
                      {excludedServices.map((excluido, index) => (
                        <li key={`${excluido.description}-${index}`} data-testid={`item-excluido-${index}`}>
                          «{excluido.description}» no entra en la factura porque {describirMotivoExclusion(excluido.reason)}.
                        </li>
                      ))}
                    </ul>
                    {/* Si por esto el total propuesto quedó en $0, lo decimos explícito para
                        que el vendedor no piense que es un error del sistema. */}
                    {totales.total <= 0 && (
                      <p className="pt-1 font-medium">
                        Por eso el monto propuesto quedó en $0 — confirmá el servicio o cargá el importe a mano.
                      </p>
                    )}
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
      {/* F4-8: el pie también se muestra en "preflight-cargando" (botón deshabilitado con spinner). */}
      {!cargandoInicial && (estadoEmision === "idle" || estadoEmision === "preflight-cargando") && (
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
            {/* F4-8: en "preflight-cargando" los botones siguen visibles (el de emitir queda
                deshabilitado con spinner). Solo se ocultan en procesando/éxito/rechazo/timeout. */}
            {(estadoEmision === "idle" || estadoEmision === "preflight-cargando") && (
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
                  onClick={handleClickEmitir}
                  disabled={
                    // F4-8: mientras verifica el preflight, el botón queda deshabilitado
                    estadoEmision === "preflight-cargando" ||
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
                  {estadoEmision === "preflight-cargando" ? (
                    // Spinner mientras el preflight verifica la condición fiscal del cliente
                    <>
                      <Loader2 className="w-4 h-4 animate-spin" aria-hidden="true" />
                      Verificando...
                    </>
                  ) : requiereOverride
                    ? "Emitir por excepción"
                    : esUSD
                    ? "Emitir factura en USD"
                    : "Emitir factura"}
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {/* ── F4-8: Aviso de bloqueo de preflight (Factura A sin CUIT) ─────────
          Se muestra ANTES del modal de confirmación cuando el backend devuelve Allowed=false.
          Es el ÚNICO bloqueo duro que implementa la spec (ver InvoiceEmissionPreflightDto.cs).
          Solo tiene el botón "Volver" — no hay forma de forzarlo desde el front.
          data-testid="aviso-factura-a-no-corresponde" para QA y tests automatizados.
          ─────────────────────────────────────────────────────────────────── */}
      {estadoEmision === "preflight-bloqueado" && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
          role="dialog"
          aria-modal="true"
          aria-labelledby="preflight-block-title"
          aria-describedby="preflight-block-desc"
          data-testid="aviso-factura-a-no-corresponde"
        >
          <div className="relative bg-white dark:bg-slate-900 rounded-2xl shadow-xl max-w-md w-full mx-4 p-6 space-y-4">
            <div className="flex items-center gap-3">
              <div className="p-2 bg-amber-100 dark:bg-amber-900/30 rounded-lg">
                <AlertCircle className="w-6 h-6 text-amber-600 dark:text-amber-400" aria-hidden="true" />
              </div>
              <h2
                id="preflight-block-title"
                className="text-base font-semibold text-slate-900 dark:text-white"
              >
                No se puede emitir esta factura
              </h2>
            </div>

            <p
              id="preflight-block-desc"
              className="text-sm text-slate-700 dark:text-slate-300"
            >
              {preflightInfo?.reason ||
                "Este cliente no es Responsable Inscripto. No corresponde Factura A. " +
                "Revisá el tipo de comprobante o la condición del cliente."}
            </p>

            {/* Si el backend informa qué datos faltan, los mostramos */}
            {Array.isArray(preflightInfo?.missingData) && preflightInfo.missingData.length > 0 && (
              <div className="rounded-xl border border-amber-200 bg-amber-50 dark:border-amber-900/40 dark:bg-amber-900/20 px-4 py-3 text-sm text-amber-800 dark:text-amber-200">
                <p className="font-semibold mb-1">Datos faltantes:</p>
                <ul className="list-disc list-inside">
                  {preflightInfo.missingData.map((dato) => (
                    <li key={dato}>{dato}</li>
                  ))}
                </ul>
              </div>
            )}

            {/* "Volver" es la ÚNICA acción: no hay forma de forzar una Factura A sin CUIT. */}
            <div className="flex justify-end pt-2">
              <button
                type="button"
                onClick={() => setEstadoEmision("idle")}
                autoFocus
                data-testid="btn-volver-bloqueo-preflight"
                className="px-5 py-2 text-sm font-medium text-slate-700 dark:text-slate-200 border border-slate-300 dark:border-slate-600 rounded-xl hover:bg-slate-50 dark:hover:bg-slate-800 transition-colors"
              >
                Volver
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ── H2: Modal de confirmación antes de emitir (Paso 2) ──────────────
          El modal es una ventana chica centrada sobre la ficha (overlay local, no global).
          Foco inicial en "Volver" (el más seguro, para que Enter por error no confirme).
          Texto fiscal EXACTO verificado contra ARCA — NO modificar.
          ─────────────────────────────────────────────────────────────────── */}
      {estadoEmision === "confirmando" && (
        <div
          className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
          role="dialog"
          aria-modal="true"
          aria-labelledby="factura-confirm-dialog-title"
          aria-describedby="factura-confirm-dialog-desc"
        >
          <div className="relative bg-white dark:bg-slate-900 rounded-2xl shadow-xl max-w-md w-full mx-4 p-6 space-y-4">
            <div className="flex items-center gap-3">
              <div className="p-2 bg-indigo-100 dark:bg-indigo-900/30 rounded-lg">
                <Calculator className="w-6 h-6 text-indigo-600 dark:text-indigo-400" aria-hidden="true" />
              </div>
              <h2
                id="factura-confirm-dialog-title"
                className="text-base font-semibold text-slate-900 dark:text-white"
              >
                ¿Confirmás la emisión?
              </h2>
            </div>

            <div id="factura-confirm-dialog-desc" className="text-sm text-slate-600 dark:text-slate-300 space-y-2">
              {/* Texto fiscal exacto (verificado contra ARCA — NO modificar) */}
              <p className="font-semibold text-amber-700 dark:text-amber-400">
                Una vez emitida no se puede eliminar; solo se corrige o anula con una Nota de Crédito.
              </p>
              {clientName && (
                <p>
                  Cliente: <span className="font-medium">{clientName}</span>
                  {clientCuit && <span className="text-slate-500"> · CUIT {clientCuit}</span>}
                </p>
              )}
              <p>
                Total: <span className="font-bold text-slate-900 dark:text-white">
                  {formatCurrency(totales.total, monedaEfectiva)}
                </span>
              </p>
            </div>

            <div className="flex gap-3 pt-2">
              {/* "Volver" primero y con autoFocus: es la acción más segura */}
              <button
                type="button"
                onClick={handleCancelarConfirmacion}
                autoFocus
                data-testid="btn-volver-confirmar-factura"
                className="flex-1 rounded-xl border border-slate-300 dark:border-slate-600 px-4 py-2 text-sm font-medium text-slate-700 dark:text-slate-200 hover:bg-slate-50 dark:hover:bg-slate-800"
              >
                Volver
              </button>
              <button
                type="button"
                onClick={handleConfirmarEmision}
                data-testid="btn-si-emitir-factura"
                className="flex-1 rounded-xl bg-indigo-600 hover:bg-indigo-700 px-4 py-2 text-sm font-bold text-white"
              >
                Sí, emitir
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ── H2: Estado PROCESANDO (Paso 3) ───────────────────────────────────
          Se muestra en el mismo lugar que la ficha, sin cerrar ni navegar.
          role="status" + aria-live="polite" para que los lectores de pantalla lo anuncien.
          ─────────────────────────────────────────────────────────────────── */}
      {estadoEmision === "procesando" && (
        <div
          className="px-6 py-10 flex flex-col items-center gap-4 text-center"
          role="status"
          aria-live="polite"
          data-testid="estado-procesando-factura"
        >
          <Loader2 className="h-8 w-8 text-indigo-500 animate-spin" aria-hidden="true" />
          <p className="text-sm font-medium text-slate-700 dark:text-slate-300">
            Estamos emitiendo la factura en AFIP. En unos instantes vas a ver el número.
          </p>
          {/* Warning del backend (si vino) — no bloquea, solo informa */}
          {warningEmision && (
            <div
              data-testid="warning-emision"
              className="mt-2 rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-800 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-200"
            >
              <div className="flex items-start gap-2">
                <AlertCircle className="w-4 h-4 mt-0.5 flex-shrink-0" />
                <p>{warningEmision}</p>
              </div>
            </div>
          )}
        </div>
      )}

      {/* ── H2: Estado ÉXITO (Paso 4a) ────────────────────────────────────────
          El bloque "procesando" se transforma en este cartel verde sin recargar la página.
          El padre ya fue notificado (onFacturaEmitida) al detectar el estado Issued.
          ─────────────────────────────────────────────────────────────────── */}
      {estadoEmision === "exito" && facturaEmitidaData && (
        <div
          className="px-6 py-10 flex flex-col items-center gap-4 text-center"
          data-testid="estado-exito-factura"
        >
          <CheckCircle2 className="h-10 w-10 text-emerald-500" aria-hidden="true" />
          <div className="space-y-1">
            <p className="text-lg font-bold text-emerald-700 dark:text-emerald-400">
              ¡Factura emitida!
            </p>
            <p className="text-sm font-semibold text-slate-800 dark:text-slate-200">
              {labelFacturaEmitida(facturaEmitidaData)}
            </p>
            {facturaEmitidaData.cae && (
              <p className="text-xs text-slate-500 dark:text-slate-400">
                CAE: {facturaEmitidaData.cae}
                {facturaEmitidaData.vencimientoCAE && (
                  <> · Vto: {new Date(facturaEmitidaData.vencimientoCAE).toLocaleDateString("es-AR")}</>
                )}
              </p>
            )}
          </div>
          <button
            type="button"
            onClick={handleCerrarExito}
            data-testid="btn-cerrar-exito-factura"
            className="mt-2 px-5 py-2 text-sm font-semibold text-white bg-emerald-600 rounded-xl hover:bg-emerald-700 transition-colors"
          >
            Cerrar
          </button>
        </div>
      )}

      {/* ── H2: Estado RECHAZO (Paso 4b) ──────────────────────────────────────
          role="alert" para que los lectores de pantalla anuncien el error de AFIP.
          "Corregir y reintentar" vuelve a la ficha con todos los datos intactos.
          ─────────────────────────────────────────────────────────────────── */}
      {estadoEmision === "rechazo" && (
        <div
          className="px-6 py-8 flex flex-col items-center gap-4 text-center"
          role="alert"
          data-testid="estado-rechazo-factura"
        >
          <AlertCircle className="h-10 w-10 text-rose-500" aria-hidden="true" />
          <div className="space-y-2">
            <p className="text-base font-bold text-rose-700 dark:text-rose-400">
              AFIP rechazó la factura.
            </p>
            {motivoRechazo && (
              <div className="rounded-xl border border-rose-200 bg-rose-50 dark:border-rose-900/40 dark:bg-rose-900/20 px-4 py-3 text-sm text-rose-700 dark:text-rose-300 text-left">
                <span className="font-semibold">Motivo: </span>{motivoRechazo}
              </div>
            )}
          </div>
          <button
            type="button"
            onClick={handleReintentar}
            data-testid="btn-corregir-reintentar-factura"
            className="mt-2 px-5 py-2 text-sm font-semibold text-white bg-rose-600 rounded-xl hover:bg-rose-700 transition-colors"
          >
            Corregir y reintentar
          </button>
        </div>
      )}

      {/* ── H2: Estado TIMEOUT — el poll se agotó sin respuesta de AFIP ──────
          No es un error: la factura puede seguir siendo procesada en background.
          El usuario puede cerrar y verla más tarde en el extracto.
          ─────────────────────────────────────────────────────────────────── */}
      {estadoEmision === "timeout" && (
        <div
          className="px-6 py-8 flex flex-col items-center gap-4 text-center"
          role="status"
          aria-live="polite"
          data-testid="estado-timeout-factura"
        >
          <Loader2 className="h-8 w-8 text-slate-400" aria-hidden="true" />
          <div className="space-y-1">
            <p className="text-sm font-medium text-slate-700 dark:text-slate-300">
              Sigue en proceso, podés cerrar y verla más tarde.
            </p>
            <p className="text-xs text-slate-400">
              AFIP puede tardar unos minutos. El resultado va a aparecer en el Estado de Cuenta.
            </p>
          </div>
          <button
            type="button"
            onClick={onCancelar}
            data-testid="btn-cerrar-timeout-factura"
            className="mt-2 px-5 py-2 text-sm font-semibold border border-slate-300 text-slate-700 rounded-xl hover:bg-slate-50 dark:border-slate-600 dark:text-slate-300 dark:hover:bg-slate-800 transition-colors"
          >
            Cerrar
          </button>
        </div>
      )}
    </div>
  );
}

/**
 * Cuenta corriente del cliente — 4 solapas.
 *
 * Layout (Tanda D2, spec `docs/ux/2026-07-16-extracto-profesional-cuenta-cliente.md`):
 *   ┌── Barra superior ──────────────────────────────────────────────────────────┐
 *   │  ← Nombre + contactos + CUIT/DNI (una línea sobria, sin avatar)  [+Nuevo]  │
 *   ├── Foto de saldo (UN recuadro, una columna por moneda) ─────────────────────┤
 *   │  Facturado sin cobrar / Multas abiertas / Crédito a favor / SALDO         │
 *   │  + botón "Usar saldo a favor" (ficha inline) + lista de aplicaciones      │
 *   └──────────────────────────────────────────────────────────────────────────┘
 *   ┌── Solapas ───────────────────────────────────────────────────────────────┐
 *   │  Reservas  │  Estado de cuenta (default)  │  Facturación  │  Datos bancarios│
 *   └──────────────────────────────────────────────────────────────────────────┘
 *
 * Decisiones de diseño (spec sec.3, 2026-06-28 + rediseño 2026-07-16):
 *   P10=A: solapa del dinero se llama "Estado de cuenta".
 *   P11=A: datos bancarios del cliente en su propia solapa "Datos bancarios".
 *   Nombre "Facturación" (no "Facturación AFIP": AFIP/ARCA solo aparece en el chip de cada comprobante).
 *   (2026-07-16, P2/P3=A) Las 4 tarjetitas de resumen (Documentado/Cobrado/Reservas/
 *   Comprobantes) y los carteles sueltos ("Debe en $", "A FAVOR", "Crédito no aplicado",
 *   "Multa pendiente de cobro") se REEMPLAZAN por una única "foto de saldo"
 *   (FotoDeSaldoCuenta) que ya incluye las multas en su composición — ver §7 de la spec.
 */
import { useCallback, useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  AlertTriangle,
  ArrowLeft,
  Loader2,
  Mail,
  Phone,
  Receipt,
  Undo2,
} from "lucide-react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import CustomerPaymentModal from "../../../components/CustomerPaymentModal";
import { UsarSaldoAFavorInline } from "../components/UsarSaldoAFavorInline";
import { FotoDeSaldoCuenta } from "../components/FotoDeSaldoCuenta";
import { ListaCuentasBancarias } from "../../bank-accounts/components/ListaCuentasBancarias";
import { Button } from "../../../components/ui/button";
import {
  DataGrid,
  DataGridActionCell,
  DataGridBody,
  DataGridCell,
  DataGridEmptyState,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { DatabaseUnavailableState } from "../../../components/ui/DatabaseUnavailableState";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { ListToolbar } from "../../../components/ui/ListToolbar";
import { MobileRecordCard, MobileRecordList } from "../../../components/ui/MobileRecordCard";
import { AccountPageSkeleton } from "../../../components/ui/skeleton";
import { useDebounce } from "../../../hooks/useDebounce";
import { getApiErrorMessage, isDatabaseUnavailableError } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency, formatDate as formatDateShared } from "../../../lib/utils";
import { ReservaStatusBadge } from "../../reservas/components/ReservaStatusBadge";
import { getMoneyStatus, isReservaAnulada } from "../../reservas/moneyStatus";
import { formatTipoComprobante } from "../lib/facturacionFilters";
import { resolverFilaReservaAnuladaCuenta } from "../lib/pendingPenaltiesLogic";
import { prefijoDestinoAplicacionSaldo } from "../lib/creditWithdrawalLogic";
import { debeMostrarBannerDatosFiscales } from "../lib/datosClienteLogic";
import { EstadoCuentaClienteTab } from "../components/EstadoCuentaClienteTab";
import { FacturacionClienteTab } from "../components/FacturacionClienteTab";
import { DatosClienteTab } from "../components/DatosClienteTab";
// Tanda D2 (2026-07-16): MultaPendienteDeCobroBlock DEJA de renderizarse acá — su
// información pasa a la línea "Multas abiertas" de la foto de saldo (FotoDeSaldoCuenta)
// y a los renglones de multa dentro del extracto (EstadoCuentaClienteTab). El archivo
// del componente NO se borra (puede volver a hacer falta si el bloque se reintroduce en
// otra pantalla), solo deja de importarse/dibujarse acá.

// ─── Constantes de paginación (solo usadas por la solapa Reservas) ───────────

const emptyPage = {
  items: [],
  page: 1,
  pageSize: 25,
  totalCount: 0,
  totalPages: 0,
  hasPreviousPage: false,
  hasNextPage: false,
};

// ─── Helpers ─────────────────────────────────────────────────────────────────

// Bug "fechas corridas un día" (2026-07-17): esta copia local parseaba el string con
// new Date() y mostraba el día anterior para fechas-solo-día guardadas como medianoche
// UTC (startDate del viaje). Delega en la formatDate central de lib/utils, que ya
// distingue día-calendario (lee el texto tal cual) de instante-con-hora (hora local).
const formatDate = (dateString) => {
  if (!dateString) return "-";
  return formatDateShared(dateString);
};

/**
 * Tanda 6 (C4, 2026-07-05): plata REAL de una reserva por moneda, para la solapa Reservas.
 *
 * Antes esta pantalla mostraba SIEMPRE "ARS" hardcodeado, lo cual mentía en una reserva
 * en dólares o multimoneda. Ahora lee `reserva.porMoneda` (CustomerAccountReservaListItemDto,
 * misma fuente de plata que usa la ficha de la reserva) y solo cae al escalar legacy en ARS
 * si el backend todavía no trae esa lista (reserva muy vieja sin filas materializadas).
 */
function getLineasMonedaCuenta(reserva) {
  if (Array.isArray(reserva.porMoneda) && reserva.porMoneda.length > 0) {
    return reserva.porMoneda;
  }
  return [{ currency: "ARS", totalSale: reserva.totalSale, paid: reserva.paid, balance: reserva.balance }];
}

/**
 * Una línea de saldo de UNA moneda, en la solapa Reservas de la cuenta del cliente.
 * Mismo criterio visual que EstadoCuentaResumen (EjeBalanceMono/ColumnaBalanceMulti):
 * saldo negativo = "a favor" en verde (el cliente pagó de más), nunca rojo.
 */
function SaldoLineaCuenta({ balance, currency }) {
  const valor = balance ?? 0;
  if (valor < -0.01) {
    return (
      <div className="text-emerald-600 dark:text-emerald-400">
        {formatCurrency(Math.abs(valor), currency)}
        <span className="ml-1 text-[9px] font-semibold uppercase tracking-wider text-emerald-500 dark:text-emerald-600">
          a favor
        </span>
      </div>
    );
  }
  if (valor > 0.01) {
    return <div className="text-rose-600 dark:text-rose-400">{formatCurrency(valor, currency)}</div>;
  }
  return <div className="text-slate-400 dark:text-slate-500">{formatCurrency(valor, currency)}</div>;
}

// Clase de color según el "tono" que devuelve resolverFilaReservaAnuladaCuenta.
const TONO_CONTEXTO_ANULADA = {
  amber: "text-amber-600 dark:text-amber-400",
  emerald: "text-emerald-600 dark:text-emerald-400",
  neutral: "text-slate-400 dark:text-slate-500",
};

/**
 * Reemplaza la columna Saldo cuando la reserva de la fila está ANULADA (Tanda 6, C2+C4).
 * Nunca muestra "deuda" genérica: el contexto sale de `cancelledMoneyContext` (mismo
 * campo canónico que llena el backend en ReservationDebtRules.DeriveForCancelled).
 *
 * Multa "en revisión" (spec 2026-07-15, §5) — POR QUÉ ESTO NO USA getMoneyStatus PARA
 * DECIDIR SI ES MULTA: esta solapa es de la cuenta del CLIENTE (no el listado del
 * vendedor). Acá SÍ se muestra la multa confirmada sin comprobante todavía (decisión
 * del dueño); en cambio `moneyStatus.js` la esconde a propósito para el vendedor (una
 * promesa de cobro sin papel no se le muestra) y esa regla compartida NO se toca.
 *
 * Toda la decisión de QUÉ texto/monto/color pintar vive en `resolverFilaReservaAnuladaCuenta`
 * (pendingPenaltiesLogic.js, capa pura y testeada) — este componente solo la llama y pinta
 * el resultado, sin volver a decidir nada acá (fix de revisión N1/N2/N3: el monto de una
 * multa "en revisión" o sin `cancelledPenaltyAmount` explícito NUNCA se inventa desde acá).
 */
function ContextoAnuladaCuenta({ reserva }) {
  const monedaFallback = reserva.porMoneda?.[0]?.currency ?? "ARS";
  // moneyStatusKind solo hace falta para el caso "saldo a favor" (getMoneyStatus ya valida
  // el balance con tolerancia de redondeo); la parte de multa se resuelve con el token
  // cancelledMoneyContext directo, sin pasar por esta función compartida con el vendedor.
  const moneyStatusKind = getMoneyStatus(reserva).kind;

  const fila = resolverFilaReservaAnuladaCuenta({
    cancelledMoneyContext: reserva.cancelledMoneyContext,
    cancelledPenaltyAmount: reserva.cancelledPenaltyAmount,
    cancelledPenaltyCurrency: reserva.cancelledPenaltyCurrency,
    balance: reserva.balance,
    moneyStatusKind,
    monedaFallback,
  });

  if (fila.tono === "neutral") {
    return <div className={TONO_CONTEXTO_ANULADA.neutral}>—</div>;
  }

  return (
    <div className={TONO_CONTEXTO_ANULADA[fila.tono]}>
      {fila.montoTexto && <span className="mr-1">{fila.montoTexto}</span>}
      <span className="text-[9px] font-semibold uppercase tracking-wider">{fila.texto}</span>
    </div>
  );
}

/**
 * Renderiza el PDF de la factura en una pestaña nueva del navegador.
 * La "cáscara" HTML se abre primero con un spinner (para no ser bloqueado como popup),
 * y luego se reemplaza con el PDF cuando llega.
 *
 * Helpers escapeHtml y renderInvoiceTab son funciones puras (sin React) que se reutilizan
 * para el estado loading y el estado final con iframe.
 */
const escapeHtml = (value) =>
  String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");

const renderInvoiceTab = (previewWindow, { title, body }) => {
  if (!previewWindow || previewWindow.closed) return;
  previewWindow.document.open();
  previewWindow.document.write(`<!doctype html><html lang="es"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" /><title>${escapeHtml(title)}</title><style>:root{color-scheme:light;font-family:Inter,system-ui,sans-serif;background:#e2e8f0;color:#0f172a}*{box-sizing:border-box}body{margin:0;min-height:100vh;background:linear-gradient(180deg,#f8fafc 0%,#e2e8f0 100%)}.shell{min-height:100vh;display:flex;flex-direction:column}.header{padding:16px 20px;border-bottom:1px solid #cbd5e1;background:rgba(255,255,255,.96);backdrop-filter:blur(10px)}.eyebrow{margin:0 0 6px;font-size:11px;font-weight:800;letter-spacing:.14em;text-transform:uppercase;color:#4f46e5}.title{margin:0;font-size:20px;font-weight:700}.subtitle{margin:6px 0 0;font-size:14px;color:#475569}.content{flex:1;padding:20px}.panel{height:calc(100vh - 117px);border:1px solid #cbd5e1;border-radius:18px;overflow:hidden;background:#fff;box-shadow:0 20px 50px rgba(15,23,42,.15)}.state{height:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:12px;padding:24px;text-align:center}.state-title{margin:0;font-size:18px;font-weight:700}.state-text{margin:0;max-width:480px;color:#475569;line-height:1.5}.spinner{width:42px;height:42px;border:4px solid #cbd5e1;border-top-color:#4f46e5;border-radius:999px;animation:spin .9s linear infinite}iframe{width:100%;height:100%;border:0;background:#fff}@keyframes spin{to{transform:rotate(360deg)}}</style></head><body>${body}</body></html>`);
  previewWindow.document.close();
};

// B1: el estado de la reserva se muestra con el badge canónico (estados en inglés del backend
// → etiquetas en español de negocio). El StatusBadge local fue eliminado porque usaba
// claves en español que no coincidían con los valores del backend, causando que estados como
// "InManagement", "Confirmed", "Traveling", etc. se mostraran en inglés crudo al usuario.
// ReservaStatusBadge importado de features/reservas/components/ReservaStatusBadge.jsx.

// ─── Componente principal ─────────────────────────────────────────────────────

export default function CustomerAccountPage() {
  const { publicId } = useParams();
  const navigate = useNavigate();

  // ── Estado del overview (resumen del encabezado) ──────────────────────────
  const [overview, setOverview] = useState(null);
  const [loadingOverview, setLoadingOverview] = useState(true);
  const [databaseUnavailable, setDatabaseUnavailable] = useState(false);

  // ── Solapa activa — default "estadoDeCuenta" (decisión UX P12=A) ──────────
  const [activeTab, setActiveTab] = useState("estadoDeCuenta");

  // ── refreshKey para el Estado de cuenta ──────────────────────────────────
  // El padre lo incrementa al registrar/eliminar un cobro para que
  // EstadoCuentaClienteTab se recargue automáticamente (mismo patrón que SupplierExtractoSection).
  const [extractoRefreshKey, setExtractoRefreshKey] = useState(0);

  // ── Paginación y datos de la solapa Reservas (única que tiene paginación propia) ──
  const [reservasPage, setReservasPage] = useState(emptyPage);
  const [reservasPaging, setReservasPaging] = useState({ page: 1, pageSize: 25 });
  const [tabLoadingReservas, setTabLoadingReservas] = useState(true);
  const [searchTerm, setSearchTerm] = useState("");
  const debouncedSearch = useDebounce(searchTerm, 300);

  // ── Opciones de reservas para el modal de cobro ──────────────────────────

  // ── Modal de cobro (CustomerPaymentModal) ────────────────────────────────
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [paymentToEdit, setPaymentToEdit] = useState(null);
  const [paymentContext, setPaymentContext] = useState(null);

  // ── Estado de cuenta (extracto libro mayor, GET .../account/statement) ────
  // Se levanta acá (no dentro de EstadoCuentaClienteTab) porque las tarjetas
  // "Ventas"/"Cobrado" del encabezado también necesitan el desglose por moneda
  // que trae este extracto — así se pide UNA sola vez por carga, no dos.
  const [estadoCuenta, setEstadoCuenta] = useState(null);
  const [loadingEstadoCuenta, setLoadingEstadoCuenta] = useState(true);
  const [errorEstadoCuenta, setErrorEstadoCuenta] = useState(null);

  // ── Saldo a favor: ficha inline y aplicaciones revertibles ───────────────
  // monedaFichaUsarSaldo: moneda del cartel que disparó la apertura de la ficha (o null)
  const [monedaFichaUsarSaldo, setMonedaFichaUsarSaldo] = useState(null);
  const [creditApplications, setCreditApplications] = useState([]);
  const [loadingCreditApplications, setLoadingCreditApplications] = useState(false);
  const [deudaClientePorReserva, setDeudaClientePorReserva] = useState(null);

  // ── Estado del formulario inline de reversión de aplicaciones ────────────
  const [revirtiendoAplicacionId, setRevirtiendoAplicacionId] = useState(null);
  const [motivoReversion, setMotivoReversion] = useState("");
  const [guardandoReversion, setGuardandoReversion] = useState(false);
  const [errorReversion, setErrorReversion] = useState(null);

  // ── Carga del overview (encabezado) ──────────────────────────────────────

  const loadOverview = useCallback(async () => {
    setLoadingOverview(true);
    try {
      const response = await api.get(`/customers/${publicId}/account`);
      setOverview(response);
      setDatabaseUnavailable(false);
    } catch (error) {
      setOverview(null);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("No se pudo cargar la cuenta corriente.");
    } finally {
      setLoadingOverview(false);
    }
  }, [publicId]);

  // Carga deuda por reserva para el picker de "Aplicar saldo a reserva específica"
  const loadDeudaClientePorReserva = useCallback(async () => {
    try {
      const response = await api.get(`/customers/${publicId}/account/debt-by-reserva`);
      setDeudaClientePorReserva(response?.reservas ?? []);
    } catch (error) {
      // No bloquea la pantalla: el picker quedará vacío si falla
      console.warn("[CustomerAccountPage] No se pudo cargar deuda por reserva:", error?.message);
      setDeudaClientePorReserva([]);
    }
  }, [publicId]);

  // Carga aplicaciones VIVAS de saldo a favor (FC4 — las que se pueden revertir)
  const loadCreditApplications = useCallback(async () => {
    setLoadingCreditApplications(true);
    try {
      const creditOverview = await api.get(`/customers/${publicId}/credit`);
      setCreditApplications(Array.isArray(creditOverview?.activeApplications) ? creditOverview.activeApplications : []);
    } catch (error) {
      console.warn("[CustomerAccountPage] No se pudo cargar aplicaciones de saldo:", error?.message);
      setCreditApplications([]);
    } finally {
      setLoadingCreditApplications(false);
    }
  }, [publicId]);

  // Carga el extracto de cuenta (libro mayor) del cliente, calculado en el servidor.
  // Fuente única: alimenta tanto la tabla de EstadoCuentaClienteTab como las tarjetas
  // "Ventas"/"Cobrado" del encabezado (desglosadas por moneda), así se pide UNA sola
  // vez por carga en vez de que cada consumidor haga su propio fetch.
  const loadEstadoCuenta = useCallback(async () => {
    setLoadingEstadoCuenta(true);
    setErrorEstadoCuenta(null);
    try {
      const response = await api.get(`/customers/${publicId}/account/statement`);
      setEstadoCuenta(response);
    } catch (error) {
      setEstadoCuenta(null);
      setErrorEstadoCuenta(getApiErrorMessage(error) || "No se pudo cargar el estado de cuenta.");
    } finally {
      setLoadingEstadoCuenta(false);
    }
    // extractoRefreshKey como dependencia: el padre lo sube al registrar/eliminar un cobro,
    // así el extracto se recarga solo sin que el usuario tenga que refrescar la página.
  }, [publicId, extractoRefreshKey]); // eslint-disable-line react-hooks/exhaustive-deps

  // ── Carga de la solapa Reservas (paginada) ────────────────────────────────

  const loadReservas = useCallback(async () => {
    setTabLoadingReservas(true);
    try {
      const params = new URLSearchParams({
        page: String(reservasPaging.page),
        pageSize: String(reservasPaging.pageSize),
        sortBy: "createdAt",
        sortDir: "desc",
      });
      if (debouncedSearch.trim()) params.set("search", debouncedSearch.trim());

      const response = await api.get(`/customers/${publicId}/account/reservas?${params.toString()}`);
      setReservasPage({ ...emptyPage, ...(response || {}) });
      setDatabaseUnavailable(false);
    } catch (error) {
      setReservasPage(emptyPage);
      setDatabaseUnavailable(isDatabaseUnavailableError(error));
      showError("No se pudieron cargar las reservas.");
    } finally {
      setTabLoadingReservas(false);
    }
  }, [publicId, reservasPaging.page, reservasPaging.pageSize, debouncedSearch]);

  // ── refreshAll: recarga el encabezado + solapa reservas + extracto ────────

  const refreshAll = useCallback(async () => {
    // Incrementar refreshKey hace que EstadoCuentaClienteTab se recargue automáticamente
    // (lo tiene como dependencia de su useEffect de carga).
    setExtractoRefreshKey((prev) => prev + 1);
    await Promise.all([
      loadOverview(),
      loadCreditApplications(),
      loadDeudaClientePorReserva(),
    ]);
  }, [loadOverview, loadCreditApplications, loadDeudaClientePorReserva]);

  // ── Effects de carga ──────────────────────────────────────────────────────

  // Resetea el estado cuando cambia el cliente en la URL
  useEffect(() => {
    setActiveTab("estadoDeCuenta");
    setSearchTerm("");
    setReservasPaging({ page: 1, pageSize: 25 });
    setExtractoRefreshKey(0);
  }, [publicId]);

  // Carga inicial del encabezado y datos auxiliares
  useEffect(() => {
    loadOverview();
    loadCreditApplications();
    loadDeudaClientePorReserva();
  }, [loadOverview, loadCreditApplications, loadDeudaClientePorReserva]);

  // Carga el extracto (Estado de cuenta) al montar, cuando cambia el cliente,
  // y cada vez que extractoRefreshKey sube (refreshAll lo incrementa tras un cobro).
  useEffect(() => {
    loadEstadoCuenta();
  }, [loadEstadoCuenta]);

  // Carga de la solapa Reservas cuando es la solapa activa
  useEffect(() => {
    if (activeTab === "reservas") {
      loadReservas();
    }
  }, [activeTab, loadReservas]);

  // Resetea la página de reservas cuando cambia el término de búsqueda
  useEffect(() => {
    if (activeTab === "reservas") {
      setReservasPaging((current) => ({ ...current, page: 1 }));
    }
  }, [activeTab, debouncedSearch]);

  // ── Handlers ──────────────────────────────────────────────────────────────

  const handleOpenModal = (payment = null, context = null) => {
    setPaymentToEdit(payment);
    setPaymentContext(context);
    setIsModalOpen(true);
  };

  const handleOpenInvoicePreview = async (invoice) => {
    const previewWindow = window.open("", "_blank");
    if (!previewWindow) {
      showError("El navegador bloqueó la apertura de la factura.");
      return;
    }
    previewWindow.opener = null;

    // B2: usa formatTipoComprobante (cubre todos los tipos A/B/C/M, NC, ND).
    // Antes había un tipoMap incompleto con fallback "Tipo ${int}" que exponía el
    // código ARCA al usuario para tipos no mapeados (ND 2/7/12, Factura M 51, etc.).
    const pdv = String(invoice.puntoDeVenta ?? 0).padStart(5, "0");
    const num = String(invoice.numeroComprobante ?? 0).padStart(8, "0");
    const tipo = formatTipoComprobante(invoice.tipoComprobante);
    const invoiceTitle = `${tipo} ${pdv}-${num}`;

    renderInvoiceTab(previewWindow, {
      title: invoiceTitle,
      body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(invoiceTitle)}</h1><p class="subtitle">Preparando la factura para mostrarla en esta pestaña.</p></div><div class="content"><div class="panel"><div class="state"><div class="spinner"></div><p class="state-title">Cargando factura...</p></div></div></div></div>`,
    });

    try {
      const blob = await api.get(`/invoices/${getPublicId(invoice)}/pdf`, { responseType: "blob" });
      if (!(blob instanceof Blob) || blob.size === 0) throw new Error("La factura no devolvió un PDF válido.");

      const pdfUrl = URL.createObjectURL(blob);
      const releaseTimer = window.setInterval(() => {
        if (previewWindow.closed) { URL.revokeObjectURL(pdfUrl); window.clearInterval(releaseTimer); }
      }, 1000);

      renderInvoiceTab(previewWindow, {
        title: invoiceTitle,
        body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(invoiceTitle)}</h1><p class="subtitle">Vista del comprobante emitido.</p></div><div class="content"><div class="panel"><iframe src="${pdfUrl}" title="${escapeHtml(invoiceTitle)}"></iframe></div></div></div>`,
      });
    } catch {
      // E1: NO interpolamos error.message en la ventana del comprobante.
      // Cuando el PDF endpoint falla sin body, api.js asigna error.message = statusText
      // (ej: "Not Found", "Forbidden", "Internal Server Error") — inglés técnico que no
      // debe llegar al usuario. Usamos texto fijo en español para este estado de error.
      renderInvoiceTab(previewWindow, {
        title: invoiceTitle,
        body: `<div class="shell"><div class="header"><p class="eyebrow">Facturación</p><h1 class="title">${escapeHtml(invoiceTitle)}</h1><p class="subtitle">No fue posible abrir el comprobante.</p></div><div class="content"><div class="panel"><div class="state"><p class="state-title">No se pudo cargar el comprobante</p><p class="state-text">No se pudo abrir el comprobante. Probá de nuevo en un momento.</p></div></div></div></div>`,
      });
    }
  };

  // Tanda D1 (2026-07-16): hay DOS rutas de reversión según el destino de la aplicación
  // (mismo mecanismo interno, rutas separadas para que el backend distinga cada caso en
  // su auditoría — ver ReverseCustomerCreditPenaltyApplication en CustomersController.cs).
  // `destinationKind` viene del DTO de la aplicación, nunca se adivina acá.
  const handleRevertirAplicacion = async (applicationPublicId, destinationKind) => {
    setGuardandoReversion(true);
    setErrorReversion(null);
    try {
      const ruta = destinationKind === "multa" ? "penalty-applications" : "applications";
      await api.post(
        `/customers/${publicId}/credit/${ruta}/${applicationPublicId}/reverse`,
        { reason: motivoReversion.trim() || null }
      );
      setRevirtiendoAplicacionId(null);
      setMotivoReversion("");
      await refreshAll();
    } catch (error) {
      setErrorReversion(getApiErrorMessage(error, "No se pudo revertir la aplicación. Intentá de nuevo."));
    } finally {
      setGuardandoReversion(false);
    }
  };

  // ── Datos derivados ───────────────────────────────────────────────────────

  const summary = overview?.summary || {};
  const customer = overview?.customer;
  const reservas = reservasPage.items || [];

  // Reservas sin cancelar: para el picker del modal de cobro
  const availableReservas = deudaClientePorReserva || [];

  // Filtra por moneda para el picker de "aplicar saldo a reserva específica"
  const getReservasConDeudaEnMoneda = useCallback((moneda) => {
    if (!deudaClientePorReserva) return [];
    return deudaClientePorReserva.filter((reserva) => {
      const lineaMoneda = (reserva.debtByCurrency ?? []).find(
        (c) => c.currency === moneda && (c.amount ?? 0) > 0
      );
      return lineaMoneda != null;
    });
  }, [deudaClientePorReserva]);

  // ── Render ────────────────────────────────────────────────────────────────

  if (loadingOverview) return <AccountPageSkeleton />;

  if (!overview && !databaseUnavailable) {
    return <div className="py-12 text-center text-muted-foreground">No se encontraron datos del cliente.</div>;
  }

  return (
    <div className="space-y-6">
      {/* ── Barra superior: identidad sobria (P3=A, sin avatar) + acción de cabecera ── */}
      <div className="flex flex-col justify-between gap-4 sm:flex-row sm:items-center">
        <div className="flex items-center gap-4">
          <Button variant="ghost" size="icon" onClick={() => navigate("/customers")}>
            <ArrowLeft className="h-5 w-5" />
          </Button>
          <div>
            <h1 className="text-xl font-bold tracking-tight text-slate-900 dark:text-white">{customer?.fullName}</h1>
            {/* Una sola línea sobria: nombre + contactos + CUIT/DNI (spec 2026-07-16, P3=A).
                Antes había un círculo gigante con la inicial + tarjetas repetidas; el
                nombre ya lo dice el título de arriba, esto solo agrega el contacto. */}
            <div className="mt-0.5 flex flex-wrap gap-x-3 gap-y-1 text-sm text-slate-500 dark:text-slate-400">
              {customer?.email && (
                <span className="flex items-center gap-1">
                  <Mail className="h-3.5 w-3.5" /> {customer.email}
                </span>
              )}
              {customer?.phone && (
                <span className="flex items-center gap-1">
                  <Phone className="h-3.5 w-3.5" /> {customer.phone}
                </span>
              )}
              {customer?.taxId && <span>CUIT/DNI {customer.taxId}</span>}
            </div>
          </div>
        </div>

        {/* La propuesta nueva nace en el circuito único Reserva-Presupuesto. */}
        <Button
          type="button"
          variant="outline"
          onClick={() => navigate(`/reservas?create=1&customerPublicId=${publicId}`)}
          className="gap-2 border-indigo-200 text-indigo-700 hover:bg-indigo-50 hover:text-indigo-800 dark:border-indigo-800 dark:text-indigo-300 dark:hover:bg-indigo-900/20"
          disabled={databaseUnavailable}
        >
          <Receipt className="h-4 w-4" />
          Nuevo presupuesto
        </Button>
      </div>

      {/*
        ── Foto de saldo (Tanda D2, spec 2026-07-16) ───────────────────────────
        UN solo recuadro reemplaza las 4 tarjetitas (Documentado/Cobrado/Reservas/
        Comprobantes) + los carteles sueltos (chip "Debe", "A FAVOR", "Crédito no
        aplicado", "Multa pendiente de cobro"). La composición YA incluye las multas
        (summary.balanceCompositionByCurrency) — el front no vuelve a sumar nada.
      */}
      <FotoDeSaldoCuenta
        composicion={summary.balanceCompositionByCurrency}
        unappliedCreditByCurrency={summary.unappliedCreditByCurrency}
        loading={loadingOverview}
        canUsarSaldo={hasPermission("cobranzas.edit")}
        monedaFichaAbierta={monedaFichaUsarSaldo}
        onToggleUsarSaldo={(moneda) =>
          setMonedaFichaUsarSaldo(monedaFichaUsarSaldo === moneda ? null : moneda)
        }
      />

      {/* Ficha inline "Usar saldo a favor": cuelga de la foto de saldo (EN LÍNEA, nunca
          ventana flotante), debajo de ella. `saldoDisponible` sale de creditBalanceByCurrency
          (el mismo número que ya usa la línea "Crédito a favor" de la foto). */}
      {monedaFichaUsarSaldo && (
        <UsarSaldoAFavorInline
          publicId={publicId}
          moneda={monedaFichaUsarSaldo}
          saldoDisponible={
            Number(
              summary.creditBalanceByCurrency?.find((c) => c.currency === monedaFichaUsarSaldo)?.amount ?? 0
            )
          }
          reservasConDeuda={getReservasConDeudaEnMoneda(monedaFichaUsarSaldo)}
          pendingPenaltyItems={overview?.pendingPenalties?.items}
          onConfirmado={async () => {
            setMonedaFichaUsarSaldo(null);
            await refreshAll();
          }}
          onCancelar={() => setMonedaFichaUsarSaldo(null)}
        />
      )}

      {/*
        ── Aplicaciones VIVAS de saldo a favor ─────────────────────────────────
        Muestra saldos que ya se aplicaron (a otra reserva O a una multa, Tanda D1) y
        que se pueden revertir. Cada fila tiene un formulario inline de reversión (sin
        modal). Título renombrado a "Saldo a favor aplicado" (spec 2026-07-16 §6): cubre
        los dos destinos posibles, no solo "otras reservas".
      */}
      {creditApplications.length > 0 && (
        <div className="rounded-xl border border-indigo-200 bg-indigo-50/40 dark:border-indigo-900/40 dark:bg-indigo-950/10 overflow-hidden">
          <div className="px-5 py-3 border-b border-indigo-100 dark:border-indigo-900/30">
            <h3 className="text-sm font-bold text-indigo-800 dark:text-indigo-300">
              Saldo a favor aplicado
            </h3>
          </div>
          <ul className="divide-y divide-indigo-100 dark:divide-indigo-900/20">
            {creditApplications.map((aplicacion) => {
              const estaRevirtiendoEsta = revirtiendoAplicacionId === String(aplicacion.applicationPublicId);
              const simbolo = aplicacion.currency === "USD" ? "US$" : "$";
              const monto = Number(aplicacion.amount).toLocaleString("es-AR", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
              const fechaTexto = aplicacion.appliedAt ? new Date(aplicacion.appliedAt).toLocaleDateString("es-AR") : "—";

              return (
                <li key={String(aplicacion.applicationPublicId)} className="px-5 py-3">
                  <div className="flex items-start justify-between gap-3 flex-wrap">
                    <div className="min-w-0">
                      <p className="text-sm font-semibold text-slate-800 dark:text-slate-200">
                        {prefijoDestinoAplicacionSaldo(aplicacion.destinationKind)}{" "}
                        <Link
                          to={`/reservas/${aplicacion.targetReservaPublicId}`}
                          className="text-indigo-600 hover:text-indigo-700 dark:text-indigo-400 dark:hover:text-indigo-300 hover:underline"
                        >
                          {aplicacion.targetReservaNumber ?? "la reserva"}
                        </Link>
                        <span className="ml-2 font-bold text-indigo-700 dark:text-indigo-400">
                          −{simbolo}{monto}
                        </span>
                      </p>
                      {aplicacion.targetReservaHolderName && (
                        <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">
                          Titular: {aplicacion.targetReservaHolderName}
                        </p>
                      )}
                      <p className="text-xs text-slate-400 dark:text-slate-500 mt-0.5">
                        Aplicado el {fechaTexto}
                      </p>
                    </div>
                    {hasPermission("cobranzas.edit") && !estaRevirtiendoEsta && (
                      <button
                        type="button"
                        onClick={() => {
                          setRevirtiendoAplicacionId(String(aplicacion.applicationPublicId));
                          setMotivoReversion("");
                          setErrorReversion(null);
                        }}
                        className="flex-shrink-0 flex items-center gap-1.5 rounded-lg border border-indigo-200 bg-white px-3 py-1.5 text-xs font-semibold text-indigo-700 hover:bg-indigo-50 dark:border-indigo-800 dark:bg-slate-900 dark:text-indigo-400 dark:hover:bg-indigo-950/30 transition-colors"
                        data-testid={`revertir-aplicacion-${aplicacion.applicationPublicId}`}
                      >
                        <Undo2 className="h-3.5 w-3.5" />
                        Revertir
                      </button>
                    )}
                  </div>
                  {estaRevirtiendoEsta && (
                    <div className="mt-3 space-y-2 rounded-lg bg-white dark:bg-slate-800 border border-indigo-200 dark:border-indigo-900/40 p-3">
                      <label
                        htmlFor={`motivo-reversion-${aplicacion.applicationPublicId}`}
                        className="text-xs font-semibold text-slate-600 dark:text-slate-400"
                      >
                        Motivo de la reversión (opcional)
                      </label>
                      <textarea
                        id={`motivo-reversion-${aplicacion.applicationPublicId}`}
                        rows={2}
                        value={motivoReversion}
                        onChange={(e) => setMotivoReversion(e.target.value)}
                        disabled={guardandoReversion}
                        placeholder="Indicá el motivo si lo tenés..."
                        className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-indigo-400 dark:border-slate-700 dark:bg-slate-900 dark:text-white disabled:opacity-50 resize-none"
                        data-testid={`motivo-reversion-${aplicacion.applicationPublicId}`}
                      />
                      {errorReversion && (
                        <p className="text-xs text-rose-600 dark:text-rose-400" role="alert">{errorReversion}</p>
                      )}
                      <div className="flex justify-end gap-2">
                        <button
                          type="button"
                          onClick={() => { setRevirtiendoAplicacionId(null); setMotivoReversion(""); setErrorReversion(null); }}
                          disabled={guardandoReversion}
                          className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 disabled:opacity-50 transition-colors"
                        >
                          Cancelar
                        </button>
                        <button
                          type="button"
                          onClick={() => handleRevertirAplicacion(String(aplicacion.applicationPublicId), aplicacion.destinationKind)}
                          disabled={guardandoReversion}
                          className="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-3 py-1.5 text-xs font-bold text-white hover:bg-indigo-700 disabled:opacity-50 transition-colors"
                          data-testid={`confirmar-reversion-${aplicacion.applicationPublicId}`}
                        >
                          {guardandoReversion ? (
                            <><Loader2 className="h-3 w-3 animate-spin" />Revirtiendo…</>
                          ) : (
                            <><Undo2 className="h-3 w-3" />Confirmar reversión</>
                          )}
                        </button>
                      </div>
                    </div>
                  )}
                </li>
              );
            })}
          </ul>
          {loadingCreditApplications && (
            <div className="px-5 py-3 text-xs text-slate-400 flex items-center gap-1.5">
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
              Actualizando aplicaciones...
            </div>
          )}
        </div>
      )}

      {/*
        ── Banner "Faltan los datos fiscales" (spec 2026-07-17, §4) ─────────────
        Espejo exacto del banner del operador (supplier-missing-tax-condition-banner):
        franja ámbar de una línea, SOLO informativa (no bloquea nada de esta pantalla).
        Se enciende con overview.hasPendingTaxData — NUNCA se recalcula acá (el mismo
        veredicto que hoy traba facturar/anular/devolver, ver docstring del DTO backend).
        Nace del callejón sin salida que reportó Gastón: la devolución avisaba "completá
        la condición fiscal" pero la ficha no tenía forma de editarla — ahora el botón
        de este banner lleva directo a la solapa "Datos" nueva.
      */}
      {debeMostrarBannerDatosFiscales(overview?.hasPendingTaxData) && (
        <div
          data-testid="customer-missing-tax-condition-banner"
          className="flex items-center gap-2 rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-900 dark:border-amber-800/50 dark:bg-amber-950/30 dark:text-amber-200"
        >
          <AlertTriangle className="h-4 w-4 flex-shrink-0 text-amber-600 dark:text-amber-400" aria-hidden="true" />
          <span>
            <span className="font-bold">Faltan los datos fiscales de este cliente.</span>{" "}
            Completá su condición fiscal para poder facturar, anular y emitir devoluciones sin trabas.
          </span>
          <button
            type="button"
            onClick={() => setActiveTab("datos")}
            data-testid="customer-missing-tax-condition-cta"
            className="ml-auto flex-shrink-0 rounded-lg border border-amber-300 bg-white px-3 py-1 text-xs font-bold text-amber-800 transition-colors hover:bg-amber-100 dark:border-amber-700 dark:bg-slate-800 dark:text-amber-200 dark:hover:bg-amber-900/30"
          >
            Completar datos
          </button>
        </div>
      )}

      {/* ── Solapas ─────────────────────────────────────────────────────────── */}
      <div className="flex flex-col gap-4">
        {/* Barra de solapas con data-testid estables */}
        <div className="flex flex-wrap gap-6 border-b border-slate-200 dark:border-slate-800">
          {[
            { key: "reservas", label: "Reservas", count: summary.reservaCount || 0 },
            { key: "estadoDeCuenta", label: "Estado de cuenta", count: null },
            { key: "facturacion", label: "Facturación", count: summary.invoiceCount || 0 },
            { key: "datosBancarios", label: "Datos bancarios", count: null },
            { key: "datos", label: "Datos", count: null },
          ].map((tab) => (
            <button
              key={tab.key}
              type="button"
              onClick={() => setActiveTab(tab.key)}
              className={`relative pb-4 text-sm font-semibold transition-colors ${
                activeTab === tab.key
                  ? "text-indigo-600 dark:text-indigo-400"
                  : "text-slate-500 hover:text-slate-700 dark:hover:text-slate-300"
              }`}
              data-testid={`tab-${tab.key}`}
            >
              <span className="flex items-center gap-2">
                {tab.label}
                {tab.count !== null && (
                  <span className="rounded-full bg-slate-100 px-1.5 py-0.5 text-[10px] text-slate-500 dark:bg-slate-800">
                    {tab.count}
                  </span>
                )}
              </span>
              {activeTab === tab.key && (
                <div className="absolute bottom-0 left-0 right-0 h-0.5 rounded-full bg-indigo-600 dark:bg-indigo-400" />
              )}
            </button>
          ))}
        </div>

        {/* ── Contenido de la solapa Reservas ──────────────────────────────── */}
        {activeTab === "reservas" && (
          <>
            {/* Buscador de reservas */}
            <ListToolbar
              className="p-3"
              searchSlot={
                <div className="relative min-w-[260px]">
                  <Receipt className="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
                  <input
                    type="text"
                    placeholder="Buscar en reservas..."
                    value={searchTerm}
                    onChange={(e) => setSearchTerm(e.target.value)}
                    className="w-full rounded-xl border border-slate-200 bg-slate-50 py-2 pl-9 pr-4 text-sm transition-shadow focus:ring-2 focus:ring-slate-200 dark:border-slate-800 dark:bg-slate-900 dark:text-white"
                  />
                </div>
              }
            />
            {databaseUnavailable ? (
              <DatabaseUnavailableState />
            ) : tabLoadingReservas && reservas.length === 0 ? (
              <div className="flex h-48 items-center justify-center text-slate-400">
                <Loader2 className="h-8 w-8 animate-spin" />
              </div>
            ) : (
              <>
                <DataGrid density="compact" minWidth="900px">
                  <DataGridHeader>
                    <DataGridHeaderRow>
                      <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                      <DataGridHeaderCell>Reserva</DataGridHeaderCell>
                      <DataGridHeaderCell>Estado</DataGridHeaderCell>
                      <DataGridHeaderCell align="right">Venta</DataGridHeaderCell>
                      <DataGridHeaderCell align="right">Cobrado</DataGridHeaderCell>
                      <DataGridHeaderCell align="right">Saldo</DataGridHeaderCell>
                      <DataGridHeaderCell align="right">Acción</DataGridHeaderCell>
                    </DataGridHeaderRow>
                  </DataGridHeader>
                  <DataGridBody>
                    {reservas.length === 0 ? (
                      <DataGridEmptyState colSpan={7} title="No hay reservas para mostrar." />
                    ) : (
                      reservas.map((reserva) => {
                        // C4: plata real por moneda (fallback a escalares+ARS si el DTO no la trae).
                        // C2: ContextoAnuladaCuenta decide si esta fila "debe" de verdad o es una
                        // reserva anulada con su propio contexto — nunca recalculamos balance>0 acá.
                        const lineasMoneda = getLineasMonedaCuenta(reserva);
                        const esAnuladaFila = isReservaAnulada(reserva);
                        return (
                          <DataGridRow key={getPublicId(reserva)}>
                            <DataGridCell>{formatDate(reserva.startDate || reserva.createdAt)}</DataGridCell>
                            <DataGridCell>
                              <div className="font-semibold text-slate-900 dark:text-white">{reserva.numeroReserva}</div>
                              <div className="text-xs text-slate-500 dark:text-slate-400">{reserva.name}</div>
                            </DataGridCell>
                            <DataGridCell><ReservaStatusBadge status={reserva.status} /></DataGridCell>
                            <DataGridCell align="right" className="font-semibold text-slate-900 dark:text-white">
                              {lineasMoneda.map((linea) => (
                                <div key={linea.currency}>{formatCurrency(linea.totalSale, linea.currency)}</div>
                              ))}
                            </DataGridCell>
                            <DataGridCell align="right" className="font-semibold text-emerald-600 dark:text-emerald-400">
                              {lineasMoneda.map((linea) => (
                                <div key={linea.currency}>{formatCurrency(linea.paid, linea.currency)}</div>
                              ))}
                            </DataGridCell>
                            <DataGridCell align="right" className="font-semibold">
                              {esAnuladaFila ? (
                                <ContextoAnuladaCuenta reserva={reserva} />
                              ) : (
                                lineasMoneda.map((linea) => (
                                  <SaldoLineaCuenta key={linea.currency} balance={linea.balance} currency={linea.currency} />
                                ))
                              )}
                            </DataGridCell>
                            <DataGridActionCell>
                              <Link
                                to={`/reservas/${getPublicId(reserva)}`}
                                className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-semibold text-indigo-600 hover:bg-indigo-50 dark:border-slate-700 dark:hover:bg-slate-800"
                              >
                                Ver
                              </Link>
                            </DataGridActionCell>
                          </DataGridRow>
                        );
                      })
                    )}
                  </DataGridBody>
                </DataGrid>

                {reservas.length === 0 ? (
                  <ListEmptyState
                    title="No hay reservas para mostrar."
                    className="md:hidden rounded-xl border border-dashed border-slate-200 dark:border-slate-800"
                  />
                ) : (
                  <MobileRecordList>
                    {reservas.map((reserva) => {
                      const lineasMoneda = getLineasMonedaCuenta(reserva);
                      // Fix M1 (review frontend 2026-07-17): la fila desktop ya pasaba la reserva
                      // completa (:836) para que isReservaAnulada pueda leer reserva.isVoided del
                      // backend; acá pasaba solo el status y se salteaba ese campo si el DTO lo suma.
                      const esAnuladaFila = isReservaAnulada(reserva);
                      return (
                        <MobileRecordCard
                          key={getPublicId(reserva)}
                          statusSlot={<ReservaStatusBadge status={reserva.status} />}
                          title={reserva.numeroReserva}
                          subtitle={reserva.name}
                          meta={
                            <>
                              <div className="text-xs text-slate-500 dark:text-slate-400">
                                Fecha: {formatDate(reserva.startDate || reserva.createdAt)}
                              </div>
                              {lineasMoneda.map((linea) => (
                                <div key={linea.currency} className="text-xs text-slate-500 dark:text-slate-400">
                                  Venta {formatCurrency(linea.totalSale, linea.currency)} · Cobrado {formatCurrency(linea.paid, linea.currency)}
                                </div>
                              ))}
                            </>
                          }
                          footer={
                            <div className="text-sm font-semibold">
                              {esAnuladaFila ? (
                                <ContextoAnuladaCuenta reserva={reserva} />
                              ) : (
                                lineasMoneda.map((linea) => (
                                  <SaldoLineaCuenta key={linea.currency} balance={linea.balance} currency={linea.currency} />
                                ))
                              )}
                            </div>
                          }
                          footerActions={
                            <Link
                              to={`/reservas/${getPublicId(reserva)}`}
                              className="inline-flex rounded-lg border border-slate-200 px-3 py-2 text-xs font-semibold text-indigo-600 hover:bg-indigo-50 dark:border-slate-700 dark:hover:bg-slate-800"
                            >
                              Ver
                            </Link>
                          }
                        />
                      );
                    })}
                  </MobileRecordList>
                )}

                <PaginationFooter
                  page={reservasPage.page || reservasPaging.page}
                  pageSize={reservasPage.pageSize || reservasPaging.pageSize}
                  totalCount={reservasPage.totalCount || 0}
                  totalPages={reservasPage.totalPages || 0}
                  hasPreviousPage={Boolean(reservasPage.hasPreviousPage)}
                  hasNextPage={Boolean(reservasPage.hasNextPage)}
                  onPageChange={(page) => setReservasPaging((prev) => ({ ...prev, page }))}
                  onPageSizeChange={(pageSize) => setReservasPaging({ page: 1, pageSize })}
                />
              </>
            )}
          </>
        )}

        {/* ── Contenido de la solapa Estado de cuenta ──────────────────────── */}
        {activeTab === "estadoDeCuenta" && (
          <EstadoCuentaClienteTab
            estadoCuenta={estadoCuenta}
            loading={loadingEstadoCuenta}
            error={errorEstadoCuenta}
            onRetry={loadEstadoCuenta}
            onNuevaCobranza={() => handleOpenModal(null)}
            canRegistrarCobranza={hasPermission("cobranzas.edit")}
          />
        )}

        {/* ── Contenido de la solapa Facturación ───────────────────────────── */}
        {/* Antes se llamaba "Facturación AFIP" — renombrado a "Facturación".
            AFIP/ARCA solo aparece como chip de estado en cada comprobante. */}
        {activeTab === "facturacion" && (
          <FacturacionClienteTab
            customerPublicId={publicId}
            onVerFactura={handleOpenInvoicePreview}
          />
        )}

        {/* ── Contenido de la solapa Datos bancarios ───────────────────────── */}
        {/* Los datos bancarios del cliente (CBU/alias) se movieron del encabezado
            a esta solapa propia (decisión UX P11=A, 2026-06-28). */}
        {activeTab === "datosBancarios" && (
          <ListaCuentasBancarias
            ownerType="Customer"
            ownerId={publicId}
            title="Datos bancarios del cliente"
            canEdit={hasPermission("clientes.edit")}
          />
        )}

        {/* ── Contenido de la solapa Datos ──────────────────────────────────
            Edición en línea de identidad + condición fiscal del cliente (spec
            2026-07-17). Reemplaza al modal del listado como lugar para editar
            desde la ficha; ese modal sigue existiendo solo para el alta. */}
        {activeTab === "datos" && (
          <div>
            <h2 className="mb-6 text-lg font-semibold">Datos del cliente</h2>
            <DatosClienteTab
              customerPublicId={publicId}
              taxIdLocked={overview?.taxIdLocked}
              canEdit={hasPermission("clientes.edit")}
              onGuardado={loadOverview}
            />
          </div>
        )}
      </div>

      {/* ── Modal (solo CustomerPaymentModal) ─────────────────────────────── */}
      {/*
        CustomerPaymentModal: permanece como modal de acción rápida para registrar cobros.
        Se abre desde el botón "Nuevo cobro" dentro de la solapa Estado de cuenta.
        TODO (mejora a futuro): migrar a ficha en línea como el cobro en la reserva
        (RegistrarCobroInline), pero eso es un cambio de mayor envergadura fuera de scope.

        Eliminar cobro / Anular recibo / Ver factura ya NO se ofrecen desde este extracto
        (que cruza TODAS las reservas del cliente, de solo lectura): esas acciones viven en
        el extracto de la reserva puntual (EstadoCuentaExtracto dentro de ReservaDetailPage),
        con el contexto completo del comprobante. El link de cada renglón lleva directo ahí.
      */}
      <CustomerPaymentModal
        isOpen={isModalOpen}
        onClose={() => { setIsModalOpen(false); setPaymentToEdit(null); setPaymentContext(null); }}
        paymentToEdit={paymentToEdit}
        customerId={publicId}
        availableReservas={availableReservas}
        initialReservaPublicId={paymentContext?.reservaPublicId}
        initialLinkedInvoicePublicId={paymentContext?.linkedInvoicePublicId}
        initialImputedCurrency={paymentContext?.imputedCurrency}
        onSave={refreshAll}
      />
    </div>
  );
}

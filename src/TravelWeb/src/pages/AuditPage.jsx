import { useState, useEffect, useCallback } from "react";
import { api } from "../api";
import { format, isToday, isYesterday, formatDistanceToNow } from "date-fns";
import { es } from "date-fns/locale";
import {
  Shield,
  Search,
  Download,
  ChevronLeft,
  ChevronRight,
  ChevronDown,
  ChevronUp,
  Filter,
  Plus,
  Pencil,
  Trash2,
  ArrowRight,
  RefreshCw,
  X,
  Briefcase,
  Server,
  LogIn,
  LogOut,
  KeyRound,
} from "lucide-react";

// ================================================================
// TRADUCCIONES COMPLETAS
// ================================================================

const entityTranslations = {
  // Operativas
  Reserva: "Reserva", Customer: "Cliente", Supplier: "Proveedor",
  Payment: "Cobro", Invoice: "Factura", Passenger: "Pasajero",
  ServicioReserva: "Servicio", FlightSegment: "Vuelo",
  HotelBooking: "Hotel", PackageBooking: "Paquete", TransferBooking: "Transfer",
  Lead: "Posible cliente", LeadActivity: "Actividad Lead",
  Quote: "Cotización", QuoteItem: "Ãtem Cotización",
  ReservaAttachment: "Adjunto", SupplierPayment: "Pago a proveedor",
  ManualCashMovement: "Movimiento de caja", CommissionRule: "Regla de comisión",
  Rate: "Tarifa", PaymentReceipt: "Recibo de pago",
  InvoiceItem: "Ãtem de factura", InvoiceTribute: "Tributo de factura",
  CatalogPackage: "Paquete catálogo", CatalogPackageDeparture: "Salida de paquete",
  Country: "País", Destination: "Destino", DestinationDeparture: "Salida de destino",
  WhatsAppDelivery: "Mensaje WhatsApp",
  // Sistema
  ApplicationUser: "Usuario", RefreshToken: "Sesión",
  AgencySettings: "Config. agencia", OperationalFinanceSettings: "Config. finanzas",
  AfipSettings: "Config. AFIP", WhatsAppBotConfig: "Config. WhatsApp Bot",
  BusinessSequence: "Numeración", RolePermission: "Permiso de rol",
  BnaExchangeRateSnapshot: "Cotización BNA", Notification: "Notificación",
  // Eventos de negocio
  Session: "Sesión", Report: "Reporte", User: "Usuario",
};

const actionTranslations = {
  Create: "Creó", Update: "Modificó", Delete: "Eliminó", SoftDelete: "Envió a papelera",
  Login: "Inició sesión", LoginFailed: "Intento de login fallido",
  Logout: "Cerró sesión", ChangePassword: "Cambió contraseña",
  InvoiceIssued: "Emitió factura", InvoiceForced: "Forzó factura",
  ReportExported: "Exportó reporte", UserCreated: "Creó usuario",
  PermissionsChanged: "Cambió permisos", WhatsAppSent: "Envió WhatsApp",
};

const actionColors = {
  Create: "bg-emerald-500", Update: "bg-blue-500",
  Delete: "bg-red-500", SoftDelete: "bg-amber-500",
  Login: "bg-indigo-500", LoginFailed: "bg-red-500",
  Logout: "bg-slate-500", ChangePassword: "bg-violet-500",
  InvoiceIssued: "bg-emerald-500", InvoiceForced: "bg-amber-500",
  ReportExported: "bg-cyan-500", UserCreated: "bg-emerald-500",
  PermissionsChanged: "bg-violet-500", WhatsAppSent: "bg-green-500",
};

const actionBadgeColors = {
  Create: "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
  Update: "bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300",
  Delete: "bg-red-50 text-red-700 dark:bg-red-900/30 dark:text-red-300",
  SoftDelete: "bg-amber-50 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
  Login: "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300",
  LoginFailed: "bg-red-50 text-red-700 dark:bg-red-900/30 dark:text-red-300",
  Logout: "bg-slate-50 text-slate-700 dark:bg-slate-800/50 dark:text-slate-300",
  ChangePassword: "bg-violet-50 text-violet-700 dark:bg-violet-900/30 dark:text-violet-300",
};

// Mapa EXHAUSTIVO de campos de entidad â†’ español legible
const fieldTranslations = {
  // Generales
  Id: "ID", PublicId: "Identificador",
  Name: "Nombre", FullName: "Nombre completo", LegalName: "Razón social",
  Description: "Descripción", Notes: "Notas", InternalNotes: "Notas internas",
  Status: "Estado", IsActive: "Activo", IsDeleted: "Eliminado",
  Email: "Email", Phone: "Teléfono", Address: "Dirección",
  CreatedAt: "Fecha de creación", UpdatedAt: "Ãšltima modificación",
  DeletedAt: "Fecha de eliminación", ClosedAt: "Fecha de cierre",
  CreatedBy: "Creado por", UploadedBy: "Subido por", UploadedAt: "Fecha de subida",

  // Reservas
  NumeroReserva: "Nro. reserva", TotalSale: "Venta total", TotalCost: "Costo total",
  TotalPaid: "Total pagado", Balance: "Saldo", GrossMargin: "Margen bruto",
  StartDate: "Fecha de salida", EndDate: "Fecha de regreso",
  PayerId: "Cliente pagador", ResponsibleUserId: "Responsable",
  SourceLeadId: "Lead origen", SourceQuoteId: "Cotización origen",
  WhatsAppPhoneOverride: "Teléfono WhatsApp",

  // Pasajeros
  DocumentType: "Tipo de documento", DocumentNumber: "Nro. de documento",
  BirthDate: "Fecha de nacimiento", Nationality: "Nacionalidad", Gender: "Género",

  // Servicios
  ServiceType: "Tipo de servicio", ProductType: "Tipo de producto",
  ConfirmationNumber: "Nro. de confirmación", SupplierName: "Proveedor",
  SalePrice: "Precio de venta", NetCost: "Costo neto", Commission: "Comisión",
  Tax: "Impuesto", CommissionPercent: "% comisión",
  DepartureDate: "Fecha de salida", ReturnDate: "Fecha de regreso",

  // Pagos
  Amount: "Importe", Method: "Método de pago", PaidAt: "Fecha de pago",
  Reference: "Referencia", EntryType: "Tipo de movimiento",
  AffectsCash: "Afecta caja", OriginalPaymentId: "Pago original",
  RelatedInvoiceId: "Factura asociada", ReceiptNumber: "Nro. de recibo",

  // Vuelos
  AirlineCode: "Aerolínea", FlightNumber: "Nro. de vuelo",
  Origin: "Origen", Destination: "Destino",
  DepartureTime: "Hora de salida", ArrivalTime: "Hora de llegada",
  CabinClass: "Clase", Baggage: "Equipaje", BaggageIncluded: "Equipaje incluido",
  PNR: "PNR", TicketNumber: "Nro. de ticket", FareBase: "Tarifa base",
  IsRoundTrip: "Ida y vuelta", OriginCity: "Ciudad origen", DestinationCity: "Ciudad destino",
  AirlineName: "Nombre aerolínea",

  // Hoteles
  HotelName: "Hotel", CheckIn: "Fecha de ingreso", CheckOut: "Fecha de salida",
  RoomType: "Tipo de habitación", MealPlan: "Régimen de comidas",
  Nights: "Noches", City: "Ciudad", StarRating: "Estrellas",
  RoomCategory: "Categoría", RoomFeatures: "Características",
  HotelPriceType: "Tipo de precio", Rooms: "Habitaciones",
  RoomBase: "Base habitación",

  // Transfers
  PickupLocation: "Punto de recogida", DropoffLocation: "Punto de destino",
  PickupDateTime: "Fecha y hora de recogida", ReturnDateTime: "Fecha y hora de regreso",
  VehicleType: "Tipo de vehículo", MaxPassengers: "Máx. pasajeros",

  // Paquetes
  PackageName: "Nombre del paquete", DurationDays: "Duración (días)",
  Adults: "Adultos", Children: "Menores",
  IncludesFlight: "Incluye vuelo", IncludesHotel: "Incluye hotel",
  IncludesTransfer: "Incluye transfer", IncludesExcursions: "Incluye excursiones",
  IncludesInsurance: "Incluye seguro", IncludesMeals: "Incluye comidas",
  Itinerary: "Itinerario",

  // Facturas
  TipoComprobante: "Tipo de comprobante", NumeroComprobante: "Nro. de comprobante",
  PuntoDeVenta: "Punto de venta", CAE: "CAE", VencimientoCAE: "Vto. CAE",
  IssuedAt: "Fecha de emisión", IsVoided: "Anulada", VoidedAt: "Fecha anulación",
  WasForced: "Fue forzada", ForceReason: "Motivo de forzado",
  ForcedByUserId: "Forzada por (ID)", ForcedByUserName: "Forzada por",
  OutstandingBalanceAtIssuance: "Saldo al emitir", Resultado: "Resultado",
  CustomerSnapshot: "Datos del cliente", AgencySnapshot: "Datos de la agencia",
  BaseImponible: "Base imponible", ImporteNeto: "Importe neto",
  ImporteTotal: "Importe total", ImporteIva: "IVA",
  Alicuota: "Alícuota", AlicuotaIvaId: "Alícuota IVA",
  TaxCondition: "Condición fiscal", TaxConditionId: "Condición fiscal (ID)",
  TaxId: "CUIT/DNI",

  // CRM / Leads
  Source: "Origen", Priority: "Prioridad",
  InterestedIn: "Interesado en", EstimatedBudget: "Presupuesto estimado",
  TravelDates: "Fechas de viaje", TravelStartDate: "Inicio de viaje",
  TravelEndDate: "Fin de viaje", Travelers: "Viajeros",
  NextFollowUp: "Próximo seguimiento",
  ConvertedCustomerId: "Cliente convertido", ConvertedReservaId: "Reserva generada",
  AssignedToUserId: "Asignado a (ID)", AssignedToName: "Asignado a",
  AcceptedAt: "Fecha de aceptación",
  QuoteNumber: "Nro. de cotización", ValidUntil: "Válida hasta",
  LeadId: "Lead asociado", QuoteId: "Cotización asociada",

  // Cotizaciones
  Total: "Total", UnitPrice: "Precio unitario", UnitCost: "Costo unitario",
  Quantity: "Cantidad", ProductName: "Producto",

  // Caja
  Direction: "Dirección", Category: "Categoría",
  OccurredAt: "Fecha", RelatedReservaId: "Reserva asociada",
  RelatedSupplierId: "Proveedor asociado",

  // Adjuntos
  FileName: "Nombre del archivo", FileSize: "Tamaño", ContentType: "Tipo de archivo",
  StoredFileName: "Archivo almacenado",

  // Catálogo
  Title: "Título", Slug: "URL amigable", Tagline: "Subtítulo",
  IsPublished: "Publicado", PublishedAt: "Fecha de publicación",
  GeneralInfo: "Información general",
  HeroImageFileName: "Imagen principal", HeroImageStoredFileName: "Archivo de imagen",
  HeroImageContentType: "Tipo de imagen", HeroImageFileSize: "Tamaño de imagen",
  CountryName: "País", CountrySlug: "URL del país",
  TransportLabel: "Transporte", Currency: "Moneda",
  DisplayOrder: "Orden", DestinationOrder: "Orden destino",
  CatalogPackageId: "Paquete", DestinationId: "Destino", CountryId: "País",

  // Proveedores extras
  ContactName: "Nombre de contacto", Cuit: "CUIT",
  CreditLimit: "Límite de crédito", CurrentBalance: "Saldo actual",
  DefaultCommissionPercent: "% comisión por defecto",

  // Clientes extras
  AgencyName: "Agencia",

  // Comisiones
  ValidFrom: "Válida desde", ValidTo: "Válida hasta",
  MarkupPercent: "% markup", ChildrenPayPercent: "% pago menores",
  ChildMaxAge: "Edad máx. menores",

  // WhatsApp
  MessageText: "Mensaje", AttachmentName: "Adjunto", BotMessageId: "ID mensaje bot",
  SentAt: "Fecha de envío", SentBy: "Enviado por", Error: "Error",
  Kind: "Tipo", IsPrimary: "Principal",
  WelcomeMessage: "Mensaje de bienvenida", AskInterestMessage: "Mensaje de interés",
  AskTravelersMessage: "Mensaje de viajeros", AskDatesMessage: "Mensaje de fechas",
  ThanksMessage: "Mensaje de agradecimiento", DuplicateMessage: "Mensaje duplicado",
  AgentRequestMessage: "Mensaje solicitud agente",

  // Config sistema
  AfipInvoiceControlMode: "Modo control AFIP",
  RequireFullPaymentForOperativeStatus: "Requiere pago completo para operativa",
  RequireFullPaymentForVoucher: "Requiere pago completo para voucher",
  EnableUpcomingUnpaidReservationNotifications: "Alertas reservas impagas",
  UpcomingUnpaidReservationAlertDays: "Días de anticipación alerta",

  // Tokens / Sesión (sistema)
  TokenHash: "Sesión", ReplacedByTokenHash: "Reemplazada por",
  RevokedAt: "Fecha de revocación", ExpiresAt: "Fecha de expiración",
  CreatedByIp: "IP de creación", UserAgent: "Navegador",
  IsPersistent: "Sesión persistente",
  RoleName: "Rol", Permission: "Permiso",

  // Secuencias
  LastValue: "Ãšltimo valor", Year: "Año",

  // BNA
  UsdSeller: "Dólar (venta)", EuroSeller: "Euro (venta)", RealSeller: "Real (venta)",
  PublishedDate: "Fecha publicación", PublishedTime: "Hora publicación",
  FetchedAt: "Consultado",

  // Notificaciones
  Message: "Mensaje", IsRead: "Leída", IsDismissed: "Descartada",
  RelatedEntityType: "Tipo entidad", RelatedEntityId: "ID entidad",
  UserId: "Usuario",

  // Paquetes
  ServiceDetailsJson: "Detalles del servicio",
  PreparedAt: "Fecha de preparación", Segments: "Segmentos",

  // Misceláneos
  Type: "Tipo", Importe: "Importe", Items: "Ãtems",
  TributeId: "Tributo", Tributes: "Tributos",
  SupplierId: "Proveedor", CustomerId: "Cliente",
  ReservaId: "Reserva", ServicioReservaId: "Servicio",
  InvoiceId: "Factura", PaymentId: "Pago",
  OriginalInvoiceId: "Factura original",
};

function translateEntity(name) { return entityTranslations[name] || name; }
function translateAction(action) { return actionTranslations[action] || action; }
function translateField(field) { return fieldTranslations[field] || field; }

function formatTimestamp(ts) {
  const date = new Date(ts);
  if (isToday(date)) return `Hoy ${format(date, "HH:mm", { locale: es })}`;
  if (isYesterday(date)) return `Ayer ${format(date, "HH:mm", { locale: es })}`;
  return format(date, "d MMM yyyy HH:mm", { locale: es });
}

function formatRelative(ts) {
  return formatDistanceToNow(new Date(ts), { addSuffix: true, locale: es });
}

// ================================================================
// COMPONENTES
// ================================================================

function ActionIcon({ action, className = "" }) {
  const base = `flex-shrink-0 h-7 w-7 rounded-full flex items-center justify-center ${actionColors[action] || "bg-gray-500"} ${className}`;
  const iconClass = "h-3.5 w-3.5 text-white";

  if (action === "Login") return <span className={base}><LogIn className={iconClass} /></span>;
  if (action === "LoginFailed") return <span className={base}><LogIn className={iconClass} /></span>;
  if (action === "Logout") return <span className={base}><LogOut className={iconClass} /></span>;
  if (action === "ChangePassword") return <span className={base}><KeyRound className={iconClass} /></span>;
  if (action === "Create" || action === "UserCreated") return <span className={base}><Plus className={iconClass} /></span>;
  if (action === "Delete" || action === "SoftDelete") return <span className={base}><Trash2 className={iconClass} /></span>;
  return <span className={base}><Pencil className={iconClass} /></span>;
}

function NarrativeSummary({ log }) {
  let changes = {};
  try { changes = JSON.parse(log.changes || "{}"); } catch {}

  const changedFields = Object.keys(changes);
  const fieldCount = changedFields.length;
  const entity = translateEntity(log.entityName);

  // Eventos de negocio
  if (["Login", "LoginFailed", "Logout", "ChangePassword"].includes(log.action)) {
    return (
      <span className="text-sm text-slate-700 dark:text-slate-300">
        <span className="font-semibold text-slate-900 dark:text-white">{log.userName || "Sistema"}</span>
        {" "}{translateAction(log.action).toLowerCase()}
      </span>
    );
  }

  let summary = "";
  if (log.action === "Create") {
    summary = `creó ${entity}`;
  } else if (log.action === "Delete") {
    summary = `eliminó ${entity}`;
  } else if (log.action === "SoftDelete") {
    summary = `envió a papelera ${entity}`;
  } else if (log.action === "Update") {
    if (fieldCount === 0) {
      summary = `modificó ${entity}`;
    } else if (fieldCount <= 3) {
      const translatedFields = changedFields.map(f => translateField(f)).join(", ");
      summary = `modificó ${translatedFields} de ${entity}`;
    } else {
      const firstTwo = changedFields.slice(0, 2).map(f => translateField(f)).join(", ");
      summary = `modificó ${firstTwo} y ${fieldCount - 2} campo(s) más de ${entity}`;
    }
  } else {
    summary = `${translateAction(log.action).toLowerCase()} â€” ${entity}`;
  }

  return (
    <span className="text-sm text-slate-700 dark:text-slate-300">
      <span className="font-semibold text-slate-900 dark:text-white">{log.userName || "Sistema"}</span>
      {" "}{summary}
    </span>
  );
}

function ChangeDetail({ changes: raw }) {
  let changes = {};
  try { changes = JSON.parse(raw || "{}"); } catch { return null; }

  const entries = Object.entries(changes);
  if (entries.length === 0) return null;

  return (
    <div className="space-y-1.5">
      {entries.map(([key, value]) => (
        <div key={key} className="flex flex-wrap items-center gap-1.5 text-xs">
          <span className="font-medium text-slate-500 dark:text-slate-400 min-w-[120px]">
            {translateField(key)}:
          </span>
          {typeof value === "object" && value !== null && "Old" in value && "New" in value ? (
            <span className="flex items-center gap-1.5">
              <span className="line-through text-red-400 dark:text-red-400/70">{formatValue(value.Old)}</span>
              <ArrowRight className="w-3 h-3 text-slate-400" />
              <span className="text-emerald-600 dark:text-emerald-400 font-medium">{formatValue(value.New)}</span>
            </span>
          ) : (
            <span className="text-slate-600 dark:text-slate-300">{formatValue(value)}</span>
          )}
        </div>
      ))}
    </div>
  );
}

function formatValue(val) {
  if (val === null || val === undefined) return "â€”";
  if (typeof val === "boolean") return val ? "Sí" : "No";
  if (val === "True" || val === "true") return "Sí";
  if (val === "False" || val === "false") return "No";
  const str = String(val);
  if (str.length > 80) return str.substring(0, 80) + "â€¦";
  return str;
}

// ================================================================
// TABS
// ================================================================

const TABS = [
  { id: "operational", label: "Operativa", icon: Briefcase, description: "Reservas, clientes, pagos y más" },
  { id: "system", label: "Sistema", icon: Server, description: "Usuarios, sesiones, configuración" },
];

// ================================================================
// PÃGINA PRINCIPAL
// ================================================================

export default function AuditPage() {
  const [logs, setLogs] = useState([]);
  const [loading, setLoading] = useState(true);
  const [totalCount, setTotalCount] = useState(0);
  const [totalPages, setTotalPages] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize] = useState(25);

  // Tab activo
  const [activeTab, setActiveTab] = useState("operational");

  // Filtros
  const [entityName, setEntityName] = useState("");
  const [action, setAction] = useState("");
  const [userId, setUserId] = useState("");
  const [dateFrom, setDateFrom] = useState("");
  const [dateTo, setDateTo] = useState("");
  const [searchTerm, setSearchTerm] = useState("");
  const [showFilters, setShowFilters] = useState(false);

  // Expand
  const [expandedId, setExpandedId] = useState(null);

  // Datos para dropdowns
  const [entities, setEntities] = useState([]);
  const [users, setUsers] = useState([]);

  const systemEntities = ["ApplicationUser", "RefreshToken", "AgencySettings", "OperationalFinanceSettings", "AfipSettings", "WhatsAppBotConfig", "BusinessSequence", "RolePermission", "BnaExchangeRateSnapshot", "Notification", "Session", "User", "Report"];
  const uiEntities = entities.filter(e => activeTab === "system" ? systemEntities.includes(e) : !systemEntities.includes(e));

  useEffect(() => {
    api.get("/auditlogs/entities").then(setEntities).catch(() => {});
    api.get("/auditlogs/users").then(setUsers).catch(() => {});
  }, []);

  const fetchLogs = useCallback(async () => {
    try {
      setLoading(true);
      const params = new URLSearchParams();
      params.set("page", page);
      params.set("pageSize", pageSize);
      params.set("category", activeTab);
      if (entityName) params.set("entityName", entityName);
      if (action) params.set("action", action);
      if (userId) params.set("userId", userId);
      if (dateFrom) params.set("dateFrom", dateFrom);
      if (dateTo) params.set("dateTo", dateTo);
      if (searchTerm) params.set("searchTerm", searchTerm);

      const res = await api.get(`/auditlogs/global?${params.toString()}`);
      setLogs(res.items || []);
      setTotalCount(res.totalCount || 0);
      setTotalPages(res.totalPages || 0);
    } catch (err) {
      console.error("Error fetching audit logs:", err);
    } finally {
      setLoading(false);
    }
  }, [page, pageSize, activeTab, entityName, action, userId, dateFrom, dateTo, searchTerm]);

  useEffect(() => { fetchLogs(); }, [fetchLogs]);

  const handleTabChange = (tab) => {
    setActiveTab(tab);
    setPage(1);
    setEntityName("");
    setAction("");
    setExpandedId(null);
  };

  const handleClearFilters = () => {
    setEntityName(""); setAction(""); setUserId("");
    setDateFrom(""); setDateTo(""); setSearchTerm(""); setPage(1);
  };

  const hasActiveFilters = entityName || action || userId || dateFrom || dateTo || searchTerm;

  const handleExportCsv = async () => {
    try {
      const params = new URLSearchParams();
      params.set("category", activeTab);
      if (entityName) params.set("entityName", entityName);
      if (action) params.set("action", action);
      if (userId) params.set("userId", userId);
      if (dateFrom) params.set("dateFrom", dateFrom);
      if (dateTo) params.set("dateTo", dateTo);
      if (searchTerm) params.set("searchTerm", searchTerm);

      const response = await fetch(`/api/auditlogs/export?${params.toString()}`, {
        credentials: "include",
      });
      if (!response.ok) throw new Error("Export failed");

      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `auditoria_${activeTab}_${new Date().toISOString().slice(0, 10)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      console.error("Error exporting CSV:", err);
    }
  };

  return (
    <div className="space-y-5">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white flex items-center gap-2">
            <Shield className="h-6 w-6 text-indigo-500" />
            Administración
          </h1>
          <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
            Control y trazabilidad de movimientos del sistema
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={() => setShowFilters(!showFilters)}
            className={`inline-flex items-center gap-2 px-3 py-2 text-sm font-medium rounded-lg border transition-colors
              ${hasActiveFilters
                ? "border-indigo-300 bg-indigo-50 text-indigo-700 dark:border-indigo-700 dark:bg-indigo-900/20 dark:text-indigo-300"
                : "border-slate-300 bg-white text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300"
              }`}
          >
            <Filter className="h-4 w-4" />
            Filtros
            {hasActiveFilters && <span className="bg-indigo-500 text-white text-[10px] font-bold px-1.5 py-0.5 rounded-full">!</span>}
          </button>
          <button
            onClick={handleExportCsv}
            className="inline-flex items-center gap-2 px-3 py-2 text-sm font-medium rounded-lg border border-slate-300 bg-white text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 transition-colors"
          >
            <Download className="h-4 w-4" />
            Exportar
          </button>
          <button
            onClick={fetchLogs}
            className="inline-flex items-center gap-2 p-2 text-sm rounded-lg border border-slate-300 bg-white text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 transition-colors"
            title="Refrescar"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? "animate-spin" : ""}`} />
          </button>
        </div>
      </div>

      {/* Tabs: Operativa / Sistema */}
      <div className="flex gap-1 p-1 bg-slate-100 dark:bg-slate-800/50 rounded-xl w-fit">
        {TABS.map((tab) => (
          <button
            key={tab.id}
            onClick={() => handleTabChange(tab.id)}
            className={`inline-flex items-center gap-2 px-4 py-2 text-sm font-medium rounded-lg transition-all
              ${activeTab === tab.id
                ? "bg-white dark:bg-slate-800 text-slate-900 dark:text-white shadow-sm"
                : "text-slate-500 dark:text-slate-400 hover:text-slate-700 dark:hover:text-slate-300"
              }`}
          >
            <tab.icon className="h-4 w-4" />
            {tab.label}
          </button>
        ))}
      </div>

      {/* Filtros */}
      {showFilters && (
        <div className="bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl p-4 space-y-4 shadow-sm">
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6 gap-3">
            <div className="xl:col-span-2">
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Buscar</label>
              <div className="relative">
                <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
                <input
                  type="text" value={searchTerm}
                  onChange={(e) => { setSearchTerm(e.target.value); setPage(1); }}
                  placeholder="Nombre, entidad, ID..."
                  className="w-full pl-9 pr-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500"
                />
              </div>
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Entidad</label>
              <select value={entityName} onChange={(e) => { setEntityName(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500">
                <option value="">Todas</option>
                {uiEntities.map((e) => <option key={e} value={e}>{translateEntity(e)}</option>)}
              </select>
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Acción</label>
              <select value={action} onChange={(e) => { setAction(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500">
                <option value="">Todas</option>
                <option value="Create">Creación</option>
                <option value="Update">Modificación</option>
                <option value="Delete">Eliminación</option>
                <option value="SoftDelete">Papelera</option>
                {activeTab === "system" && <>
                  <option value="Login">Inicio de sesión</option>
                  <option value="LoginFailed">Login fallido</option>
                  <option value="Logout">Cierre de sesión</option>
                  <option value="ChangePassword">Cambio de contraseña</option>
                </>}
              </select>
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Usuario</label>
              <select value={userId} onChange={(e) => { setUserId(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500">
                <option value="">Todos</option>
                {users.map((u) => <option key={u.id} value={u.id}>{u.fullName || u.userName}</option>)}
              </select>
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Desde</label>
              <input type="date" value={dateFrom} onChange={(e) => { setDateFrom(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500" />
            </div>
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Hasta</label>
              <input type="date" value={dateTo} onChange={(e) => { setDateTo(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500" />
            </div>
          </div>
          {hasActiveFilters && (
            <div className="flex justify-end">
              <button onClick={handleClearFilters} className="inline-flex items-center gap-1.5 text-xs font-medium text-slate-500 hover:text-red-500 transition-colors">
                <X className="h-3.5 w-3.5" /> Limpiar filtros
              </button>
            </div>
          )}
        </div>
      )}

      {/* Info */}
      <div className="flex items-center justify-between text-xs text-slate-500 dark:text-slate-400">
        <span>{totalCount.toLocaleString()} {totalCount === 1 ? "registro" : "registros"}{hasActiveFilters && " (filtrado)"}</span>
        {totalPages > 1 && <span>Página {page} de {totalPages}</span>}
      </div>

      {/* Lista */}
      <div className="bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl shadow-sm overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-16">
            <RefreshCw className="h-6 w-6 text-indigo-500 animate-spin" />
            <span className="ml-3 text-sm text-slate-500">Cargando historial...</span>
          </div>
        ) : logs.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 text-slate-400">
            <Shield className="h-10 w-10 mb-3 opacity-50" />
            <p className="text-sm font-medium">No hay registros</p>
            <p className="text-xs mt-1">
              {activeTab === "operational" ? "Los movimientos operativos aparecerán aquí" : "Los eventos del sistema aparecerán aquí"}
            </p>
          </div>
        ) : (
          <ul className="divide-y divide-slate-100 dark:divide-slate-800">
            {logs.map((log) => {
              const isExpanded = expandedId === log.id;
              const hasChanges = log.changes && log.changes !== "{}";
              return (
                <li
                  key={log.id}
                  className={`transition-colors ${hasChanges ? "cursor-pointer hover:bg-slate-50/50 dark:hover:bg-slate-800/30" : ""}`}
                  onClick={() => hasChanges && setExpandedId(isExpanded ? null : log.id)}
                >
                  <div className="px-4 py-3">
                    <div className="flex items-start gap-3">
                      <ActionIcon action={log.action} className="mt-0.5" />
                      <div className="flex-1 min-w-0">
                        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-1">
                          <NarrativeSummary log={log} />
                          <div className="flex items-center gap-2 flex-shrink-0">
                            <span className={`text-[10px] font-semibold px-2 py-0.5 rounded-full ${actionBadgeColors[log.action] || "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-400"}`}>
                              {translateAction(log.action)}
                            </span>
                            <span className="text-xs text-slate-400 dark:text-slate-500 whitespace-nowrap" title={formatTimestamp(log.timestamp)}>
                              {formatRelative(log.timestamp)}
                            </span>
                            {hasChanges && (isExpanded ? <ChevronUp className="h-4 w-4 text-slate-400" /> : <ChevronDown className="h-4 w-4 text-slate-400" />)}
                          </div>
                        </div>
                        <p className="text-[11px] text-slate-400 dark:text-slate-500 mt-0.5">
                          {translateEntity(log.entityName)}
                          {log.entityId && !["Login", "LoginFailed", "Logout", "ChangePassword"].includes(log.action) && ` #${log.entityId?.substring(0, 8)}`}
                        </p>
                      </div>
                    </div>

                    {isExpanded && hasChanges && (
                      <div className="mt-2 ml-10 p-3 bg-slate-50 dark:bg-slate-800/50 rounded-lg border border-slate-100 dark:border-slate-700/50">
                        <p className="text-[11px] font-medium text-slate-500 dark:text-slate-400 mb-2 uppercase tracking-wider">Cambios detallados</p>
                        <ChangeDetail changes={log.changes} />
                      </div>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </div>

      {/* Paginación */}
      {totalPages > 1 && (
        <div className="flex items-center justify-between">
          <p className="text-xs text-slate-500 dark:text-slate-400">
            Mostrando {((page - 1) * pageSize) + 1}-{Math.min(page * pageSize, totalCount)} de {totalCount.toLocaleString()}
          </p>
          <div className="flex items-center gap-1">
            <button onClick={() => setPage(p => Math.max(1, p - 1))} disabled={page <= 1}
              className="p-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-600 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800 disabled:opacity-40 disabled:cursor-not-allowed transition-colors">
              <ChevronLeft className="h-4 w-4" />
            </button>
            {(() => {
              const pages = [];
              const start = Math.max(1, page - 2);
              const end = Math.min(totalPages, page + 2);
              for (let i = start; i <= end; i++) {
                pages.push(
                  <button key={i} onClick={() => setPage(i)}
                    className={`min-w-[36px] h-9 rounded-lg text-sm font-medium transition-colors ${i === page ? "bg-indigo-600 text-white shadow-sm" : "text-slate-600 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800"}`}>
                    {i}
                  </button>
                );
              }
              return pages;
            })()}
            <button onClick={() => setPage(p => Math.min(totalPages, p + 1))} disabled={page >= totalPages}
              className="p-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-600 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800 disabled:opacity-40 disabled:cursor-not-allowed transition-colors">
              <ChevronRight className="h-4 w-4" />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}


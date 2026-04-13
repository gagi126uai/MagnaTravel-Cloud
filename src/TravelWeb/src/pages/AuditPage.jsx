import { useState, useEffect, useCallback, useMemo } from "react";
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
} from "lucide-react";

// ---- Traducciones ----
const entityTranslations = {
  Reserva: "Reserva",
  Customer: "Cliente",
  Supplier: "Proveedor",
  Payment: "Pago",
  Invoice: "Factura",
  Passenger: "Pasajero",
  ServicioReserva: "Servicio",
  FlightSegment: "Vuelo",
  HotelBooking: "Hotel",
  PackageBooking: "Paquete",
  TransferBooking: "Transfer",
  Lead: "Lead",
  Quote: "Cotización",
  QuoteItem: "Item Cotización",
  ReservaAttachment: "Adjunto",
  SupplierPayment: "Pago Proveedor",
  ManualCashMovement: "Mov. Caja",
  ApplicationUser: "Usuario",
  CommissionRule: "Regla Comisión",
  Rate: "Tarifa",
  AgencySettings: "Config. Agencia",
  OperationalFinanceSettings: "Config. Finanzas",
  AfipSettings: "Config. AFIP",
  WhatsAppBotConfig: "Config. WhatsApp",
  CatalogPackage: "Paquete Catálogo",
  Country: "País",
  Destination: "Destino",
};

const actionTranslations = {
  Create: "Creó",
  Update: "Modificó",
  Delete: "Eliminó",
  SoftDelete: "Envió a papelera",
};

const actionColors = {
  Create: "bg-emerald-500",
  Update: "bg-blue-500",
  Delete: "bg-red-500",
  SoftDelete: "bg-amber-500",
};

const actionBadgeColors = {
  Create: "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
  Update: "bg-blue-50 text-blue-700 dark:bg-blue-900/30 dark:text-blue-300",
  Delete: "bg-red-50 text-red-700 dark:bg-red-900/30 dark:text-red-300",
  SoftDelete: "bg-amber-50 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300",
};

const fieldTranslations = {
  Status: "Estado", Balance: "Saldo", TotalSale: "Venta Total", TotalCost: "Costo Total",
  TotalPaid: "Total Pagado", Name: "Nombre", Description: "Descripción",
  SalePrice: "Precio Venta", NetCost: "Costo Neto", Commission: "Comisión",
  FullName: "Nombre Completo", DocumentType: "Tipo Doc.", DocumentNumber: "Nro. Doc.",
  BirthDate: "Fecha Nacimiento", Nationality: "Nacionalidad", Phone: "Teléfono",
  Email: "Email", Gender: "Género", Notes: "Notas", StartDate: "Fecha Salida",
  EndDate: "Fecha Regreso", CreatedAt: "Fecha Creación", ClosedAt: "Fecha Cierre",
  NumeroReserva: "Nro. Reserva", ServiceType: "Servicio", ProductType: "Producto",
  ConfirmationNumber: "Nro. Confirmación", DepartureDate: "Salida", ReturnDate: "Regreso",
  Method: "Método Pago", Amount: "Importe", PaidAt: "Fecha Pago", Reference: "Referencia",
  HotelName: "Hotel", CheckIn: "Check-In", CheckOut: "Check-Out", RoomType: "Habitación",
  MealPlan: "Régimen", Nights: "Noches", City: "Ciudad", IsDeleted: "Eliminado",
  DeletedAt: "Fecha Eliminación", AffectsCash: "Afecta Caja",
  Title: "Título", Slug: "Slug", Tagline: "Subtítulo", IsPublished: "Publicado",
};

function translateEntity(name) {
  return entityTranslations[name] || name;
}

function translateAction(action) {
  return actionTranslations[action] || action;
}

function translateField(field) {
  return fieldTranslations[field] || field;
}

function formatTimestamp(ts) {
  const date = new Date(ts);
  if (isToday(date)) {
    return `Hoy ${format(date, "HH:mm", { locale: es })}`;
  }
  if (isYesterday(date)) {
    return `Ayer ${format(date, "HH:mm", { locale: es })}`;
  }
  return format(date, "d MMM yyyy HH:mm", { locale: es });
}

function formatRelative(ts) {
  return formatDistanceToNow(new Date(ts), { addSuffix: true, locale: es });
}

// ---- Componentes ----

function ActionIcon({ action, className = "" }) {
  const base = `flex-shrink-0 h-7 w-7 rounded-full flex items-center justify-center ${actionColors[action] || "bg-gray-500"} ${className}`;
  const iconClass = "h-3.5 w-3.5 text-white";

  switch (action) {
    case "Create": return <span className={base}><Plus className={iconClass} /></span>;
    case "Update": return <span className={base}><Pencil className={iconClass} /></span>;
    case "Delete":
    case "SoftDelete": return <span className={base}><Trash2 className={iconClass} /></span>;
    default: return <span className={base}><Pencil className={iconClass} /></span>;
  }
}

function NarrativeSummary({ log }) {
  let changes = {};
  try { changes = JSON.parse(log.changes || "{}"); } catch {}

  const changedFields = Object.keys(changes);
  const fieldCount = changedFields.length;

  // Generar resumen narrativo
  let summary = "";
  if (log.action === "Create") {
    summary = `creó un/a ${translateEntity(log.entityName)}`;
  } else if (log.action === "Delete") {
    summary = `eliminó un/a ${translateEntity(log.entityName)}`;
  } else if (log.action === "SoftDelete") {
    summary = `envió a papelera un/a ${translateEntity(log.entityName)}`;
  } else if (log.action === "Update") {
    if (fieldCount === 0) {
      summary = `modificó un/a ${translateEntity(log.entityName)}`;
    } else if (fieldCount <= 3) {
      const translatedFields = changedFields.map(f => translateField(f)).join(", ");
      summary = `modificó ${translatedFields} de ${translateEntity(log.entityName)}`;
    } else {
      const firstTwo = changedFields.slice(0, 2).map(f => translateField(f)).join(", ");
      summary = `modificó ${firstTwo} y ${fieldCount - 2} más de ${translateEntity(log.entityName)}`;
    }
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
    <div className="mt-3 space-y-1.5 pl-9">
      {entries.map(([key, value]) => (
        <div key={key} className="flex flex-wrap items-center gap-1.5 text-xs">
          <span className="font-medium text-slate-500 dark:text-slate-400 min-w-[100px]">
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
  if (val === null || val === undefined) return "—";
  if (typeof val === "boolean") return val ? "Sí" : "No";
  const str = String(val);
  if (str.length > 80) return str.substring(0, 80) + "…";
  return str;
}

// ---- Página Principal ----
export default function AuditPage() {
  const [logs, setLogs] = useState([]);
  const [loading, setLoading] = useState(true);
  const [totalCount, setTotalCount] = useState(0);
  const [totalPages, setTotalPages] = useState(0);
  const [page, setPage] = useState(1);
  const [pageSize] = useState(25);

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

  // Cargar dropdowns una vez
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
  }, [page, pageSize, entityName, action, userId, dateFrom, dateTo, searchTerm]);

  useEffect(() => {
    fetchLogs();
  }, [fetchLogs]);

  const handleClearFilters = () => {
    setEntityName("");
    setAction("");
    setUserId("");
    setDateFrom("");
    setDateTo("");
    setSearchTerm("");
    setPage(1);
  };

  const hasActiveFilters = entityName || action || userId || dateFrom || dateTo || searchTerm;

  const handleExportCsv = async () => {
    try {
      const params = new URLSearchParams();
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
      a.download = `auditoria_${new Date().toISOString().slice(0, 10)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      console.error("Error exporting CSV:", err);
    }
  };

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-slate-900 dark:text-white flex items-center gap-2">
            <Shield className="h-6 w-6 text-indigo-500" />
            Auditoría
          </h1>
          <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
            Historial completo de movimientos del sistema
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
            {hasActiveFilters && (
              <span className="bg-indigo-500 text-white text-[10px] font-bold px-1.5 py-0.5 rounded-full">!</span>
            )}
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
            className="inline-flex items-center gap-2 px-3 py-2 text-sm font-medium rounded-lg border border-slate-300 bg-white text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 transition-colors"
            title="Refrescar"
          >
            <RefreshCw className={`h-4 w-4 ${loading ? "animate-spin" : ""}`} />
          </button>
        </div>
      </div>

      {/* Filtros */}
      {showFilters && (
        <div className="bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl p-4 space-y-4 shadow-sm">
          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-6 gap-3">
            {/* Búsqueda libre */}
            <div className="xl:col-span-2">
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Buscar</label>
              <div className="relative">
                <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
                <input
                  type="text"
                  value={searchTerm}
                  onChange={(e) => { setSearchTerm(e.target.value); setPage(1); }}
                  placeholder="Nombre, entidad, ID..."
                  className="w-full pl-9 pr-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500"
                />
              </div>
            </div>

            {/* Entidad */}
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Entidad</label>
              <select
                value={entityName}
                onChange={(e) => { setEntityName(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500"
              >
                <option value="">Todas</option>
                {entities.map((e) => (
                  <option key={e} value={e}>{translateEntity(e)}</option>
                ))}
              </select>
            </div>

            {/* Acción */}
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Acción</label>
              <select
                value={action}
                onChange={(e) => { setAction(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500"
              >
                <option value="">Todas</option>
                <option value="Create">Creación</option>
                <option value="Update">Modificación</option>
                <option value="Delete">Eliminación</option>
                <option value="SoftDelete">Papelera</option>
              </select>
            </div>

            {/* Usuario */}
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Usuario</label>
              <select
                value={userId}
                onChange={(e) => { setUserId(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500"
              >
                <option value="">Todos</option>
                {users.map((u) => (
                  <option key={u.id} value={u.id}>{u.fullName || u.userName}</option>
                ))}
              </select>
            </div>

            {/* Fecha desde */}
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Desde</label>
              <input
                type="date"
                value={dateFrom}
                onChange={(e) => { setDateFrom(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500"
              />
            </div>

            {/* Fecha hasta */}
            <div>
              <label className="block text-xs font-medium text-slate-500 dark:text-slate-400 mb-1">Hasta</label>
              <input
                type="date"
                value={dateTo}
                onChange={(e) => { setDateTo(e.target.value); setPage(1); }}
                className="w-full px-3 py-2 text-sm border border-slate-300 rounded-lg bg-white dark:bg-slate-800 dark:border-slate-700 dark:text-white focus:ring-2 focus:ring-indigo-500"
              />
            </div>
          </div>

          {hasActiveFilters && (
            <div className="flex justify-end">
              <button
                onClick={handleClearFilters}
                className="inline-flex items-center gap-1.5 text-xs font-medium text-slate-500 hover:text-red-500 transition-colors"
              >
                <X className="h-3.5 w-3.5" />
                Limpiar filtros
              </button>
            </div>
          )}
        </div>
      )}

      {/* Resultado info */}
      <div className="flex items-center justify-between text-xs text-slate-500 dark:text-slate-400">
        <span>
          {totalCount.toLocaleString()} {totalCount === 1 ? "registro" : "registros"}
          {hasActiveFilters && " (filtrado)"}
        </span>
        {totalPages > 1 && (
          <span>Página {page} de {totalPages}</span>
        )}
      </div>

      {/* Lista de logs */}
      <div className="bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded-xl shadow-sm overflow-hidden">
        {loading ? (
          <div className="flex items-center justify-center py-16">
            <RefreshCw className="h-6 w-6 text-indigo-500 animate-spin" />
            <span className="ml-3 text-sm text-slate-500">Cargando historial...</span>
          </div>
        ) : logs.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-16 text-slate-400">
            <Shield className="h-10 w-10 mb-3 opacity-50" />
            <p className="text-sm font-medium">No hay registros de auditoría</p>
            <p className="text-xs mt-1">Los movimientos del sistema aparecerán aquí</p>
          </div>
        ) : (
          <ul className="divide-y divide-slate-100 dark:divide-slate-800">
            {logs.map((log) => {
              const isExpanded = expandedId === log.id;
              return (
                <li
                  key={log.id}
                  className="hover:bg-slate-50/50 dark:hover:bg-slate-800/30 transition-colors cursor-pointer"
                  onClick={() => setExpandedId(isExpanded ? null : log.id)}
                >
                  <div className="px-4 py-3">
                    <div className="flex items-start gap-3">
                      <ActionIcon action={log.action} className="mt-0.5" />
                      <div className="flex-1 min-w-0">
                        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-1">
                          <NarrativeSummary log={log} />
                          <div className="flex items-center gap-2 flex-shrink-0">
                            <span className={`text-[10px] font-semibold px-2 py-0.5 rounded-full ${actionBadgeColors[log.action] || "bg-slate-100 text-slate-600"}`}>
                              {translateAction(log.action)}
                            </span>
                            <span className="text-xs text-slate-400 dark:text-slate-500 whitespace-nowrap" title={formatTimestamp(log.timestamp)}>
                              {formatRelative(log.timestamp)}
                            </span>
                            {log.changes && log.changes !== "{}" && (
                              isExpanded ? (
                                <ChevronUp className="h-4 w-4 text-slate-400" />
                              ) : (
                                <ChevronDown className="h-4 w-4 text-slate-400" />
                              )
                            )}
                          </div>
                        </div>

                        {/* ID de entidad (subtle) */}
                        <p className="text-[11px] text-slate-400 dark:text-slate-500 mt-0.5">
                          {translateEntity(log.entityName)} #{log.entityId?.substring(0, 8)}
                        </p>
                      </div>
                    </div>

                    {/* Detalle expandido */}
                    {isExpanded && log.changes && (
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
            <button
              onClick={() => setPage(p => Math.max(1, p - 1))}
              disabled={page <= 1}
              className="p-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-600 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
            >
              <ChevronLeft className="h-4 w-4" />
            </button>

            {/* Page numbers */}
            {(() => {
              const pages = [];
              const start = Math.max(1, page - 2);
              const end = Math.min(totalPages, page + 2);
              for (let i = start; i <= end; i++) {
                pages.push(
                  <button
                    key={i}
                    onClick={() => setPage(i)}
                    className={`min-w-[36px] h-9 rounded-lg text-sm font-medium transition-colors 
                      ${i === page
                        ? "bg-indigo-600 text-white shadow-sm"
                        : "text-slate-600 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800"
                      }`}
                  >
                    {i}
                  </button>
                );
              }
              return pages;
            })()}

            <button
              onClick={() => setPage(p => Math.min(totalPages, p + 1))}
              disabled={page >= totalPages}
              className="p-2 rounded-lg border border-slate-300 dark:border-slate-700 text-slate-600 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
            >
              <ChevronRight className="h-4 w-4" />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

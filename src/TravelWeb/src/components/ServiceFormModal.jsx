import { useState, useEffect, useCallback, useMemo } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { hasPermission } from "../auth";
import {
    X,
    Plane,
    Hotel,
    Bus,
    Package,
    ShieldCheck,
    Search,
    Calculator,
    DollarSign,
    AlertCircle,
    RefreshCw,
    Building2,
    MapPin,
    BedDouble,
    CalendarDays,
    Users,
    CheckCircle2,
    Star,
    ChevronDown,
    ChevronUp,
} from "lucide-react";
import {
    SERVICE_RECORD_KIND,
    findNormalizedService,
    getRecordKindForServiceType,
    getServiceCreateEndpoint,
    getServiceMutationEndpoint
} from "../features/reservas/lib/reservationServiceModel";
import RoomingPlanner from "./RoomingPlanner";

const SERVICE_TYPES = [
    { value: "Aereo", label: "Aereo", icon: Plane, color: "sky" },
    { value: "Hotel", label: "Hotel", icon: Hotel, color: "amber" },
    { value: "Traslado", label: "Traslado", icon: Bus, color: "emerald" },
    { value: "Paquete", label: "Paquete", icon: Package, color: "violet" },
    // Asistencia al viajero / seguro de viaje
    { value: "Asistencia", label: "Asistencia", icon: ShieldCheck, color: "blue" },
];

const inputClass = "w-full rounded-xl border border-slate-200 bg-slate-50 p-2.5 text-sm text-slate-900 focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-600 dark:bg-slate-700 dark:text-white dark:focus:border-indigo-400";
const labelClass = "mb-1 block text-sm font-medium text-slate-700 dark:text-slate-300";
const panelClass = "rounded-2xl border border-slate-200 bg-white p-4 shadow-sm dark:border-slate-700 dark:bg-slate-900/50";

const calculateNights = (checkIn, checkOut) => {
    if (!checkIn || !checkOut) return 0;
    const start = new Date(checkIn);
    const end = new Date(checkOut);
    const diff = Math.ceil((end - start) / (1000 * 60 * 60 * 24));
    return diff > 0 ? diff : 0;
};

const formatDateForInput = (value) => {
    if (!value) return "";
    return value.split?.("T")?.[0] || "";
};

const formatMoney = (value) => {
    const numericValue = Number(value) || 0;
    return `$${numericValue.toLocaleString("es-AR", {
        minimumFractionDigits: 2,
        maximumFractionDigits: 2,
    })}`;
};

const roundMoney = (value) => Math.round((Number(value) || 0) * 100) / 100;

const getHotelQuantity = (form) => {
    const nights = calculateNights(form.checkIn, form.checkOut);
    const rooms = Math.max(Number(form.rooms) || 1, 1);
    return Math.max(nights || 1, 1) * rooms;
};

const formatShortDate = (value) => {
    if (!value) return "Sin definir";
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return "Sin definir";
    return date.toLocaleDateString("es-AR");
};

const toIsoDate = (value, fieldLabel) => {
    if (!value) {
        throw new Error(`Completa ${fieldLabel}.`);
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
        throw new Error(`La fecha de ${fieldLabel} no es valida.`);
    }

    return date.toISOString();
};

const toOptionalIsoDate = (value) => {
    if (!value) return null;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
};

const buildGenericServicePayload = (form, serviceToEdit) => ({
    serviceType: form.serviceType || serviceToEdit?.serviceType || serviceToEdit?.displayType || "Generico",
    supplierId: form.supplierId || null,
    description: form.description || form.name || form.serviceType || "Servicio",
    confirmationNumber: form.confirmationNumber || null,
    departureDate: toIsoDate(form.departureDate, "salida"),
    returnDate: toOptionalIsoDate(form.returnDate),
    salePrice: Number(form.salePrice) || 0,
    netCost: Number(form.netCost) || 0,
    rateId: form.rateId || form.ratePublicId || null,
});

const unwrapSavedService = (savedService) => savedService?.servicio || savedService?.service || savedService;

function RateSelector({ serviceType, supplierId, onSelect, disabled }) {
    const [rates, setRates] = useState([]);
    const [search, setSearch] = useState("");
    const [loading, setLoading] = useState(false);
    const [showDropdown, setShowDropdown] = useState(false);

    const searchRates = useCallback(async () => {
        if (!supplierId) {
            setRates([]);
            return;
        }

        setLoading(true);
        try {
            const params = new URLSearchParams();
            params.append("serviceType", serviceType);
            params.append("supplierId", supplierId);
            if (search) params.append("query", search);
            const data = await api.get(`/rates/search?${params}`);
            setRates(data || []);
        } catch {
            setRates([]);
        } finally {
            setLoading(false);
        }
    }, [search, serviceType, supplierId]);

    useEffect(() => {
        if (showDropdown && supplierId) searchRates();
    }, [showDropdown, supplierId, searchRates]);

    return (
        <div className="relative">
            <label className={labelClass}>{serviceType === "Hotel" ? "Seleccionar Hotel" : "Seleccionar tarifa"}</label>
            <div className="relative">
                <Search className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                <input
                    type="text"
                    className={`${inputClass} border-indigo-200 bg-indigo-50/20 pl-10 dark:border-indigo-900/50`}
                    placeholder={`Buscar en tarifario de ${serviceType}...`}
                    value={search}
                    onChange={(event) => {
                        setSearch(event.target.value);
                        setShowDropdown(true);
                    }}
                    onFocus={() => setShowDropdown(true)}
                    disabled={disabled || !supplierId}
                />
                {loading ? (
                    <div className="absolute right-3 top-2.5">
                        <RefreshCw className="h-4 w-4 animate-spin text-indigo-500" />
                    </div>
                ) : null}
            </div>

            {showDropdown && supplierId ? (
                <div className="absolute z-30 mt-1 max-h-68 w-full overflow-y-auto overflow-x-hidden rounded-xl border border-slate-200 bg-white shadow-lg dark:border-slate-600 dark:bg-slate-800">
                    {rates.length === 0 ? (
                        <div className="p-4 text-center text-sm text-slate-500">
                            {loading ? "Buscando tarifas..." : "No se encontraron tarifas para este proveedor."}
                        </div>
                    ) : (
                        rates.map((rate) => {
                            const isExpired = rate.validTo && new Date(rate.validTo) < new Date();
                            return (
                                <button
                                    key={rate.publicId}
                                    type="button"
                                    onClick={() => {
                                        onSelect(rate);
                                        setSearch(rate.productName || rate.hotelName || "");
                                        setShowDropdown(false);
                                    }}
                                    className={`w-full border-b border-slate-100 px-4 py-3 text-left transition-colors dark:border-slate-700 last:border-0 ${
                                        isExpired
                                            ? "cursor-not-allowed bg-slate-50 opacity-60 dark:bg-slate-900/50"
                                            : "cursor-pointer hover:bg-indigo-50 dark:hover:bg-indigo-900/30"
                                    }`}
                                    disabled={isExpired}
                                >
                                    <div className="mb-1 flex items-start justify-between">
                                        <div className="truncate font-bold text-slate-900 dark:text-white">
                                            {rate.serviceType === "Hotel" ? rate.hotelName : rate.productName}
                                        </div>
                                        {isExpired ? (
                                            <span className="ml-2 rounded bg-amber-100 px-1.5 py-0.5 text-[10px] font-bold text-amber-700">
                                                VENCIDO
                                            </span>
                                        ) : null}
                                    </div>
                                    <div className="flex flex-wrap gap-x-3 gap-y-1 text-[11px] text-slate-500">
                                        {rate.city ? <span>{rate.city}</span> : null}
                                        {rate.roomType ? <span>{rate.roomType}</span> : null}
                                        {rate.airline ? <span>{rate.airline}</span> : null}
                                        <div className="flex items-center gap-2 font-mono">
                                            <span className="font-bold text-emerald-600">NET: {formatMoney(rate.netCost)}</span>
                                            <span className="font-bold text-indigo-600">VTA: {formatMoney(rate.salePrice)}</span>
                                        </div>
                                    </div>
                                </button>
                            );
                        })
                    )}
                </div>
            ) : null}
        </div>
    );
}

/**
 * Formulario de servicio Aereo.
 * Carga todos los datos operativos del vuelo: rutas, fechas+hora, PNR, tickets, equipaje.
 * La comision NO se muestra — va oculta en el payload (regla de negocio del dueño).
 */
function FlightForm({ form, setForm, suppliers, onRateSelect, disabled, isBudget }) {
    return (
        <div className="space-y-4">
            {/* Proveedor y estado */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId || ""} onChange={(event) => setForm({ ...form, supplierId: event.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map((supplier) => (
                            <option key={supplier.id || supplier.publicId || supplier.PublicId} value={supplier.publicId || supplier.PublicId}>
                                {supplier.name} {!supplier.isActive ? "(Inactivo)" : ""}
                            </option>
                        ))}
                    </select>
                </div>
                {!isBudget && (
                    <div>
                        <label className={labelClass}>Estado *</label>
                        <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={(event) => setForm({ ...form, workflowStatus: event.target.value })} required disabled={disabled}>
                            <option value="Solicitado">Solicitado</option>
                            <option value="Confirmado">Confirmado</option>
                            <option value="Cancelado">Cancelado</option>
                        </select>
                    </div>
                )}
            </div>

            <RateSelector serviceType={form.serviceType || "Aereo"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />

            {/* Aerolinea: codigo IATA (ej. "AR") y nombre (ej. "Aerolineas Argentinas") */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Codigo Aerolinea</label>
                    <input
                        className={inputClass}
                        placeholder="AR, LA, AA..."
                        value={form.airlineCode || ""}
                        onChange={(event) => setForm({ ...form, airlineCode: event.target.value.toUpperCase() })}
                        disabled={disabled}
                        data-testid="flight-airline-code"
                    />
                </div>
                <div>
                    <label className={labelClass}>Nombre Aerolinea</label>
                    <input
                        className={inputClass}
                        placeholder="Aerolineas Argentinas..."
                        value={form.airlineName || ""}
                        onChange={(event) => setForm({ ...form, airlineName: event.target.value })}
                        disabled={disabled}
                        data-testid="flight-airline-name"
                    />
                </div>
            </div>

            {/* Numero de vuelo y clase de cabina */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Numero de Vuelo</label>
                    <input
                        className={inputClass}
                        placeholder="AR1234"
                        value={form.flightNumber || ""}
                        onChange={(event) => setForm({ ...form, flightNumber: event.target.value.toUpperCase() })}
                        disabled={disabled}
                        data-testid="flight-number"
                    />
                </div>
                <div>
                    <label className={labelClass}>Clase de Cabina</label>
                    {/* Economy es la clase mas comun; Business y First para vuelos premium */}
                    <select
                        className={inputClass}
                        value={form.cabinClass || ""}
                        onChange={(event) => setForm({ ...form, cabinClass: event.target.value })}
                        disabled={disabled}
                        data-testid="flight-cabin-class"
                    >
                        <option value="">Sin especificar</option>
                        <option value="Economy">Economy</option>
                        <option value="Premium">Premium Economy</option>
                        <option value="Business">Business</option>
                        <option value="First">Primera Clase</option>
                    </select>
                </div>
            </div>

            {/* Rutas: aeropuerto IATA (3 letras) + ciudad de cada tramo */}
            <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                <div>
                    <label className={labelClass}>Origen (IATA)</label>
                    <input className={inputClass} placeholder="BUE" value={form.origin || ""} onChange={(event) => setForm({ ...form, origin: event.target.value.toUpperCase() })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Ciudad Origen</label>
                    <input
                        className={inputClass}
                        placeholder="Buenos Aires"
                        value={form.originCity || ""}
                        onChange={(event) => setForm({ ...form, originCity: event.target.value })}
                        disabled={disabled}
                        data-testid="flight-origin-city"
                    />
                </div>
                <div>
                    <label className={labelClass}>Destino (IATA)</label>
                    <input className={inputClass} placeholder="MIA" value={form.destination || ""} onChange={(event) => setForm({ ...form, destination: event.target.value.toUpperCase() })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Ciudad Destino</label>
                    <input
                        className={inputClass}
                        placeholder="Miami"
                        value={form.destinationCity || ""}
                        onChange={(event) => setForm({ ...form, destinationCity: event.target.value })}
                        disabled={disabled}
                        data-testid="flight-destination-city"
                    />
                </div>
            </div>

            {/* Fechas y horas de salida/llegada */}
            <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                <div>
                    <label className={labelClass}>Fecha Salida</label>
                    <input type="date" className={inputClass} value={form.departureDate || ""} onChange={(event) => setForm({ ...form, departureDate: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Hora Salida</label>
                    <input
                        type="time"
                        className={inputClass}
                        value={form.departureTime || ""}
                        onChange={(event) => setForm({ ...form, departureTime: event.target.value })}
                        disabled={disabled}
                        data-testid="flight-departure-time"
                    />
                </div>
                <div>
                    <label className={labelClass}>Fecha Llegada</label>
                    <input type="date" className={inputClass} value={form.arrivalDate || ""} onChange={(event) => setForm({ ...form, arrivalDate: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Hora Llegada</label>
                    <input
                        type="time"
                        className={inputClass}
                        value={form.arrivalTime || ""}
                        onChange={(event) => setForm({ ...form, arrivalTime: event.target.value })}
                        disabled={disabled}
                        data-testid="flight-arrival-time"
                    />
                </div>
            </div>

            {/* Datos de reserva aerea: PNR, confirmacion, ticket */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
                <div>
                    <label className={labelClass}>PNR</label>
                    <input
                        className={inputClass}
                        placeholder="ABC123"
                        value={form.pnr || ""}
                        onChange={(event) => setForm({ ...form, pnr: event.target.value.toUpperCase() })}
                        disabled={disabled}
                        data-testid="flight-pnr"
                    />
                </div>
                <div>
                    <label className={labelClass}>Numero Confirmacion</label>
                    <input
                        className={inputClass}
                        placeholder="CF-00001"
                        value={form.confirmationNumber || ""}
                        onChange={(event) => setForm({ ...form, confirmationNumber: event.target.value })}
                        disabled={disabled}
                        data-testid="flight-confirmation"
                    />
                </div>
                <div>
                    <label className={labelClass}>Numero Ticket</label>
                    <input
                        className={inputClass}
                        placeholder="TK-00001"
                        value={form.ticketNumber || ""}
                        onChange={(event) => setForm({ ...form, ticketNumber: event.target.value })}
                        disabled={disabled}
                        data-testid="flight-ticket-number"
                    />
                </div>
            </div>

            {/* Equipaje y cantidad de pasajeros */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Equipaje Incluido</label>
                    <input
                        className={inputClass}
                        placeholder="1 maleta 23kg + carry-on"
                        value={form.baggage || ""}
                        onChange={(event) => setForm({ ...form, baggage: event.target.value })}
                        disabled={disabled}
                        data-testid="flight-baggage"
                    />
                </div>
                <div>
                    <label className={labelClass}>Cantidad Pasajeros</label>
                    <input
                        type="number"
                        min="1"
                        className={inputClass}
                        value={form.passengerCount || ""}
                        onChange={(event) => setForm({ ...form, passengerCount: parseInt(event.target.value, 10) || null })}
                        disabled={disabled}
                        data-testid="flight-passenger-count"
                    />
                </div>
            </div>

            {/* Notas internas del operador sobre el vuelo */}
            <div>
                <label className={labelClass}>Notas</label>
                <textarea
                    className={`${inputClass} resize-none`}
                    rows={2}
                    placeholder="Observaciones del vuelo..."
                    value={form.notes || ""}
                    onChange={(event) => setForm({ ...form, notes: event.target.value })}
                    disabled={disabled}
                    data-testid="flight-notes"
                />
            </div>
        </div>
    );
}

function HotelForm({ form, setForm, suppliers, onRateSelect, disabled, reservaPax, isBudget }) {
    const [searchQuery, setSearchQuery] = useState(form.hotelName || "");
    const [hotelGroups, setHotelGroups] = useState([]);
    const [loading, setLoading] = useState(false);
    const [showResults, setShowResults] = useState(false);
    const [expandedHotel, setExpandedHotel] = useState(null);
    const nights = calculateNights(form.checkIn, form.checkOut);
    const days = nights > 0 ? nights + 1 : 0;

    const searchHotels = useCallback(async (query) => {
        if (!query || query.length < 3) {
            setHotelGroups([]);
            return;
        }
        setLoading(true);
        try {
            const params = new URLSearchParams({
                serviceType: "Hotel",
                query: query,
                supplierId: form.supplierId || "",
            });
            const data = await api.get(`/rates/search?${params}`);
            
            // Agrupar por hotel + ciudad
            const groups = {};
            data.forEach((rate) => {
                const key = `${rate.hotelName || rate.productName}|${rate.city || ""}`;
                if (!groups[key]) {
                    groups[key] = {
                        key,
                        hotelName: rate.hotelName || rate.productName,
                        city: rate.city,
                        starRating: rate.starRating,
                        supplierName: rate.supplierName,
                        rates: [],
                    };
                }
                groups[key].rates.push(rate);
            });
            setHotelGroups(Object.values(groups));
            setShowResults(true);
        } catch (error) {
            console.error("Search error:", error);
            setHotelGroups([]);
        } finally {
            setLoading(false);
        }
    }, [form.supplierId]);

    useEffect(() => {
        const timer = setTimeout(() => {
            if (searchQuery && searchQuery !== form.hotelName) searchHotels(searchQuery);
        }, 500);
        return () => clearTimeout(timer);
    }, [searchQuery, searchHotels, form.hotelName]);

    const handleSelectRate = (rate) => {
        onRateSelect(rate);
        setSearchQuery(rate.hotelName || rate.productName);
        setShowResults(false);
        setExpandedHotel(null);
    };

    return (
        <div className="space-y-4">
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Proveedor (Opcional para busqueda)</label>
                    <select 
                        className={inputClass} 
                        value={form.supplierId || ""} 
                        onChange={(event) => setForm({ ...form, supplierId: event.target.value })} 
                        disabled={disabled}
                    >
                        <option value="">Cualquier proveedor...</option>
                        {suppliers.map((supplier) => (
                            <option key={supplier.publicId || supplier.PublicId} value={supplier.publicId || supplier.PublicId}>
                                {supplier.name}
                            </option>
                        ))}
                    </select>
                </div>
                {!isBudget && (
                    <div>
                        <label className={labelClass}>Estado de Reserva</label>
                        <select
                            className={inputClass}
                            value={form.workflowStatus || "Solicitado"}
                            onChange={(event) => setForm({ ...form, workflowStatus: event.target.value })}
                            required
                            disabled={disabled}
                        >
                            <option value="Solicitado">Solicitado</option>
                            <option value="Confirmado">Confirmado</option>
                            <option value="Cancelado">Cancelado</option>
                        </select>
                    </div>
                )}
            </div>

            <div className="relative">
                <label className={labelClass}>Buscar Hotel o Ciudad</label>
                <div className="relative">
                    <Search className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                    <input
                        type="text"
                        className={`${inputClass} pl-10 border-indigo-200 bg-indigo-50/20 focus:bg-white`}
                        placeholder="Escribe el nombre del hotel o destino..."
                        value={searchQuery}
                        onChange={(e) => setSearchQuery(e.target.value)}
                        onFocus={() => searchQuery.length >= 3 && setShowResults(true)}
                        disabled={disabled}
                    />
                    {loading && <RefreshCw className="absolute right-3 top-2.5 h-4 w-4 animate-spin text-indigo-500" />}
                </div>

                {showResults && hotelGroups.length > 0 && (
                    <div className="absolute z-50 mt-1 max-h-[300px] w-full overflow-y-auto rounded-2xl border border-slate-200 bg-white p-2 shadow-2xl dark:border-slate-700 dark:bg-slate-800">
                        <div className="mb-2 px-2 py-1 text-[10px] font-black uppercase tracking-widest text-slate-400">Resultados encontrados</div>
                        {hotelGroups.map((group) => (
                            <div key={group.key} className="mb-2 overflow-hidden rounded-xl border border-slate-100 dark:border-slate-700">
                                <button
                                    type="button"
                                    onClick={() => setExpandedHotel(expandedHotel === group.key ? null : group.key)}
                                    className="flex w-full items-center justify-between bg-slate-50/50 p-3 text-left hover:bg-indigo-50/50 dark:bg-slate-900/30 dark:hover:bg-indigo-900/20"
                                >
                                    <div className="flex items-center gap-3">
                                        <div className="rounded-lg bg-white p-2 shadow-sm dark:bg-slate-800">
                                            <Building2 className="h-4 w-4 text-indigo-500" />
                                        </div>
                                        <div>
                                            <div className="text-sm font-bold text-slate-900 dark:text-white">{group.hotelName}</div>
                                            <div className="flex items-center gap-2 text-[11px] text-slate-500">
                                                <MapPin className="h-3 w-3" />
                                                {group.city}
                                                {group.starRating && (
                                                    <span className="flex items-center gap-0.5 text-amber-500">
                                                        <Star className="h-3 w-3 fill-current" />
                                                        {group.starRating}
                                                    </span>
                                                )}
                                            </div>
                                        </div>
                                    </div>
                                    {expandedHotel === group.key ? <ChevronUp className="h-4 w-4 text-slate-400" /> : <ChevronDown className="h-4 w-4 text-slate-400" />}
                                </button>
                                
                                {expandedHotel === group.key && (
                                    <div className="divide-y divide-slate-100 bg-white dark:divide-slate-700 dark:bg-slate-800">
                                        {group.rates.map((rate) => (
                                            <button
                                                key={rate.publicId}
                                                type="button"
                                                onClick={() => handleSelectRate(rate)}
                                                className="flex w-full items-center justify-between p-3 text-left transition-colors hover:bg-emerald-50/50 dark:hover:bg-emerald-900/10"
                                            >
                                                <div className="flex-1">
                                                    <div className="text-xs font-semibold text-slate-700 dark:text-slate-200">{rate.roomType || "Habitacion Estandar"}</div>
                                                    <div className="text-[10px] text-slate-500">{rate.mealPlan || "Solo Alojamiento"} • {rate.supplierName}</div>
                                                </div>
                                                <div className="text-right">
                                                    <div className="text-xs font-bold text-emerald-600">{formatMoney(rate.salePrice)}</div>
                                                    <div className="text-[9px] text-slate-400">por noche/unidad</div>
                                                </div>
                                            </button>
                                        ))}
                                    </div>
                                )}
                            </div>
                        ))}
                    </div>
                )}
            </div>

            {form.rateId && (
                <div className="rounded-2xl border border-indigo-100 bg-indigo-50/30 p-4 dark:border-indigo-900/30 dark:bg-indigo-950/10">
                    <div className="flex items-start justify-between">
                        <div className="flex items-center gap-3">
                            <div className="rounded-xl bg-indigo-500 p-2.5 text-white shadow-lg shadow-indigo-200 dark:shadow-none">
                                <CheckCircle2 className="h-5 w-5" />
                            </div>
                            <div>
                                <h4 className="text-sm font-bold text-slate-900 dark:text-white">{form.hotelName}</h4>
                                <p className="text-xs text-slate-500">
                                    {form.roomType} • {form.mealPlan} • {calculateNights(form.checkIn, form.checkOut)} noches
                                </p>
                            </div>
                        </div>
                        <div className="text-right">
                            <div className="text-sm font-black text-indigo-600 dark:text-indigo-400">{formatMoney(form.unitSalePrice)}</div>
                            <div className="text-[10px] uppercase tracking-tighter text-slate-400">Precio Ref.</div>
                        </div>
                    </div>
                </div>
            )}

            <div className="grid grid-cols-2 gap-4">
                <div className="grid grid-cols-2 gap-2">
                    <div>
                        <label className={labelClass}>Check-In</label>
                        <div className="relative">
                            <CalendarDays className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                            <input type="date" className={`${inputClass} pl-10`} value={form.checkIn || ""} onChange={(e) => setForm({ ...form, checkIn: e.target.value })} disabled={disabled} />
                        </div>
                    </div>
                    <div>
                        <label className={labelClass}>Check-Out</label>
                        <div className="relative">
                            <CalendarDays className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                            <input type="date" className={`${inputClass} pl-10`} value={form.checkOut || ""} onChange={(e) => setForm({ ...form, checkOut: e.target.value })} disabled={disabled} />
                        </div>
                    </div>
                </div>
                <div className="grid grid-cols-3 gap-2">
                    <div>
                        <label className={labelClass}>Hab.</label>
                        <input type="number" className={inputClass} value={form.rooms || 1} onChange={(e) => setForm({ ...form, rooms: parseInt(e.target.value, 10) || 1 })} disabled={disabled} />
                    </div>
                    <div>
                        <label className={labelClass}>Adt.</label>
                        <input type="number" className={inputClass} value={form.adults || 2} onChange={(e) => setForm({ ...form, adults: parseInt(e.target.value, 10) || 1 })} disabled={disabled} />
                    </div>
                    <div>
                        <label className={labelClass}>Chd.</label>
                        <input type="number" className={inputClass} value={form.children || 0} onChange={(e) => setForm({ ...form, children: parseInt(e.target.value, 10) || 0 })} disabled={disabled} />
                    </div>
                </div>
            </div>

            <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                <div className="rounded-xl border border-slate-200 bg-slate-50 p-3 dark:border-slate-700 dark:bg-slate-800/50">
                    <div className="text-[10px] font-bold uppercase tracking-wider text-slate-400">Noches</div>
                    <div className="mt-1 text-lg font-black text-slate-900 dark:text-white">{nights}</div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-slate-50 p-3 dark:border-slate-700 dark:bg-slate-800/50">
                    <div className="text-[10px] font-bold uppercase tracking-wider text-slate-400">Dias</div>
                    <div className="mt-1 text-lg font-black text-slate-900 dark:text-white">{days}</div>
                </div>
                <div className="rounded-xl border border-slate-200 bg-slate-50 p-3 dark:border-slate-700 dark:bg-slate-800/50">
                    <div className="text-[10px] font-bold uppercase tracking-wider text-slate-400">Pasajeros</div>
                    <div className="mt-1 text-lg font-black text-slate-900 dark:text-white">{(reservaPax || []).length}</div>
                </div>
            </div>

            <div className="pt-2 border-t border-slate-100 dark:border-slate-800">
                <RoomingPlanner
                    rooms={form.rooms || 1}
                    reservaPax={reservaPax}
                    value={form.roomingAssignments}
                    onChange={(val) => setForm((current) => ({ ...current, roomingAssignments: val }))}
                />
            </div>
        </div>
    );
}

/**
 * Formulario de servicio Traslado.
 * Cubre in/out aeropuerto, ciudad a ciudad, etc.
 * Si es ida y vuelta, se habilita el campo de fecha/hora de retorno.
 */
function TransferForm({ form, setForm, suppliers, onRateSelect, disabled, isBudget }) {
    return (
        <div className="space-y-4">
            {/* Proveedor y estado */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId || ""} onChange={(event) => setForm({ ...form, supplierId: event.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map((supplier) => (
                            <option key={supplier.id || supplier.publicId || supplier.PublicId} value={supplier.publicId || supplier.PublicId}>
                                {supplier.name} {!supplier.isActive ? "(Inactivo)" : ""}
                            </option>
                        ))}
                    </select>
                </div>
                {!isBudget && (
                    <div>
                        <label className={labelClass}>Estado *</label>
                        <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={(event) => setForm({ ...form, workflowStatus: event.target.value })} required disabled={disabled}>
                            <option value="Solicitado">Solicitado</option>
                            <option value="Confirmado">Confirmado</option>
                            <option value="Cancelado">Cancelado</option>
                        </select>
                    </div>
                )}
            </div>

            <RateSelector serviceType={form.serviceType || "Traslado"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />

            {/* Puntos de recogida y entrega */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Pick-up (origen)</label>
                    <input className={inputClass} placeholder="Aeropuerto EZE, terminal 1" value={form.pickupLocation || ""} onChange={(event) => setForm({ ...form, pickupLocation: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Drop-off (destino)</label>
                    <input className={inputClass} placeholder="Hotel Sheraton, Retiro" value={form.dropoffLocation || ""} onChange={(event) => setForm({ ...form, dropoffLocation: event.target.value })} disabled={disabled} />
                </div>
            </div>

            {/* Fecha y hora del traslado de ida */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Fecha Pick-up</label>
                    <input type="date" className={inputClass} value={form.pickupDate || ""} onChange={(event) => setForm({ ...form, pickupDate: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Hora Pick-up</label>
                    <input type="time" className={inputClass} value={form.pickupTime || ""} onChange={(event) => setForm({ ...form, pickupTime: event.target.value })} disabled={disabled} />
                </div>
            </div>

            {/* Tipo de vehiculo y cantidad de pasajeros */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Tipo de Vehiculo</label>
                    <input
                        className={inputClass}
                        placeholder="Van, sedan, microbus..."
                        value={form.vehicleType || ""}
                        onChange={(event) => setForm({ ...form, vehicleType: event.target.value })}
                        disabled={disabled}
                        data-testid="transfer-vehicle-type"
                    />
                </div>
                <div>
                    <label className={labelClass}>Cantidad Pasajeros</label>
                    <input
                        type="number"
                        min="1"
                        className={inputClass}
                        value={form.passengers || ""}
                        onChange={(event) => setForm({ ...form, passengers: parseInt(event.target.value, 10) || null })}
                        disabled={disabled}
                        data-testid="transfer-passengers"
                    />
                </div>
            </div>

            {/* Vuelo asociado (util para in-out aeropuerto) y numero de confirmacion */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Vuelo Asociado</label>
                    <input
                        className={inputClass}
                        placeholder="AR1234 (vuelo que se recibe)"
                        value={form.flightNumber || ""}
                        onChange={(event) => setForm({ ...form, flightNumber: event.target.value.toUpperCase() })}
                        disabled={disabled}
                        data-testid="transfer-flight-number"
                    />
                </div>
                <div>
                    <label className={labelClass}>Numero Confirmacion</label>
                    <input
                        className={inputClass}
                        placeholder="CF-00001"
                        value={form.confirmationNumber || ""}
                        onChange={(event) => setForm({ ...form, confirmationNumber: event.target.value })}
                        disabled={disabled}
                        data-testid="transfer-confirmation"
                    />
                </div>
            </div>

            {/* Checkbox ida y vuelta: si esta activo, se muestra la fecha/hora de retorno */}
            <div className="flex items-center gap-2">
                <input
                    type="checkbox"
                    id="transfer-round-trip"
                    className="h-4 w-4 rounded border-slate-300 text-indigo-600"
                    checked={!!form.isRoundTrip}
                    onChange={(event) => setForm({ ...form, isRoundTrip: event.target.checked })}
                    disabled={disabled}
                    data-testid="transfer-is-round-trip"
                />
                <label htmlFor="transfer-round-trip" className="text-sm font-medium text-slate-700 dark:text-slate-300">
                    Ida y vuelta
                </label>
            </div>

            {/* Fecha/hora de retorno — solo visible si es ida y vuelta */}
            {form.isRoundTrip && (
                <div className="grid grid-cols-2 gap-4">
                    <div>
                        <label className={labelClass}>Fecha Retorno</label>
                        <input
                            type="date"
                            className={inputClass}
                            value={form.returnDate || ""}
                            onChange={(event) => setForm({ ...form, returnDate: event.target.value })}
                            disabled={disabled}
                            data-testid="transfer-return-date"
                        />
                    </div>
                    <div>
                        <label className={labelClass}>Hora Retorno</label>
                        <input
                            type="time"
                            className={inputClass}
                            value={form.returnTime || ""}
                            onChange={(event) => setForm({ ...form, returnTime: event.target.value })}
                            disabled={disabled}
                            data-testid="transfer-return-time"
                        />
                    </div>
                </div>
            )}

            {/* Notas internas del traslado */}
            <div>
                <label className={labelClass}>Notas</label>
                <textarea
                    className={`${inputClass} resize-none`}
                    rows={2}
                    placeholder="Observaciones del traslado..."
                    value={form.notes || ""}
                    onChange={(event) => setForm({ ...form, notes: event.target.value })}
                    disabled={disabled}
                    data-testid="transfer-notes"
                />
            </div>
        </div>
    );
}

/**
 * Formulario de servicio Paquete turistico.
 * Incluye: nombre, destino, fechas, adultos/menores, que servicios incluye el paquete,
 * itinerario (texto libre) y numero de confirmacion.
 */
function PackageForm({ form, setForm, suppliers, onRateSelect, disabled, isBudget }) {
    return (
        <div className="space-y-4">
            {/* Proveedor y estado */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Proveedor *</label>
                    <select className={inputClass} value={form.supplierId || ""} onChange={(event) => setForm({ ...form, supplierId: event.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar proveedor...</option>
                        {suppliers.map((supplier) => (
                            <option key={supplier.id || supplier.publicId || supplier.PublicId} value={supplier.publicId || supplier.PublicId}>
                                {supplier.name} {!supplier.isActive ? "(Inactivo)" : ""}
                            </option>
                        ))}
                    </select>
                </div>
                {!isBudget && (
                    <div>
                        <label className={labelClass}>Estado *</label>
                        <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={(event) => setForm({ ...form, workflowStatus: event.target.value })} required disabled={disabled}>
                            <option value="Solicitado">Solicitado</option>
                            <option value="Confirmado">Confirmado</option>
                            <option value="Cancelado">Cancelado</option>
                        </select>
                    </div>
                )}
            </div>

            <RateSelector serviceType={form.serviceType || "Paquete"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />

            {/* Nombre del paquete y destino */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Nombre del Paquete</label>
                    <input className={inputClass} placeholder="Caribe All Inclusive 7N" value={form.packageName || ""} onChange={(event) => setForm({ ...form, packageName: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Destino</label>
                    <input
                        className={inputClass}
                        placeholder="Cancun, Mexico"
                        value={form.destination || ""}
                        onChange={(event) => setForm({ ...form, destination: event.target.value })}
                        disabled={disabled}
                        data-testid="package-destination"
                    />
                </div>
            </div>

            {/* Fechas de inicio y fin del paquete */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Inicio</label>
                    <input type="date" className={inputClass} value={form.startDate || ""} onChange={(event) => setForm({ ...form, startDate: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Fin</label>
                    <input type="date" className={inputClass} value={form.endDate || ""} onChange={(event) => setForm({ ...form, endDate: event.target.value })} disabled={disabled} />
                </div>
            </div>

            {/* Adultos y menores del paquete */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Adultos</label>
                    <input
                        type="number"
                        min="0"
                        className={inputClass}
                        value={form.adults ?? ""}
                        onChange={(event) => setForm({ ...form, adults: parseInt(event.target.value, 10) || 0 })}
                        disabled={disabled}
                        data-testid="package-adults"
                    />
                </div>
                <div>
                    <label className={labelClass}>Menores</label>
                    <input
                        type="number"
                        min="0"
                        className={inputClass}
                        value={form.children ?? ""}
                        onChange={(event) => setForm({ ...form, children: parseInt(event.target.value, 10) || 0 })}
                        disabled={disabled}
                        data-testid="package-children"
                    />
                </div>
            </div>

            {/* Numero de confirmacion del operador */}
            <div>
                <label className={labelClass}>Numero Confirmacion</label>
                <input
                    className={inputClass}
                    placeholder="CF-00001"
                    value={form.confirmationNumber || ""}
                    onChange={(event) => setForm({ ...form, confirmationNumber: event.target.value })}
                    disabled={disabled}
                    data-testid="package-confirmation"
                />
            </div>

            {/* Checkboxes: que servicios incluye el paquete */}
            <div className={panelClass}>
                <p className="mb-3 text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                    Servicios Incluidos en el Paquete
                </p>
                <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
                    {[
                        { field: "includesHotel",      label: "Hotel",      testId: "package-includes-hotel" },
                        { field: "includesFlight",     label: "Vuelo",      testId: "package-includes-flight" },
                        { field: "includesTransfer",   label: "Traslado",   testId: "package-includes-transfer" },
                        { field: "includesExcursions", label: "Excursiones",testId: "package-includes-excursions" },
                        { field: "includesMeals",      label: "Comidas",    testId: "package-includes-meals" },
                    ].map(({ field, label, testId }) => (
                        <label key={field} className="flex cursor-pointer items-center gap-2 text-sm text-slate-700 dark:text-slate-300">
                            <input
                                type="checkbox"
                                className="h-4 w-4 rounded border-slate-300 text-indigo-600"
                                checked={!!form[field]}
                                onChange={(event) => setForm({ ...form, [field]: event.target.checked })}
                                disabled={disabled}
                                data-testid={testId}
                            />
                            {label}
                        </label>
                    ))}
                </div>
            </div>

            {/* Itinerario: descripcion detallada del programa dia por dia */}
            <div>
                <label className={labelClass}>Itinerario / Descripcion</label>
                <textarea
                    className={`${inputClass} resize-none`}
                    rows={4}
                    placeholder="Dia 1: Llegada y traslado al hotel. Dia 2: Tour por la ciudad..."
                    value={form.itinerary || ""}
                    onChange={(event) => setForm({ ...form, itinerary: event.target.value })}
                    disabled={disabled}
                    data-testid="package-itinerary"
                />
            </div>

            {/* Notas internas del paquete */}
            <div>
                <label className={labelClass}>Notas</label>
                <textarea
                    className={`${inputClass} resize-none`}
                    rows={2}
                    placeholder="Observaciones del paquete..."
                    value={form.notes || ""}
                    onChange={(event) => setForm({ ...form, notes: event.target.value })}
                    disabled={disabled}
                    data-testid="package-notes"
                />
            </div>
        </div>
    );
}

/**
 * Formulario de servicio Asistencia al viajero (seguro de viaje).
 * Carga los datos de la poliza: aseguradora, plan, cobertura, vigencia y pasajeros cubiertos.
 * La comision NO se muestra — regla de negocio del dueño, igual que los otros tipos.
 * validFrom/validTo se tratan como fechas date-only (sin hora), igual que checkIn/checkOut en Hotel.
 */
function AssistanceForm({ form, setForm, suppliers, onRateSelect, disabled, isBudget }) {
    return (
        <div className="space-y-4">
            {/* Aseguradora/proveedor y estado de la asistencia */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Aseguradora / Proveedor *</label>
                    <select
                        className={inputClass}
                        value={form.supplierId || ""}
                        onChange={(event) => setForm({ ...form, supplierId: event.target.value })}
                        required
                        disabled={disabled}
                    >
                        <option value="">Seleccionar aseguradora...</option>
                        {suppliers.map((supplier) => (
                            <option
                                key={supplier.id || supplier.publicId || supplier.PublicId}
                                value={supplier.publicId || supplier.PublicId}
                            >
                                {supplier.name} {!supplier.isActive ? "(Inactivo)" : ""}
                            </option>
                        ))}
                    </select>
                </div>
                {/* El estado de la asistencia no se muestra en presupuesto (regla igual que Hotel/Traslado) */}
                {!isBudget && (
                    <div>
                        <label className={labelClass}>Estado *</label>
                        <select
                            className={inputClass}
                            value={form.workflowStatus || "Solicitado"}
                            onChange={(event) => setForm({ ...form, workflowStatus: event.target.value })}
                            required
                            disabled={disabled}
                        >
                            <option value="Solicitado">Solicitado</option>
                            <option value="Confirmado">Confirmado</option>
                            <option value="Cancelado">Cancelado</option>
                        </select>
                    </div>
                )}
            </div>

            {/* Selector de tarifa del tarifario de asistencias */}
            <RateSelector
                serviceType={form.serviceType || "Asistencia"}
                supplierId={form.supplierId}
                onSelect={onRateSelect}
                disabled={disabled}
            />

            {/* Datos identificatorios de la poliza */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Nro. de Poliza</label>
                    <input
                        className={inputClass}
                        placeholder="POL-00001"
                        value={form.policyNumber || ""}
                        onChange={(event) => setForm({ ...form, policyNumber: event.target.value })}
                        disabled={disabled}
                        data-testid="assistance-policy-number"
                    />
                </div>
                <div>
                    <label className={labelClass}>Tipo de Plan</label>
                    <input
                        className={inputClass}
                        placeholder="Basic, Plus, Premium..."
                        value={form.planType || ""}
                        onChange={(event) => setForm({ ...form, planType: event.target.value })}
                        disabled={disabled}
                        data-testid="assistance-plan-type"
                    />
                </div>
            </div>

            {/* Cobertura (TEXTO libre, ej: "USD 30.000") y zona cubierta */}
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Cobertura / Limite</label>
                    {/* coverageLimit es texto segun el contrato del backend, no un numero */}
                    <input
                        className={inputClass}
                        placeholder="USD 30.000, EUR 20.000..."
                        value={form.coverageLimit || ""}
                        onChange={(event) => setForm({ ...form, coverageLimit: event.target.value })}
                        disabled={disabled}
                        data-testid="assistance-coverage-limit"
                    />
                </div>
                <div>
                    <label className={labelClass}>Zona / Destinos Cubiertos</label>
                    <input
                        className={inputClass}
                        placeholder="Mundo excepto USA y Canada..."
                        value={form.coverageZone || ""}
                        onChange={(event) => setForm({ ...form, coverageZone: event.target.value })}
                        disabled={disabled}
                        data-testid="assistance-coverage-zone"
                    />
                </div>
            </div>

            {/* Vigencia: date-only. Igual que checkIn/checkOut en Hotel — NO incluye hora. */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Vigencia Desde *</label>
                    <div className="relative">
                        <CalendarDays className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                        <input
                            type="date"
                            className={`${inputClass} pl-10`}
                            value={form.validFrom || ""}
                            onChange={(event) => setForm({ ...form, validFrom: event.target.value })}
                            required
                            disabled={disabled}
                            data-testid="assistance-valid-from"
                        />
                    </div>
                </div>
                <div>
                    <label className={labelClass}>Vigencia Hasta *</label>
                    <div className="relative">
                        <CalendarDays className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                        <input
                            type="date"
                            className={`${inputClass} pl-10`}
                            value={form.validTo || ""}
                            onChange={(event) => setForm({ ...form, validTo: event.target.value })}
                            required
                            disabled={disabled}
                            data-testid="assistance-valid-to"
                        />
                    </div>
                </div>
            </div>

            {/* Cantidad de pasajeros cubiertos por la poliza */}
            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Adultos</label>
                    <input
                        type="number"
                        min="0"
                        className={inputClass}
                        value={form.adults ?? ""}
                        onChange={(event) => setForm({ ...form, adults: parseInt(event.target.value, 10) || 0 })}
                        disabled={disabled}
                        data-testid="assistance-adults"
                    />
                </div>
                <div>
                    <label className={labelClass}>Menores</label>
                    <input
                        type="number"
                        min="0"
                        className={inputClass}
                        value={form.children ?? ""}
                        onChange={(event) => setForm({ ...form, children: parseInt(event.target.value, 10) || 0 })}
                        disabled={disabled}
                        data-testid="assistance-children"
                    />
                </div>
            </div>

            {/* Numero de confirmacion del operador/aseguradora */}
            <div>
                <label className={labelClass}>Nro. Confirmacion</label>
                <input
                    className={inputClass}
                    placeholder="CF-00001"
                    value={form.confirmationNumber || ""}
                    onChange={(event) => setForm({ ...form, confirmationNumber: event.target.value })}
                    disabled={disabled}
                    data-testid="assistance-confirmation"
                />
            </div>

            {/* Notas internas de la asistencia */}
            <div>
                <label className={labelClass}>Notas</label>
                <textarea
                    className={`${inputClass} resize-none`}
                    rows={2}
                    placeholder="Observaciones de la asistencia..."
                    value={form.notes || ""}
                    onChange={(event) => setForm({ ...form, notes: event.target.value })}
                    disabled={disabled}
                    data-testid="assistance-notes"
                />
            </div>
        </div>
    );
}

function GenericServiceForm({ form, setForm, suppliers, disabled }) {
    const baseTypeOptions = [
        ...SERVICE_TYPES.map(({ value, label }) => ({ value, label })),
        { value: "Otro", label: "Otro" },
    ];
    const currentType = form.serviceType || "Otro";
    const hasCurrentType = baseTypeOptions.some((option) => option.value === currentType);
    const typeOptions = hasCurrentType
        ? baseTypeOptions
        : [{ value: currentType, label: currentType }, ...baseTypeOptions];

    return (
        <div className="space-y-4">
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Tipo de servicio *</label>
                    <select className={inputClass} value={currentType} onChange={(event) => setForm({ ...form, serviceType: event.target.value })} required disabled={disabled}>
                        {typeOptions.map((option) => (
                            <option key={option.value} value={option.value}>{option.label}</option>
                        ))}
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Proveedor</label>
                    <select className={inputClass} value={form.supplierId || ""} onChange={(event) => setForm({ ...form, supplierId: event.target.value })} disabled={disabled}>
                        <option value="">Sin proveedor vinculado</option>
                        {suppliers.map((supplier) => (
                            <option key={supplier.id || supplier.publicId || supplier.PublicId} value={supplier.publicId || supplier.PublicId}>
                                {supplier.name} {!supplier.isActive ? "(Inactivo)" : ""}
                            </option>
                        ))}
                    </select>
                </div>
            </div>

            <div>
                <label className={labelClass}>Descripcion *</label>
                <input className={inputClass} value={form.description || ""} onChange={(event) => setForm({ ...form, description: event.target.value })} required disabled={disabled} />
            </div>

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
                <div>
                    <label className={labelClass}>Salida *</label>
                    <input type="date" className={inputClass} value={form.departureDate || ""} onChange={(event) => setForm({ ...form, departureDate: event.target.value })} required disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Regreso</label>
                    <input type="date" className={inputClass} value={form.returnDate || ""} onChange={(event) => setForm({ ...form, returnDate: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Confirmacion</label>
                    <input className={inputClass} value={form.confirmationNumber || ""} onChange={(event) => setForm({ ...form, confirmationNumber: event.target.value })} disabled={disabled} />
                </div>
            </div>
        </div>
    );
}

function PricingForm({ form, setForm, commissionPercent, onRecalculate, disabled, onManualPriceChange }) {
    const canSeeCost = hasPermission("cobranzas.see_cost");
    const margin = canSeeCost ? (form.salePrice || 0) - (form.netCost || 0) : null;

    // Con permiso: 3 columnas (Costo Neto, Precio Venta, Ganancia).
    // Sin permiso: 1 columna centrada (solo Precio Venta).
    const gridClass = canSeeCost ? "grid grid-cols-3 gap-4" : "grid grid-cols-1 gap-4";

    return (
        <div className="space-y-4 rounded-xl border border-slate-200 bg-gradient-to-r from-slate-50 to-slate-100 p-4 dark:border-slate-700 dark:from-slate-800 dark:to-slate-800/50">
            <div className="flex items-center justify-between">
                <h4 className="flex items-center gap-2 text-xs font-bold uppercase text-slate-500 dark:text-slate-400">
                    <DollarSign className="h-4 w-4" /> Valores Economicos
                </h4>
                {canSeeCost && commissionPercent > 0 && !disabled ? (
                    <button
                        type="button"
                        onClick={onRecalculate}
                        className="flex items-center gap-1 rounded-lg bg-indigo-100 px-3 py-1.5 text-xs text-indigo-700 transition-colors hover:bg-indigo-200 dark:bg-indigo-900/50 dark:text-indigo-300 dark:hover:bg-indigo-900"
                    >
                        <Calculator className="h-3 w-3" /> Aplicar {commissionPercent}%
                    </button>
                ) : null}
            </div>

            <div className={gridClass}>
                {canSeeCost ? (
                    <div>
                        <label className={labelClass}>Costo Neto *</label>
                        <div className="relative">
                            <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                            <input
                                type="number"
                                step="0.01"
                                className={`${inputClass} pl-6`}
                                value={form.netCost || 0}
                                onChange={(event) => {
                                    const netCost = parseFloat(event.target.value) || 0;
                                    onManualPriceChange?.("netCost");
                                    setForm({ ...form, netCost, commission: roundMoney((form.salePrice || 0) - netCost) });
                                }}
                                required
                                disabled={disabled}
                            />
                        </div>
                    </div>
                ) : null}
                <div>
                    <label className={labelClass}>Precio Venta *</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                        <input
                            type="number"
                            step="0.01"
                            className={`${inputClass} pl-6`}
                            value={form.salePrice || 0}
                            onChange={(event) => {
                                const salePrice = parseFloat(event.target.value) || 0;
                                onManualPriceChange?.("salePrice");
                                setForm({ ...form, salePrice, commission: roundMoney(salePrice - (form.netCost || 0)) });
                            }}
                            required
                            disabled={disabled}
                        />
                    </div>
                </div>
                {canSeeCost ? (
                    <div>
                        <label className={labelClass}>Ganancia</label>
                        <div className={`rounded-xl p-2.5 text-center font-bold ${margin >= 0 ? "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400" : "bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400"}`}>
                            {formatMoney(margin)}
                        </div>
                    </div>
                ) : null}
            </div>
        </div>
    );
}

export default function ServiceFormModal({ isOpen, onClose, reservaId, reservaStatus, suppliers, onSuccess, initialServiceType, serviceToEdit, reservaPax }) {
    const [serviceType, setServiceType] = useState(initialServiceType || "Aereo");
    const [form, setForm] = useState({
        supplierId: "",
        rateId: "",
        netCost: 0,
        salePrice: 0,
        commission: 0,
        unitNetCost: 0,
        unitSalePrice: 0,
        unitCommission: 0,
        rooms: 1,
        adults: 2,
        children: 0,
        roomType: "",
        mealPlan: "",
        checkIn: "",
        checkOut: "",
        roomingAssignments: "",
        workflowStatus: "Solicitado"
    });
    const [loading, setLoading] = useState(false);
    const [commissionPercent, setCommissionPercent] = useState(10);
    const [manualHotelPricing, setManualHotelPricing] = useState({ netCost: false, salePrice: false });

    const isGenericEdit = serviceToEdit?.recordKind === SERVICE_RECORD_KIND.GENERIC;
    const isLocked = reservaStatus === "Traveling" || reservaStatus === "Closed";
    // En Presupuesto el agente NO elige el estado del servicio — siempre queda en
    // "Solicitado" hasta que la reserva sea Confirmed (regla de negocio: en
    // Presupuesto los servicios todavia no se confirman con el proveedor).
    const isBudget = reservaStatus === "Budget";
    const showPricingForm = true;

    const serviceTabClassMap = {
        sky: "border-sky-500 text-sky-600 bg-white dark:bg-slate-900",
        amber: "border-amber-500 text-amber-600 bg-white dark:bg-slate-900",
        emerald: "border-emerald-500 text-emerald-600 bg-white dark:bg-slate-900",
        violet: "border-violet-500 text-violet-600 bg-white dark:bg-slate-900",
        blue: "border-blue-500 text-blue-600 bg-white dark:bg-slate-900",
    };

    const sortedSuppliers = useMemo(() => {
        return [...suppliers].sort((a, b) => {
            if (a.isActive === b.isActive) return a.name.localeCompare(b.name);
            return a.isActive ? -1 : 1;
        });
    }, [suppliers]);

    const fetchCommission = useCallback(async () => {
        try {
            const params = new URLSearchParams();
            if (form.supplierId) params.append("supplierId", form.supplierId);
            params.append("serviceType", serviceType);
            const result = await api.get(`/commissions/calculate?${params}`);
            setCommissionPercent(result.commissionPercent || 10);
        } catch {
            setCommissionPercent(10);
        }
    }, [form.supplierId, serviceType]);

    useEffect(() => {
        if (form.supplierId && showPricingForm) fetchCommission();
    }, [fetchCommission, form.supplierId, showPricingForm]);

    useEffect(() => {
        if (serviceType === "Hotel") {
            const qty = getHotelQuantity(form);
            if (form.unitNetCost > 0 || form.unitSalePrice > 0 || form.unitCommission > 0) {
                setForm((prev) => {
                    const next = { ...prev };

                    if (!manualHotelPricing.netCost && prev.unitNetCost > 0) {
                        next.netCost = roundMoney(prev.unitNetCost * qty);
                    }

                    if (!manualHotelPricing.salePrice && prev.unitSalePrice > 0) {
                        next.salePrice = roundMoney(prev.unitSalePrice * qty);
                    }

                    if (prev.unitCommission > 0) {
                        next.commission = roundMoney(prev.unitCommission * qty);
                    } else {
                        next.commission = roundMoney((next.salePrice || 0) - (next.netCost || 0));
                    }

                    return next;
                });
            }
        }
    }, [form.checkIn, form.checkOut, form.rooms, form.unitNetCost, form.unitSalePrice, form.unitCommission, manualHotelPricing.netCost, manualHotelPricing.salePrice, serviceType]);

    useEffect(() => {
        if (!isOpen) return;

        if (serviceToEdit) {
            const nextServiceType = isGenericEdit
                ? serviceToEdit.serviceType || serviceToEdit.displayType || "Generico"
                : serviceToEdit.displayType || serviceToEdit._type || serviceType;

            setServiceType(nextServiceType);

            // Extrae solo "HH:MM" de un string datetime LOCAL guardado por el backend
            // (formato "2025-06-15T14:30:00", SIN Z ni offset).
            //
            // POR QUE no usamos new Date(...).toLocaleTimeString():
            //   - Si el string tiene Z (UTC), el browser lo convierte a hora local al parsearlo,
            //     corriendo la hora (ej. "14:30Z" -> "11:30" en Argentina UTC-3).
            //   - Con el nuevo contrato el backend guarda la hora de pared sin Z,
            //     pero parsear por string es mas seguro y no depende del timezone del browser.
            const extractTimeFromLocalString = (localDatetimeString) => {
                if (!localDatetimeString) return "";
                // Tomamos los caracteres despues de la "T": "2025-06-15T14:30:00" -> "14:30:00"
                // y nos quedamos solo con HH:MM (primeros 5 caracteres).
                const separatorIndex = localDatetimeString.indexOf("T");
                if (separatorIndex === -1) return "";
                return localDatetimeString.slice(separatorIndex + 1, separatorIndex + 6);
            };

            const formattedForm = {
                ...serviceToEdit,
                serviceType: nextServiceType,
                rateId: serviceToEdit.ratePublicId?.toString() || serviceToEdit.rateId?.toString() || "",
                supplierId: serviceToEdit.supplierPublicId?.toString() || serviceToEdit.supplierId?.toString() || "",
                // Fecha de salida del vuelo (solo "YYYY-MM-DD", sin hora).
                // formatDateForInput toma el fragmento antes de la "T", seguro para strings locales.
                departureDate: formatDateForInput(isGenericEdit ? serviceToEdit.departureDate || serviceToEdit.date : serviceToEdit.departureTime),
                // Hora de salida del vuelo (solo "HH:MM"), extraida SIN pasar por new Date() para
                // no re-correr la hora local a UTC ni al reves.
                departureTime: isGenericEdit ? "" : extractTimeFromLocalString(serviceToEdit.departureTime),
                // Fecha de llegada del vuelo (solo "YYYY-MM-DD")
                arrivalDate: formatDateForInput(serviceToEdit.arrivalTime),
                // Hora de llegada del vuelo (solo "HH:MM"), misma logica que departureTime.
                arrivalTime: isGenericEdit ? "" : extractTimeFromLocalString(serviceToEdit.arrivalTime),
                checkIn: formatDateForInput(serviceToEdit.checkIn),
                checkOut: formatDateForInput(serviceToEdit.checkOut),
                startDate: formatDateForInput(serviceToEdit.startDate),
                endDate: formatDateForInput(serviceToEdit.endDate),
                pickupDate: formatDateForInput(serviceToEdit.pickupDateTime),
                // Hora del traslado de ida, extraida por string para no correr la hora.
                pickupTime: extractTimeFromLocalString(serviceToEdit.pickupDateTime),
                returnDate: formatDateForInput(isGenericEdit ? serviceToEdit.returnDate : serviceToEdit.returnDateTime),
                // Hora del traslado de retorno, misma logica.
                returnTime: extractTimeFromLocalString(serviceToEdit.returnDateTime),
                roomingAssignments: serviceToEdit.roomingAssignmentsJson || serviceToEdit.roomingAssignments || "",
                workflowStatus: serviceToEdit.workflowStatus || "Solicitado",
                // Asistencia: validFrom/validTo son date-only, el mismo tratamiento que checkIn/checkOut.
                // formatDateForInput toma solo la parte "YYYY-MM-DD" antes de la "T".
                validFrom: formatDateForInput(serviceToEdit.validFrom),
                validTo: formatDateForInput(serviceToEdit.validTo),
                // Campos opcionales de la poliza — null viene del backend como string vacio para no romper inputs
                policyNumber: serviceToEdit.policyNumber || "",
                planType: serviceToEdit.planType || "",
                coverageLimit: serviceToEdit.coverageLimit || "",
                coverageZone: serviceToEdit.coverageZone || "",
            };

            if (nextServiceType === "Hotel") {
                const nights = calculateNights(formattedForm.checkIn, formattedForm.checkOut);
                const qty = Math.max(nights || 1, 1) * Math.max(serviceToEdit.rooms || 1, 1);
                formattedForm.unitNetCost = qty > 0 ? (Number(serviceToEdit.netCost) || 0) / qty : 0;
                formattedForm.unitSalePrice = qty > 0 ? (Number(serviceToEdit.salePrice) || 0) / qty : 0;
                formattedForm.unitCommission = qty > 0 ? (Number(serviceToEdit.commission) || 0) / qty : 0;
            }

            setManualHotelPricing({ netCost: false, salePrice: false });
            setForm(formattedForm);
            return;
        }

        setServiceType(initialServiceType || "Aereo");
        setManualHotelPricing({ netCost: false, salePrice: false });
        setForm({
            supplierId: "",
            rateId: "",
            netCost: 0,
            salePrice: 0,
            commission: 0,
            unitNetCost: 0,
            unitSalePrice: 0,
            unitCommission: 0,
            rooms: 1,
            adults: 2,
            children: 0,
            roomType: "",
            mealPlan: "",
            checkIn: "",
            checkOut: "",
            roomingAssignments: "",
            workflowStatus: "Solicitado"
        });
    }, [initialServiceType, isGenericEdit, isOpen, serviceToEdit]);

    const handleRateSelect = (rate) => {
        setManualHotelPricing({ netCost: false, salePrice: false });
        setForm((prev) => {
            const hotelQuantity = getHotelQuantity(prev);
            const newForm = {
                ...prev,
                rateId: rate.publicId?.toString() || "",
                unitNetCost: rate.netCost,
                unitSalePrice: rate.salePrice,
                unitCommission: rate.commission ?? Math.max((rate.salePrice || 0) - (rate.netCost || 0), 0),
                commission: rate.commission ?? Math.max((rate.salePrice || 0) - (rate.netCost || 0), 0),
                description: rate.description || prev.description || ""
            };

            if (serviceType !== "Hotel") {
                newForm.netCost = rate.netCost;
                newForm.salePrice = rate.salePrice;
                newForm.commission = rate.commission ?? Math.max((rate.salePrice || 0) - (rate.netCost || 0), 0);
            }

            if (serviceType === "Hotel") {
                newForm.hotelName = rate.hotelName || rate.productName;
                newForm.city = rate.city || prev.city;
                if (rate.roomType) newForm.roomType = rate.roomType;
                if (rate.mealPlan) newForm.mealPlan = rate.mealPlan;
                newForm.netCost = roundMoney((rate.netCost || 0) * hotelQuantity);
                newForm.salePrice = roundMoney((rate.salePrice || 0) * hotelQuantity);
                newForm.commission = roundMoney((newForm.unitCommission || 0) * hotelQuantity);
            } else if (serviceType === "Paquete") {
                newForm.packageName = rate.productName;
            } else if (serviceType === "Aereo") {
                newForm.origin = rate.origin || prev.origin;
                newForm.destination = rate.destination || prev.destination;
                newForm.airline = rate.airline || prev.airline;
            } else if (serviceType === "Traslado") {
                newForm.pickupLocation = rate.pickupLocation || prev.pickupLocation;
                newForm.dropoffLocation = rate.dropoffLocation || prev.dropoffLocation;
            }

            return newForm;
        });
    };

    const applyCommission = () => {
        if (serviceType === "Hotel" && form.unitNetCost > 0) {
            const unitCost = form.unitNetCost;
            const unitMargin = unitCost * (commissionPercent / 100);
            const unitSalePrice = roundMoney(unitCost + unitMargin);
            const qty = getHotelQuantity(form);
            setManualHotelPricing((current) => ({ ...current, salePrice: false }));
            setForm((prev) => ({
                ...prev,
                unitSalePrice,
                unitCommission: roundMoney(unitMargin),
                salePrice: roundMoney(unitSalePrice * qty),
                commission: roundMoney(unitMargin * qty),
            }));
        } else {
            const cost = form.netCost || 0;
            const margin = cost * (commissionPercent / 100);
            setForm((prev) => ({ ...prev, salePrice: roundMoney(cost + margin), commission: roundMoney(margin) }));
        }
    };

    const handleSubmit = async (e, shouldClose = true) => {
        if (e) e.preventDefault();
        setLoading(true);

        const isLegacyHotelEdit = serviceToEdit && serviceType === "Hotel" && !serviceToEdit.rateId;

        try {
            const method = serviceToEdit ? "put" : "post";
            const endpoint = serviceToEdit
                ? getServiceMutationEndpoint(reservaId, serviceToEdit)
                : getServiceCreateEndpoint(reservaId, serviceType);

            const payload = isGenericEdit ? buildGenericServicePayload(form, serviceToEdit) : { ...form };

            if (!isGenericEdit && serviceType === "Aereo") {
                // POR QUE NO usamos toIsoDate() / toISOString() acá:
                //   La hora de un vuelo es la "hora de pared" del aeropuerto (hora local),
                //   NO un instante UTC. Si convertimos a UTC, un vuelo que sale a las 14:30 en
                //   Argentina (UTC-3) se mandaria como "17:30Z" y el voucher del pasajero
                //   mostraria la hora equivocada.
                //   El contrato con el backend es: mandar "YYYY-MM-DDTHH:mm:00" SIN sufijo Z.
                if (!form.departureDate) throw new Error("Completa la fecha de salida.");
                if (!form.arrivalDate) throw new Error("Completa la fecha de llegada.");
                payload.departureTime = `${form.departureDate}T${form.departureTime || "00:00"}:00`;
                payload.arrivalTime = `${form.arrivalDate}T${form.arrivalTime || "00:00"}:00`;

                // Campos operativos del vuelo — todos opcionales salvo los de fecha
                payload.flightNumber = form.flightNumber || null;
                payload.airlineCode = form.airlineCode || null;
                payload.airlineName = form.airlineName || null;
                payload.originCity = form.originCity || null;
                payload.destinationCity = form.destinationCity || null;
                payload.cabinClass = form.cabinClass || null;
                payload.pnr = form.pnr || null;
                payload.confirmationNumber = form.confirmationNumber || null;
                payload.ticketNumber = form.ticketNumber || null;
                payload.baggage = form.baggage || null;
                payload.passengerCount = form.passengerCount ? Number(form.passengerCount) : null;
                payload.notes = form.notes || null;

            } else if (!isGenericEdit && serviceType === "Hotel") {
                if (!form.rateId && !isLegacyHotelEdit) {
                    throw new Error("Selecciona un hotel y una variante antes de guardar.");
                }
                payload.checkIn = toIsoDate(form.checkIn, "check-in");
                payload.checkOut = toIsoDate(form.checkOut, "check-out");

            } else if (!isGenericEdit && serviceType === "Traslado") {
                // POR QUE NO usamos toIsoDate() / toISOString() acá:
                //   La hora del traslado es la "hora de pared" en el lugar del servicio.
                //   Convertir a UTC correria la hora y el pasajero recibiria el voucher con
                //   horario equivocado (mismo problema que con el vuelo).
                //   Mandamos "YYYY-MM-DDTHH:mm:00" SIN sufijo Z.
                if (!form.pickupDate) throw new Error("Completa la fecha de pick-up.");
                payload.pickupDateTime = `${form.pickupDate}T${form.pickupTime || "00:00"}:00`;

                // returnDateTime: solo se manda si es ida y vuelta; null si no lo es.
                if (form.isRoundTrip && form.returnDate) {
                    payload.returnDateTime = `${form.returnDate}T${form.returnTime || "00:00"}:00`;
                } else {
                    payload.returnDateTime = null;
                }

                // Campos operativos del traslado — todos opcionales
                payload.isRoundTrip = !!form.isRoundTrip;
                payload.vehicleType = form.vehicleType || null;
                payload.passengers = form.passengers ? Number(form.passengers) : null;
                payload.flightNumber = form.flightNumber || null;
                payload.confirmationNumber = form.confirmationNumber || null;
                payload.notes = form.notes || null;

            } else if (!isGenericEdit && serviceType === "Paquete") {
                payload.startDate = toIsoDate(form.startDate, "inicio");
                payload.endDate = toIsoDate(form.endDate, "fin");

                // Campos operativos del paquete — todos opcionales
                payload.destination = form.destination || null;
                payload.adults = form.adults != null ? Number(form.adults) : null;
                payload.children = form.children != null ? Number(form.children) : null;
                payload.confirmationNumber = form.confirmationNumber || null;
                payload.itinerary = form.itinerary || null;
                payload.notes = form.notes || null;
                payload.includesHotel = !!form.includesHotel;
                payload.includesFlight = !!form.includesFlight;
                payload.includesTransfer = !!form.includesTransfer;
                payload.includesExcursions = !!form.includesExcursions;
                payload.includesMeals = !!form.includesMeals;

            } else if (!isGenericEdit && serviceType === "Asistencia") {
                // validFrom/validTo son date-only — igual tratamiento que checkIn/checkOut en Hotel.
                // toIsoDate() los convierte a ISO string ("2025-06-01T00:00:00.000Z").
                // El backend los parsea como DateOnly/date (solo toma la parte de fecha).
                payload.validFrom = toIsoDate(form.validFrom, "vigencia desde");
                payload.validTo = toIsoDate(form.validTo, "vigencia hasta");

                // Campos opcionales de la poliza — se mandan null si estan vacios
                payload.policyNumber = form.policyNumber || null;
                payload.planType = form.planType || null;
                // coverageLimit es TEXTO libre segun el contrato del backend (no un numero)
                payload.coverageLimit = form.coverageLimit || null;
                payload.coverageZone = form.coverageZone || null;
                payload.adults = form.adults != null ? Number(form.adults) : null;
                payload.children = form.children != null ? Number(form.children) : null;
                payload.confirmationNumber = form.confirmationNumber || null;
                payload.notes = form.notes || null;
            }

            const savedService = method === "put"
                ? await api.put(endpoint, payload)
                : await api.post(endpoint, payload);
            const persistedService = unwrapSavedService(savedService);
            
            await onSuccess?.({
                service: persistedService,
                serviceType,
                action: method,
                showLoading: false,
            });

            if (shouldClose) {
                showSuccess("Servicio guardado");
                onClose();
            } else {
                showSuccess(`Habitacion "${form.roomType || "Estandar"}" agregada correctamente.`);
                setManualHotelPricing({ netCost: false, salePrice: false });
                // Reset variant-specific fields but keep hotel, dates and supplier
                setForm(prev => ({
                    ...prev,
                    rateId: "",
                    unitNetCost: 0,
                    unitSalePrice: 0,
                    unitCommission: 0,
                    netCost: 0,
                    salePrice: 0,
                    commission: 0,
                    roomType: "",
                    mealPlan: "",
                    roomingAssignments: "",
                    rooms: 1
                }));
            }
        } catch (error) {
            showError(error.message || "Error al guardar");
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    const currentType = isGenericEdit
        ? { icon: Package }
        : SERVICE_TYPES.find((type) => type.value === serviceType);


    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm">
            <div className="w-full max-w-2xl overflow-hidden rounded-2xl bg-white shadow-2xl dark:bg-slate-900">
                <div className="flex items-center justify-between border-b border-slate-200 bg-gradient-to-r from-indigo-500 to-sky-600 p-4 text-white dark:border-slate-700">
                    <h2 className="flex items-center gap-2 text-lg font-semibold">
                        {currentType ? <currentType.icon className="h-5 w-5" /> : null}
                        {serviceToEdit ? "Editar Servicio" : "Agregar Servicio"}
                    </h2>
                    <button onClick={onClose} className="rounded-lg p-2 text-white/80 transition-colors hover:bg-white/20 hover:text-white">
                        <X className="h-5 w-5" />
                    </button>
                </div>

                {!isGenericEdit ? (
                    <div className="flex border-b border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/50">
                        {SERVICE_TYPES.map(({ value, label, icon: Icon, color }) => (
                            <button
                                key={value}
                                type="button"
                                onClick={() => {
                                    if (!serviceToEdit) {
                                        setServiceType(value);
                                        setManualHotelPricing({ netCost: false, salePrice: false });
                                        setForm((prev) => ({ ...prev, serviceType: value }));
                                    }
                                }}
                                disabled={!!serviceToEdit}
                                className={`flex flex-1 items-center justify-center gap-2 py-3.5 text-sm font-medium transition-all ${
                                    serviceType === value
                                        ? `border-b-2 ${serviceTabClassMap[color] || "border-indigo-500 text-indigo-600 bg-white dark:bg-slate-900"}`
                                        : "text-slate-500"
                                }`}
                            >
                                <Icon className="h-4 w-4" />
                                <span className="hidden sm:inline">{label}</span>
                            </button>
                        ))}
                    </div>
                ) : null}

                <form onSubmit={handleSubmit} className="max-h-[78vh] space-y-4 overflow-y-auto p-4">
                    {isLocked ? (
                        <div className="mb-4 flex items-center gap-3 rounded-xl border border-slate-200 bg-slate-100 p-3 text-sm text-slate-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400">
                            <AlertCircle className="h-5 w-5" />
                            <p>Reserva en estado <b>{reservaStatus}</b>. La edicion economica queda bloqueada.</p>
                        </div>
                    ) : null}

                    {isGenericEdit ? <GenericServiceForm form={form} setForm={setForm} suppliers={sortedSuppliers} disabled={isLocked} /> : null}
                    {!isGenericEdit && serviceType === "Aereo" ? <FlightForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} isBudget={isBudget} /> : null}
                    {!isGenericEdit && serviceType === "Hotel" ? (
                        <HotelForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} reservaPax={reservaPax} isBudget={isBudget} />
                    ) : null}
                    {!isGenericEdit && serviceType === "Traslado" ? <TransferForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} isBudget={isBudget} /> : null}
                    {!isGenericEdit && serviceType === "Paquete" ? <PackageForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} isBudget={isBudget} /> : null}
                    {!isGenericEdit && serviceType === "Asistencia" ? <AssistanceForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} isBudget={isBudget} /> : null}

                    {showPricingForm ? (
                        <PricingForm
                            form={form}
                            setForm={setForm}
                            commissionPercent={commissionPercent}
                            onRecalculate={applyCommission}
                            disabled={isLocked}
                            onManualPriceChange={(field) => {
                                if (serviceType === "Hotel") {
                                    setManualHotelPricing((current) => ({ ...current, [field]: true }));
                                }
                            }}
                        />
                    ) : null}

                    <div className="flex justify-end gap-3 border-t border-slate-200 pt-4 dark:border-slate-700">
                        {!serviceToEdit && serviceType === "Hotel" && form.rateId && (
                            <button 
                                type="button" 
                                onClick={() => handleSubmit(null, false)} 
                                disabled={loading}
                                className="rounded-xl border border-indigo-200 bg-indigo-50 px-5 py-2.5 text-sm font-medium text-indigo-600 hover:bg-indigo-100 disabled:opacity-50 dark:border-indigo-800 dark:bg-indigo-900/20 dark:text-indigo-400"
                            >
                                Guardar y Agregar Otra Hab.
                            </button>
                        )}
                        <button type="button" onClick={onClose} className="rounded-xl px-5 py-2.5 text-sm text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">
                            Cancelar
                        </button>
                        <button type="submit" disabled={loading} className="rounded-xl bg-indigo-600 px-5 py-2.5 text-sm text-white disabled:opacity-50 shadow-lg shadow-indigo-200 dark:shadow-none">
                            {loading ? "Guardando..." : (serviceToEdit ? "Actualizar" : "Guardar Servicio")}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}

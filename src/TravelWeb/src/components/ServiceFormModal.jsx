import { useState, useEffect, useCallback, useMemo } from "react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import {
    X,
    Plane,
    Hotel,
    Bus,
    Package,
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
    { value: "Paquete", label: "Paquete", icon: Package, color: "violet" }
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

function FlightForm({ form, setForm, suppliers, onRateSelect, disabled }) {
    return (
        <div className="space-y-4">
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
                <div>
                    <label className={labelClass}>Estado *</label>
                    <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={(event) => setForm({ ...form, workflowStatus: event.target.value })} required disabled={disabled}>
                        <option value="Solicitado">Solicitado</option>
                        <option value="Confirmado">Confirmado</option>
                        <option value="Cancelado">Cancelado</option>
                    </select>
                </div>
            </div>

            <RateSelector serviceType={form.serviceType || "Aereo"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />

            <div className="grid grid-cols-2 gap-4 sm:grid-cols-4">
                <div>
                    <label className={labelClass}>Origen</label>
                    <input className={inputClass} placeholder="BUE" value={form.origin || ""} onChange={(event) => setForm({ ...form, origin: event.target.value.toUpperCase() })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Destino</label>
                    <input className={inputClass} placeholder="MIA" value={form.destination || ""} onChange={(event) => setForm({ ...form, destination: event.target.value.toUpperCase() })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Salida</label>
                    <input type="date" className={inputClass} value={form.departureDate || ""} onChange={(event) => setForm({ ...form, departureDate: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Regreso</label>
                    <input type="date" className={inputClass} value={form.arrivalDate || ""} onChange={(event) => setForm({ ...form, arrivalDate: event.target.value })} disabled={disabled} />
                </div>
            </div>
        </div>
    );
}

function HotelForm({ form, setForm, suppliers, onRateSelect, disabled, reservaPax }) {
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
                    onChange={(val) => setForm({ ...form, roomingAssignments: val })}
                />
            </div>
        </div>
    );
}

function TransferForm({ form, setForm, suppliers, onRateSelect, disabled }) {
    return (
        <div className="space-y-4">
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
                <div>
                    <label className={labelClass}>Estado *</label>
                    <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={(event) => setForm({ ...form, workflowStatus: event.target.value })} required disabled={disabled}>
                        <option value="Solicitado">Solicitado</option>
                        <option value="Confirmado">Confirmado</option>
                        <option value="Cancelado">Cancelado</option>
                    </select>
                </div>
            </div>

            <RateSelector serviceType={form.serviceType || "Traslado"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Pick-up</label>
                    <input className={inputClass} value={form.pickupLocation || ""} onChange={(event) => setForm({ ...form, pickupLocation: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Drop-off</label>
                    <input className={inputClass} value={form.dropoffLocation || ""} onChange={(event) => setForm({ ...form, dropoffLocation: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Fecha</label>
                    <input type="date" className={inputClass} value={form.pickupDate || ""} onChange={(event) => setForm({ ...form, pickupDate: event.target.value })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Hora</label>
                    <input type="time" className={inputClass} value={form.pickupTime || ""} onChange={(event) => setForm({ ...form, pickupTime: event.target.value })} disabled={disabled} />
                </div>
            </div>
        </div>
    );
}

function PackageForm({ form, setForm, suppliers, onRateSelect, disabled }) {
    return (
        <div className="space-y-4">
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
                <div>
                    <label className={labelClass}>Estado *</label>
                    <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={(event) => setForm({ ...form, workflowStatus: event.target.value })} required disabled={disabled}>
                        <option value="Solicitado">Solicitado</option>
                        <option value="Confirmado">Confirmado</option>
                        <option value="Cancelado">Cancelado</option>
                    </select>
                </div>
            </div>

            <RateSelector serviceType={form.serviceType || "Paquete"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />

            <div className="grid grid-cols-1 gap-4">
                <div>
                    <label className={labelClass}>Nombre del Paquete</label>
                    <input className={inputClass} value={form.packageName || ""} onChange={(event) => setForm({ ...form, packageName: event.target.value })} disabled={disabled} />
                </div>
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
            <div className="rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm text-slate-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300">
                Este servicio es generico/legacy. El tipo se usa solo como etiqueta visual; la edicion se guarda por el endpoint generico.
            </div>

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Tipo visual *</label>
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
    const margin = (form.salePrice || 0) - (form.netCost || 0);

    return (
        <div className="space-y-4 rounded-xl border border-slate-200 bg-gradient-to-r from-slate-50 to-slate-100 p-4 dark:border-slate-700 dark:from-slate-800 dark:to-slate-800/50">
            <div className="flex items-center justify-between">
                <h4 className="flex items-center gap-2 text-xs font-bold uppercase text-slate-500 dark:text-slate-400">
                    <DollarSign className="h-4 w-4" /> Valores Economicos
                </h4>
                {commissionPercent > 0 && !disabled ? (
                    <button
                        type="button"
                        onClick={onRecalculate}
                        className="flex items-center gap-1 rounded-lg bg-indigo-100 px-3 py-1.5 text-xs text-indigo-700 transition-colors hover:bg-indigo-200 dark:bg-indigo-900/50 dark:text-indigo-300 dark:hover:bg-indigo-900"
                    >
                        <Calculator className="h-3 w-3" /> Aplicar {commissionPercent}%
                    </button>
                ) : null}
            </div>

            <div className="grid grid-cols-3 gap-4">
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
                <div>
                    <label className={labelClass}>Ganancia</label>
                    <div className={`rounded-xl p-2.5 text-center font-bold ${margin >= 0 ? "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400" : "bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400"}`}>
                        {formatMoney(margin)}
                    </div>
                </div>
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
    const isLocked = reservaStatus === "Operativo" || reservaStatus === "Cerrado";
    const showPricingForm = true;

    const serviceTabClassMap = {
        sky: "border-sky-500 text-sky-600 bg-white dark:bg-slate-900",
        amber: "border-amber-500 text-amber-600 bg-white dark:bg-slate-900",
        emerald: "border-emerald-500 text-emerald-600 bg-white dark:bg-slate-900",
        violet: "border-violet-500 text-violet-600 bg-white dark:bg-slate-900",
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
            const formattedForm = {
                ...serviceToEdit,
                serviceType: nextServiceType,
                rateId: serviceToEdit.ratePublicId?.toString() || serviceToEdit.rateId?.toString() || "",
                supplierId: serviceToEdit.supplierPublicId?.toString() || serviceToEdit.supplierId?.toString() || "",
                departureDate: formatDateForInput(isGenericEdit ? serviceToEdit.departureDate || serviceToEdit.date : serviceToEdit.departureTime),
                arrivalDate: formatDateForInput(serviceToEdit.arrivalTime),
                checkIn: formatDateForInput(serviceToEdit.checkIn),
                checkOut: formatDateForInput(serviceToEdit.checkOut),
                startDate: formatDateForInput(serviceToEdit.startDate),
                endDate: formatDateForInput(serviceToEdit.endDate),
                pickupDate: formatDateForInput(serviceToEdit.pickupDateTime),
                pickupTime: serviceToEdit.pickupDateTime ? new Date(serviceToEdit.pickupDateTime).toLocaleTimeString("en-GB").slice(0, 5) : "",
                returnDate: formatDateForInput(isGenericEdit ? serviceToEdit.returnDate : serviceToEdit.returnDateTime),
                returnTime: serviceToEdit.returnDateTime ? new Date(serviceToEdit.returnDateTime).toLocaleTimeString("en-GB").slice(0, 5) : "",
                roomingAssignments: serviceToEdit.roomingAssignmentsJson || serviceToEdit.roomingAssignments || "",
                workflowStatus: serviceToEdit.workflowStatus || "Solicitado"
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
                payload.departureTime = toIsoDate(form.departureDate, "salida");
                payload.arrivalTime = toIsoDate(form.arrivalDate, "regreso");
            } else if (!isGenericEdit && serviceType === "Hotel") {
                if (!form.rateId && !isLegacyHotelEdit) {
                    throw new Error("Selecciona un hotel y una variante antes de guardar.");
                }
                payload.checkIn = toIsoDate(form.checkIn, "check-in");
                payload.checkOut = toIsoDate(form.checkOut, "check-out");
            } else if (!isGenericEdit && serviceType === "Traslado") {
                const pickupDateTime = form.pickupTime ? `${form.pickupDate}T${form.pickupTime}` : form.pickupDate;
                payload.pickupDateTime = toIsoDate(pickupDateTime, "pick-up");
            } else if (!isGenericEdit && serviceType === "Paquete") {
                payload.startDate = toIsoDate(form.startDate, "inicio");
                payload.endDate = toIsoDate(form.endDate, "fin");
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

                {isGenericEdit ? (
                    <div className="border-b border-slate-200 bg-slate-50 px-4 py-3 text-sm text-slate-600 dark:border-slate-700 dark:bg-slate-800/50 dark:text-slate-300">
                        Editando servicio generico con etiqueta visual <b>{serviceType}</b>. No se usaran endpoints de hotel/vuelo/traslado/paquete.
                    </div>
                ) : (
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
                )}

                <form onSubmit={handleSubmit} className="max-h-[78vh] space-y-4 overflow-y-auto p-4">
                    {isLocked ? (
                        <div className="mb-4 flex items-center gap-3 rounded-xl border border-slate-200 bg-slate-100 p-3 text-sm text-slate-600 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-400">
                            <AlertCircle className="h-5 w-5" />
                            <p>Reserva en estado <b>{reservaStatus}</b>. La edicion economica queda bloqueada.</p>
                        </div>
                    ) : null}

                    {isGenericEdit ? <GenericServiceForm form={form} setForm={setForm} suppliers={sortedSuppliers} disabled={isLocked} /> : null}
                    {!isGenericEdit && serviceType === "Aereo" ? <FlightForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} /> : null}
                    {!isGenericEdit && serviceType === "Hotel" ? (
                        <HotelForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} reservaPax={reservaPax} />
                    ) : null}
                    {!isGenericEdit && serviceType === "Traslado" ? <TransferForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} /> : null}
                    {!isGenericEdit && serviceType === "Paquete" ? <PackageForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} /> : null}

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

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

function LegacyHotelForm({ form, setForm, suppliers, onRateSelect, disabled, reservaPax }) {
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

            <RateSelector serviceType={form.serviceType || "Hotel"} supplierId={form.supplierId} onSelect={onRateSelect} disabled={disabled} />

            <div className="grid grid-cols-2 gap-4">
                <div>
                    <label className={labelClass}>Tipo Habitacion *</label>
                    <select className={inputClass} value={form.roomType || ""} onChange={(event) => setForm({ ...form, roomType: event.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar...</option>
                        <option value="Single">Single</option>
                        <option value="Doble">Doble</option>
                        <option value="Triple">Triple</option>
                        <option value="Cuadruple">Cuadruple</option>
                        <option value="Suite">Suite</option>
                    </select>
                </div>
                <div>
                    <label className={labelClass}>Regimen *</label>
                    <select className={inputClass} value={form.mealPlan || ""} onChange={(event) => setForm({ ...form, mealPlan: event.target.value })} required disabled={disabled}>
                        <option value="">Seleccionar...</option>
                        <option value="Solo Alojamiento">Solo Alojamiento</option>
                        <option value="Desayuno">Desayuno</option>
                        <option value="Media Pension">Media Pension</option>
                        <option value="Pension Completa">Pension Completa</option>
                        <option value="All Inclusive">All Inclusive</option>
                    </select>
                </div>
            </div>

            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                <div>
                    <label className={labelClass}>Nombre Hotel</label>
                    <input className={inputClass} value={form.hotelName || ""} onChange={(event) => setForm({ ...form, hotelName: event.target.value })} disabled={disabled} />
                </div>
                <div className="grid grid-cols-2 gap-2">
                    <div>
                        <label className={labelClass}>Check-In</label>
                        <input type="date" className={inputClass} value={form.checkIn || ""} onChange={(event) => setForm({ ...form, checkIn: event.target.value })} disabled={disabled} />
                    </div>
                    <div>
                        <label className={labelClass}>Check-Out</label>
                        <input type="date" className={inputClass} value={form.checkOut || ""} onChange={(event) => setForm({ ...form, checkOut: event.target.value })} disabled={disabled} />
                    </div>
                </div>
            </div>

            <div className="grid grid-cols-3 gap-4">
                <div>
                    <label className={labelClass}>Habitaciones</label>
                    <input type="number" className={inputClass} value={form.rooms || 1} onChange={(event) => setForm({ ...form, rooms: parseInt(event.target.value, 10) || 1 })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Adultos</label>
                    <input type="number" className={inputClass} value={form.adults || 2} onChange={(event) => setForm({ ...form, adults: parseInt(event.target.value, 10) || 1 })} disabled={disabled} />
                </div>
                <div>
                    <label className={labelClass}>Menores</label>
                    <input type="number" className={inputClass} value={form.children || 0} onChange={(event) => setForm({ ...form, children: parseInt(event.target.value, 10) || 0 })} disabled={disabled} />
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

function HotelExplorerForm({ form, setForm, disabled, isEditingLinkedHotel, reservaPax }) {
    const [hotelGroups, setHotelGroups] = useState([]);
    const [hotelSearch, setHotelSearch] = useState("");
    const [loadingHotels, setLoadingHotels] = useState(false);
    const [hotelError, setHotelError] = useState("");
    const [selectedGroupKey, setSelectedGroupKey] = useState("");
    const nights = calculateNights(form.checkIn, form.checkOut);

    useEffect(() => {
        if (isEditingLinkedHotel) {
            return undefined;
        }

        let cancelled = false;
        const timeoutId = setTimeout(async () => {
            try {
                setLoadingHotels(true);
                setHotelError("");
                const params = new URLSearchParams({
                    page: "1",
                    pageSize: "100",
                    activeOnly: "true",
                });
                if (hotelSearch.trim()) params.set("search", hotelSearch.trim());

                const response = await api.get(`/rates/hotels?${params.toString()}`);
                if (cancelled) return;

                const items = response?.items || [];
                setHotelGroups(items);

                if (selectedGroupKey && items.some((group) => group.groupKey === selectedGroupKey)) {
                    return;
                }

                const matchingGroup = items.find((group) =>
                    group.hotelName === form.hotelName &&
                    (group.city || "") === (form.city || "") &&
                    String(group.supplierPublicId || "") === String(form.supplierId || "")
                );

                setSelectedGroupKey(matchingGroup?.groupKey || items[0]?.groupKey || "");
            } catch (error) {
                if (!cancelled) {
                    setHotelGroups([]);
                    setHotelError(error.message || "No se pudo cargar el selector de hoteles.");
                }
            } finally {
                if (!cancelled) setLoadingHotels(false);
            }
        }, 250);

        return () => {
            cancelled = true;
            clearTimeout(timeoutId);
        };
    }, [form.city, form.hotelName, form.supplierId, hotelSearch, isEditingLinkedHotel, selectedGroupKey]);

    const selectedGroup = useMemo(
        () => hotelGroups.find((group) => group.groupKey === selectedGroupKey) || null,
        [hotelGroups, selectedGroupKey]
    );

    const selectedRate = useMemo(() => {
        const normalizedRateId = String(form.rateId || "");
        if (!normalizedRateId) {
            return null;
        }

        const allItems = hotelGroups.flatMap((group) => group.items || []);
        return allItems.find((item) => String(item.publicId || "") === normalizedRateId) || null;
    }, [form.rateId, hotelGroups]);

    const visibleVariants = useMemo(() => {
        const items = selectedGroup?.items || [];
        return items.filter((item) => !item.validTo || new Date(item.validTo) >= new Date());
    }, [selectedGroup]);

    const handleVariantSelect = (rate) => {
        setForm((prev) => {
            const qty = Math.max(calculateNights(prev.checkIn, prev.checkOut) || 1, 1) * Math.max(prev.rooms || 1, 1);
            return {
                ...prev,
                rateId: rate.publicId?.toString() || "",
                supplierId: rate.supplierPublicId?.toString() || prev.supplierId || "",
                unitNetCost: Number(rate.netCost) || 0,
                unitSalePrice: Number(rate.salePrice) || 0,
                netCost: Math.round((Number(rate.netCost) || 0) * qty * 100) / 100,
                salePrice: Math.round((Number(rate.salePrice) || 0) * qty * 100) / 100,
                hotelName: rate.hotelName || rate.productName || prev.hotelName || "",
                city: rate.city || prev.city || "",
                starRating: rate.starRating ?? prev.starRating ?? null,
                roomType: rate.roomType || prev.roomType || "",
                mealPlan: rate.mealPlan || prev.mealPlan || "",
                description: rate.description || prev.description || "",
            };
        });
    };

    const clearVariantSelection = () => {
        setForm((prev) => ({
            ...prev,
            rateId: "",
            unitNetCost: 0,
            unitSalePrice: 0,
            netCost: 0,
            salePrice: 0,
            roomType: "",
            mealPlan: "",
        }));
    };

    const selectedHotelName = selectedRate?.hotelName || form.hotelName || selectedGroup?.hotelName || "Sin seleccionar";
    const selectedSupplierName = selectedRate?.supplierName || form.supplierName || selectedGroup?.supplierName || "Sin proveedor";
    const selectedCity = selectedRate?.city || form.city || selectedGroup?.city || "Sin ciudad";
    const selectedStarRating = selectedRate?.starRating ?? form.starRating ?? selectedGroup?.starRating ?? null;

    return (
        <div className="space-y-5">
            <div className="rounded-2xl border border-indigo-100 bg-indigo-50/70 p-4 text-sm text-indigo-700 dark:border-indigo-900/40 dark:bg-indigo-950/20 dark:text-indigo-300">
                El hotel se toma desde el tarifario y queda congelado dentro de la reserva. Si el tarifario cambia despues, esta reserva no se actualiza sola.
            </div>

            <div className="grid gap-4 xl:grid-cols-[0.95fr,1.15fr,0.9fr]">
                <div className={`${panelClass} space-y-4`}>
                    <div>
                        <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Paso 1</div>
                        <h4 className="mt-1 flex items-center gap-2 text-sm font-bold text-slate-900 dark:text-white">
                            <Building2 className="h-4 w-4 text-indigo-500" />
                            Seleccionar Hotel
                        </h4>
                        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                            Busca por hotel, ciudad o proveedor. La seleccion de hotel define la base de la reserva.
                        </p>
                    </div>

                    {isEditingLinkedHotel ? (
                        <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-700 dark:bg-slate-800/60">
                            <div className="text-xs font-black uppercase tracking-widest text-slate-400">Hotel tomado</div>
                            <div className="mt-2 text-base font-bold text-slate-900 dark:text-white">{selectedHotelName}</div>
                            <div className="mt-1 flex items-center gap-2 text-sm text-slate-500 dark:text-slate-400">
                                <MapPin className="h-4 w-4" />
                                {selectedCity}
                            </div>
                            <div className="mt-2 text-xs text-slate-500 dark:text-slate-400">
                                Proveedor: <span className="font-semibold text-slate-700 dark:text-slate-200">{selectedSupplierName}</span>
                            </div>
                        </div>
                    ) : (
                        <>
                            <div>
                                <label className={labelClass}>Buscar hotel</label>
                                <div className="relative">
                                    <Search className="absolute left-3 top-2.5 h-4 w-4 text-slate-400" />
                                    <input
                                        type="text"
                                        className={`${inputClass} pl-10`}
                                        placeholder="Nombre, ciudad o proveedor"
                                        value={hotelSearch}
                                        onChange={(event) => setHotelSearch(event.target.value)}
                                        disabled={disabled}
                                    />
                                </div>
                            </div>

                            <div className="max-h-[28rem] space-y-2 overflow-y-auto pr-1">
                                {loadingHotels ? (
                                    <div className="rounded-2xl border border-dashed border-slate-200 p-4 text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
                                        Cargando hoteles disponibles...
                                    </div>
                                ) : hotelError ? (
                                    <div className="rounded-2xl border border-rose-200 bg-rose-50 p-4 text-sm text-rose-700 dark:border-rose-900/40 dark:bg-rose-950/20 dark:text-rose-300">
                                        {hotelError}
                                    </div>
                                ) : hotelGroups.length === 0 ? (
                                    <div className="rounded-2xl border border-dashed border-slate-200 p-4 text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
                                        No encontramos hoteles para esa busqueda.
                                    </div>
                                ) : (
                                    hotelGroups.map((group) => {
                                        const isSelected = group.groupKey === selectedGroupKey;
                                        return (
                                            <button
                                                key={group.groupKey}
                                                type="button"
                                                onClick={() => setSelectedGroupKey(group.groupKey)}
                                                disabled={disabled}
                                                className={`w-full rounded-2xl border p-3 text-left transition-all ${
                                                    isSelected
                                                        ? "border-indigo-500 bg-indigo-50 shadow-sm dark:border-indigo-400 dark:bg-indigo-950/20"
                                                        : "border-slate-200 bg-slate-50 hover:border-slate-300 hover:bg-white dark:border-slate-700 dark:bg-slate-800/50 dark:hover:border-slate-600"
                                                }`}
                                            >
                                                <div className="flex items-start justify-between gap-3">
                                                    <div>
                                                        <div className="font-bold text-slate-900 dark:text-white">{group.hotelName}</div>
                                                        <div className="mt-1 flex flex-wrap items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
                                                            <span className="inline-flex items-center gap-1">
                                                                <MapPin className="h-3.5 w-3.5" />
                                                                {group.city || "Sin ciudad"}
                                                            </span>
                                                            <span>{group.supplierName || "Sin proveedor"}</span>
                                                        </div>
                                                    </div>
                                                    <div className="text-right">
                                                        <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Desde</div>
                                                        <div className="text-sm font-bold text-slate-900 dark:text-white">{formatMoney(group.fromPrice)}</div>
                                                    </div>
                                                </div>
                                                <div className="mt-3 flex flex-wrap gap-2 text-[11px]">
                                                    <span className="rounded-full bg-slate-100 px-2 py-1 font-semibold text-slate-600 dark:bg-slate-700 dark:text-slate-200">
                                                        {group.roomCount} variante{group.roomCount === 1 ? "" : "s"}
                                                    </span>
                                                    {group.starRating ? (
                                                        <span className="rounded-full bg-amber-100 px-2 py-1 font-semibold text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
                                                            {group.starRating} estrellas
                                                        </span>
                                                    ) : null}
                                                </div>
                                            </button>
                                        );
                                    })
                                )}
                            </div>
                        </>
                    )}
                </div>

                <div className={`${panelClass} space-y-4`}>
                    <div>
                        <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Paso 2</div>
                        <h4 className="mt-1 flex items-center gap-2 text-sm font-bold text-slate-900 dark:text-white">
                            <BedDouble className="h-4 w-4 text-indigo-500" />
                            Elegir Variante
                        </h4>
                        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                            Cada opcion crea una linea hotelera propia dentro de la reserva.
                        </p>
                    </div>

                    {isEditingLinkedHotel ? (
                        <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-700 dark:bg-slate-800/60">
                            <div className="flex items-center justify-between gap-3">
                                <div>
                                    <div className="text-xs font-black uppercase tracking-widest text-slate-400">Variante tomada</div>
                                    <div className="mt-2 text-base font-bold text-slate-900 dark:text-white">
                                        {form.roomType || "Habitacion"} {form.mealPlan ? `- ${form.mealPlan}` : ""}
                                    </div>
                                </div>
                                <CheckCircle2 className="h-6 w-6 text-emerald-500" />
                            </div>
                            <div className="mt-3 grid grid-cols-1 gap-2 text-xs text-slate-500 dark:text-slate-400">
                                <div>Tarifa tomada el dia de la reserva.</div>
                                <div>{selectedStarRating ? `${selectedStarRating} estrellas` : "Categoria sin definir"}</div>
                            </div>
                        </div>
                    ) : !selectedGroup ? (
                        <div className="rounded-2xl border border-dashed border-slate-200 p-5 text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
                            Elige un hotel a la izquierda para ver sus variantes disponibles.
                        </div>
                    ) : visibleVariants.length === 0 ? (
                        <div className="rounded-2xl border border-dashed border-slate-200 p-5 text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
                            Este hotel no tiene variantes vigentes para reservar.
                        </div>
                    ) : (
                        <div className="max-h-[28rem] space-y-3 overflow-y-auto pr-1">
                            {visibleVariants.map((rate) => {
                                const isSelected = String(rate.publicId || "") === String(form.rateId || "");
                                const validRange = rate.validFrom || rate.validTo
                                    ? `${formatShortDate(rate.validFrom)} - ${formatShortDate(rate.validTo)}`
                                    : "Sin vigencia cargada";

                                return (
                                    <div
                                        key={rate.publicId}
                                        className={`rounded-2xl border p-4 transition-all ${
                                            isSelected
                                                ? "border-emerald-500 bg-emerald-50 shadow-sm dark:border-emerald-400 dark:bg-emerald-950/20"
                                                : "border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/50"
                                        }`}
                                    >
                                        <div className="flex flex-wrap items-start justify-between gap-3">
                                            <div>
                                                <div className="text-base font-bold text-slate-900 dark:text-white">
                                                    {rate.roomType || "Habitacion"} {rate.roomCategory ? `- ${rate.roomCategory}` : ""}
                                                </div>
                                                <div className="mt-1 text-sm text-slate-500 dark:text-slate-400">
                                                    {rate.mealPlan || "Regimen sin especificar"}
                                                </div>
                                            </div>
                                            <div className="text-right">
                                                <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Venta unitaria</div>
                                                <div className="text-base font-bold text-slate-900 dark:text-white">{formatMoney(rate.salePrice)}</div>
                                            </div>
                                        </div>

                                        <div className="mt-3 flex flex-wrap gap-2 text-[11px]">
                                            <span className="rounded-full bg-slate-100 px-2 py-1 font-semibold text-slate-600 dark:bg-slate-700 dark:text-slate-200">
                                                Neto {formatMoney(rate.netCost)}
                                            </span>
                                            <span className="rounded-full bg-slate-100 px-2 py-1 font-semibold text-slate-600 dark:bg-slate-700 dark:text-slate-200">
                                                Vigencia {validRange}
                                            </span>
                                            {selectedGroup.starRating ? (
                                                <span className="rounded-full bg-amber-100 px-2 py-1 font-semibold text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
                                                    {selectedGroup.starRating} estrellas
                                                </span>
                                            ) : null}
                                        </div>

                                        {rate.roomFeatures ? (
                                            <div className="mt-3 text-xs text-slate-500 dark:text-slate-400">
                                                {rate.roomFeatures}
                                            </div>
                                        ) : null}

                                        <div className="mt-4 flex justify-end">
                                            <button
                                                type="button"
                                                onClick={() => handleVariantSelect(rate)}
                                                disabled={disabled}
                                                className={`rounded-xl px-4 py-2 text-sm font-bold transition-colors ${
                                                    isSelected
                                                        ? "bg-emerald-600 text-white"
                                                        : "bg-slate-900 text-white hover:bg-slate-800 dark:bg-slate-100 dark:text-slate-900"
                                                }`}
                                            >
                                                {isSelected ? "Opcion elegida" : "Usar esta opcion"}
                                            </button>
                                        </div>
                                    </div>
                                );
                            })}
                        </div>
                    )}
                </div>

                <div className={`${panelClass} space-y-4`}>
                    <div>
                        <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Paso 3</div>
                        <h4 className="mt-1 flex items-center gap-2 text-sm font-bold text-slate-900 dark:text-white">
                            <CalendarDays className="h-4 w-4 text-indigo-500" />
                            Resumen de estadia
                        </h4>
                        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                            Completa solo los datos operativos. El nombre del hotel, habitacion y regimen ya quedan definidos por la opcion elegida.
                        </p>
                    </div>

                    <div className="rounded-2xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-700 dark:bg-slate-800/60">
                        <div className="text-xs font-black uppercase tracking-widest text-slate-400">Hotel elegido</div>
                        <div className="mt-2 text-lg font-bold text-slate-900 dark:text-white">{selectedHotelName}</div>
                        <div className="mt-1 flex items-center gap-2 text-sm text-slate-500 dark:text-slate-400">
                            <MapPin className="h-4 w-4" />
                            {selectedCity}
                        </div>
                        <div className="mt-3 grid grid-cols-1 gap-2 text-sm text-slate-600 dark:text-slate-300">
                            <div className="flex items-center justify-between gap-3">
                                <span className="text-slate-500 dark:text-slate-400">Variante</span>
                                <span className="text-right font-semibold">
                                    {form.roomType || "Pendiente"} {form.mealPlan ? `- ${form.mealPlan}` : ""}
                                </span>
                            </div>
                            <div className="flex items-center justify-between gap-3">
                                <span className="text-slate-500 dark:text-slate-400">Proveedor</span>
                                <span className="text-right font-semibold">{selectedSupplierName}</span>
                            </div>
                        </div>
                        <div className="mt-3 rounded-xl border border-dashed border-slate-200 px-3 py-2 text-xs text-slate-500 dark:border-slate-700 dark:text-slate-400">
                            Basado en tarifario. La tarifa queda tomada dentro de la reserva y no se resincroniza automaticamente.
                        </div>
                    </div>

                    <div className="grid grid-cols-2 gap-3">
                        <div>
                            <label className={labelClass}>Check-In</label>
                            <input type="date" className={inputClass} value={form.checkIn || ""} onChange={(event) => setForm({ ...form, checkIn: event.target.value })} disabled={disabled} />
                        </div>
                        <div>
                            <label className={labelClass}>Check-Out</label>
                            <input type="date" className={inputClass} value={form.checkOut || ""} onChange={(event) => setForm({ ...form, checkOut: event.target.value })} disabled={disabled} />
                        </div>
                    </div>

                    <div className="rounded-2xl border border-slate-200 bg-slate-50 px-4 py-3 dark:border-slate-700 dark:bg-slate-800/60">
                        <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Noches</div>
                        <div className="mt-1 text-2xl font-black text-slate-900 dark:text-white">{nights}</div>
                        <div className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                            Entre {form.checkIn ? formatShortDate(form.checkIn) : "check-in"} y {form.checkOut ? formatShortDate(form.checkOut) : "check-out"}
                        </div>
                    </div>

                    <div className="grid grid-cols-3 gap-3">
                        <div>
                            <label className={labelClass}>Habitaciones</label>
                            <input type="number" min="1" className={inputClass} value={form.rooms || 1} onChange={(event) => setForm({ ...form, rooms: parseInt(event.target.value, 10) || 1 })} disabled={disabled} />
                        </div>
                        <div>
                            <label className={labelClass}>Adultos</label>
                            <input type="number" min="1" className={inputClass} value={form.adults || 2} onChange={(event) => setForm({ ...form, adults: parseInt(event.target.value, 10) || 1 })} disabled={disabled} />
                        </div>
                        <div>
                            <label className={labelClass}>Menores</label>
                            <input type="number" min="0" className={inputClass} value={form.children || 0} onChange={(event) => setForm({ ...form, children: parseInt(event.target.value, 10) || 0 })} disabled={disabled} />
                        </div>
                    </div>

                    <div className="pt-2">
                        <RoomingPlanner
                            rooms={form.rooms || 1}
                            reservaPax={reservaPax}
                            value={form.roomingAssignments}
                            onChange={(val) => setForm({ ...form, roomingAssignments: val })}
                        />
                    </div>

                    <div className="grid grid-cols-1 gap-3">
                        <div>
                            <label className={labelClass}>Estado operativo</label>
                            <select className={inputClass} value={form.workflowStatus || "Solicitado"} onChange={(event) => setForm({ ...form, workflowStatus: event.target.value })} disabled={disabled}>
                                <option value="Solicitado">Solicitado</option>
                                <option value="Confirmado">Confirmado</option>
                                <option value="Cancelado">Cancelado</option>
                            </select>
                        </div>
                        <div>
                            <label className={labelClass}>Confirmacion</label>
                            <input className={inputClass} value={form.confirmationNumber || ""} onChange={(event) => setForm({ ...form, confirmationNumber: event.target.value })} placeholder="Codigo o locator" disabled={disabled} />
                        </div>
                    </div>

                    <div className="grid grid-cols-2 gap-3 rounded-2xl border border-slate-200 bg-slate-50 p-4 dark:border-slate-700 dark:bg-slate-800/60">
                        <div>
                            <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Total Neto</div>
                            <div className="mt-1 text-lg font-bold text-slate-900 dark:text-white">{formatMoney(form.netCost)}</div>
                        </div>
                        <div>
                            <div className="text-[11px] font-black uppercase tracking-widest text-slate-400">Total Venta</div>
                            <div className="mt-1 text-lg font-bold text-slate-900 dark:text-white">{formatMoney(form.salePrice)}</div>
                        </div>
                        <div className="col-span-2 flex items-center gap-2 text-xs text-slate-500 dark:text-slate-400">
                            <Users className="h-4 w-4" />
                            {form.adults || 0} adulto(s) y {form.children || 0} menor(es)
                        </div>
                    </div>

                    {!isEditingLinkedHotel ? (
                        <button
                            type="button"
                            onClick={clearVariantSelection}
                            disabled={disabled || !form.rateId}
                            className="w-full rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-semibold text-slate-700 transition-colors hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                        >
                            Agregar otra habitacion/variante del mismo hotel
                        </button>
                    ) : null}
                </div>
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

function PricingForm({ form, setForm, commissionPercent, onRecalculate, disabled }) {
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
                        <input type="number" step="0.01" className={`${inputClass} pl-6`} value={form.netCost || 0} onChange={(event) => setForm({ ...form, netCost: parseFloat(event.target.value) || 0 })} required disabled={disabled} />
                    </div>
                </div>
                <div>
                    <label className={labelClass}>Precio Venta *</label>
                    <div className="relative">
                        <span className="absolute left-3 top-2.5 text-slate-500">$</span>
                        <input type="number" step="0.01" className={`${inputClass} pl-6`} value={form.salePrice || 0} onChange={(event) => setForm({ ...form, salePrice: parseFloat(event.target.value) || 0 })} required disabled={disabled} />
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
        unitNetCost: 0,
        unitSalePrice: 0,
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

    const isGenericEdit = serviceToEdit?.recordKind === SERVICE_RECORD_KIND.GENERIC;
    const isLocked = reservaStatus === "Operativo" || reservaStatus === "Cerrado";
    const isLegacyHotelEdit = !isGenericEdit && serviceType === "Hotel" && Boolean(serviceToEdit) && !serviceToEdit?.ratePublicId;
    const isLinkedHotelEdit = !isGenericEdit && serviceType === "Hotel" && Boolean(serviceToEdit?.ratePublicId);
    const showHotelExplorer = !isGenericEdit && serviceType === "Hotel" && !isLegacyHotelEdit;
    const showPricingForm = !showHotelExplorer;

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
            const nights = calculateNights(form.checkIn, form.checkOut);
            const qty = (nights || 1) * (form.rooms || 1);
            if (form.unitNetCost > 0 || form.unitSalePrice > 0) {
                setForm((prev) => ({
                    ...prev,
                    netCost: Math.round(prev.unitNetCost * qty * 100) / 100,
                    salePrice: Math.round(prev.unitSalePrice * qty * 100) / 100
                }));
            }
        }
    }, [form.checkIn, form.checkOut, form.rooms, form.unitNetCost, form.unitSalePrice, serviceType]);

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
            }

            setForm(formattedForm);
            return;
        }

        setServiceType(initialServiceType || "Aereo");
        setForm({
            supplierId: "",
            rateId: "",
            netCost: 0,
            salePrice: 0,
            unitNetCost: 0,
            unitSalePrice: 0,
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
        setForm((prev) => {
            const newForm = {
                ...prev,
                rateId: rate.publicId?.toString() || "",
                unitNetCost: rate.netCost,
                unitSalePrice: rate.salePrice,
                description: rate.description || prev.description || ""
            };

            if (serviceType !== "Hotel") {
                newForm.netCost = rate.netCost;
                newForm.salePrice = rate.salePrice;
            }

            if (serviceType === "Hotel") {
                newForm.hotelName = rate.hotelName || rate.productName;
                newForm.city = rate.city || prev.city;
                if (rate.roomType) newForm.roomType = rate.roomType;
                if (rate.mealPlan) newForm.mealPlan = rate.mealPlan;
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
        const cost = form.netCost || 0;
        const margin = cost * (commissionPercent / 100);
        setForm((prev) => ({ ...prev, salePrice: Math.round((cost + margin) * 100) / 100 }));
    };

    const handleSubmit = async (event) => {
        event.preventDefault();
        setLoading(true);
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
            const serviceToValidate = serviceToEdit || {
                ...(persistedService || {}),
                recordKind: getRecordKindForServiceType(serviceType),
            };

            let updatedReserva = null;
            let serviceCollectionError = null;
            try {
                const refreshResult = await onSuccess?.({
                    service: persistedService,
                    serviceType,
                    action: method,
                    showLoading: false,
                });
                updatedReserva = Object.prototype.hasOwnProperty.call(refreshResult || {}, "reserva")
                    ? refreshResult?.reserva || null
                    : refreshResult || null;
                serviceCollectionError = refreshResult?.serviceCollectionError || null;
            } catch (refreshError) {
                console.error("Error refreshing reserva after saving service", refreshError);
                showError("Servicio guardado, pero no se pudo actualizar la lista.");
                return;
            }

            if (serviceCollectionError) {
                showSuccess("Servicio guardado. La reserva se actualizo, pero esa lista no se pudo refrescar en este momento.");
                onClose();
                return;
            }

            if (!updatedReserva) {
                showSuccess("Servicio guardado");
                onClose();
                return;
            }

            if (!findNormalizedService(updatedReserva, serviceToValidate)) {
                showError("Servicio guardado, pero no se ve reflejado en la reserva actualizada.");
                return;
            }

            showSuccess("Servicio guardado");
            onClose();
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
    const modalWidthClass = showHotelExplorer ? "max-w-6xl" : "max-w-2xl";

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4 backdrop-blur-sm">
            <div className={`w-full overflow-hidden rounded-2xl bg-white shadow-2xl dark:bg-slate-900 ${modalWidthClass}`}>
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
                    {!isGenericEdit && serviceType === "Hotel" && showHotelExplorer ? (
                        <HotelExplorerForm form={form} setForm={setForm} disabled={isLocked} isEditingLinkedHotel={isLinkedHotelEdit} reservaPax={reservaPax} />
                    ) : null}
                    {!isGenericEdit && serviceType === "Hotel" && isLegacyHotelEdit ? (
                        <LegacyHotelForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} reservaPax={reservaPax} />
                    ) : null}
                    {!isGenericEdit && serviceType === "Traslado" ? <TransferForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} /> : null}
                    {!isGenericEdit && serviceType === "Paquete" ? <PackageForm form={form} setForm={setForm} suppliers={sortedSuppliers} onRateSelect={handleRateSelect} disabled={isLocked} /> : null}

                    {showPricingForm ? (
                        <PricingForm form={form} setForm={setForm} commissionPercent={commissionPercent} onRecalculate={applyCommission} disabled={isLocked} />
                    ) : null}

                    <div className="flex justify-end gap-3 border-t border-slate-200 pt-4 dark:border-slate-700">
                        <button type="button" onClick={onClose} className="rounded-xl px-5 py-2.5 text-sm text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">
                            Cancelar
                        </button>
                        <button type="submit" disabled={loading} className="rounded-xl bg-indigo-600 px-5 py-2.5 text-sm text-white disabled:opacity-50">
                            {loading ? "Guardando..." : "Guardar Servicio"}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}

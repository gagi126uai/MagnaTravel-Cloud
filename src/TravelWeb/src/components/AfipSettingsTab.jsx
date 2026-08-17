import { useState, useEffect } from "react";
import { api } from "../api";
import { showSuccess, showError } from "../alerts";
import { getApiErrorMessage } from "../lib/errors";
import { Upload, CheckCircle2, AlertCircle, Key, FileKey, ShieldCheck, RefreshCw } from "lucide-react";
import { Button } from "./ui/button";

export default function AfipSettingsTab() {
    const [loading, setLoading] = useState(false);
    const [status, setStatus] = useState("Desconocido");
    const [checkingStatus, setCheckingStatus] = useState(false);
    const [form, setForm] = useState({
        cuit: "",
        puntoDeVenta: 1,
        isProduction: false,
        taxCondition: "Responsable Inscripto",
        certificatePassword: "",
        prodCertificatePassword: ""
    });
    const [certificateFile, setCertificateFile] = useState(null);
    const [certificateName, setCertificateName] = useState(null);
    const [prodCertificateFile, setProdCertificateFile] = useState(null);
    const [prodCertificateName, setProdCertificateName] = useState(null);

    useEffect(() => {
        loadSettings();
        checkStatus();
    }, []);

    const loadSettings = async () => {
        setLoading(true);
        try {
            const data = await api.get("/afip/settings");
            if (data) {
                setForm({
                    cuit: data.cuit || "",
                    puntoDeVenta: data.puntoDeVenta || 1,
                    isProduction: data.isProduction || false,
                    taxCondition: data.taxCondition || "Responsable Inscripto",
                    certificatePassword: "", // Don't show password
                    prodCertificatePassword: ""
                });
                if (data.hasCertificate) {
                    setCertificateName(data.certificateFileName || "Certificado homologación cargado");
                }
                if (data.hasProdCertificate) {
                    setProdCertificateName(data.prodCertificateFileName || "Certificado producción cargado");
                }
            }
        } catch (error) {
            console.log("No AFIP settings found.");
        } finally {
            setLoading(false);
        }
    };

    const checkStatus = async () => {
        setCheckingStatus(true);
        try {
            const data = await api.get("/afip/status");
            setStatus(data.status);
        } catch (error) {
            console.error("Status check failed:", error);
            setStatus(getApiErrorMessage(error, "No se pudo verificar la conexión con ARCA."));
        } finally {
            setCheckingStatus(false);
        }
    };

    const handleFileChange = (e, isProd = false) => {
        const file = e.target.files[0];
        if (file) {
            if (isProd) {
                setProdCertificateFile(file);
                setProdCertificateName(file.name);
            } else {
                setCertificateFile(file);
                setCertificateName(file.name);
            }
        }
    };

    const handleSubmit = async (e) => {
        e.preventDefault();
        setLoading(true);

        const formData = new FormData();
        formData.append("Cuit", form.cuit);
        formData.append("PuntoDeVenta", form.puntoDeVenta);
        formData.append("IsProduction", form.isProduction);
        formData.append("TaxCondition", form.taxCondition);
        
        if (form.certificatePassword) {
            formData.append("Password", form.certificatePassword);
        }
        if (certificateFile) {
            formData.append("Certificate", certificateFile);
        }

        if (form.prodCertificatePassword) {
            formData.append("ProdPassword", form.prodCertificatePassword);
        }
        if (prodCertificateFile) {
            formData.append("ProdCertificate", prodCertificateFile);
        }

        try {
            await api.post("/afip/settings", formData);
            showSuccess("Configuración de AFIP guardada.");
            loadSettings(); // Reload to refresh names and status
            checkStatus();
        } catch (error) {
            // El motor puede rechazar con un motivo puntual (CUIT inválido, punto de venta fuera de rango,
            // condición fiscal desconocida): se muestra ESE texto tal cual. Antes se leía
            // error.response.data.message, que es la forma de axios — este proyecto usa fetch y deja el
            // cuerpo de la respuesta en error.payload, que es justo lo que mira getApiErrorMessage.
            showError(getApiErrorMessage(error, "No se pudo guardar la configuración de facturación."));
        } finally {
            setLoading(false);
        }
    };

    return (
        <div className="space-y-6 max-w-5xl">
            {/* Header / Status */}
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4 rounded-[14px] border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900/50">
                <div>
                    <h3 className="text-lg font-semibold text-slate-900 dark:text-white flex items-center gap-2">
                        <ShieldCheck className="h-5 w-5 text-blue-600" />
                        Estado de Conexión
                    </h3>
                    <p className="text-sm text-slate-500 mt-1">Verifica la conexión con los servidores de AFIP en el entorno actual ({form.isProduction ? "Producción" : "Homologación"})</p>
                </div>
                <div className="flex items-center gap-4">
                    <div className={`flex items-center gap-2 px-4 py-2 rounded-[10px] text-sm font-medium ${status?.includes("Online")
                        ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400"
                        : "bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400"
                        }`}>
                        {status?.includes("Online") ? <CheckCircle2 className="h-4 w-4" /> : <AlertCircle className="h-4 w-4" />}
                        {status}
                    </div>
                    <Button
                        type="button"
                        variant="ghost"
                        size="icon"
                        onClick={checkStatus}
                        disabled={checkingStatus}
                        title="Verificar Estado"
                        aria-label="Verificar estado de conexión"
                    >
                        <RefreshCw className={`h-5 w-5 ${checkingStatus ? "animate-spin" : ""}`} aria-hidden="true" />
                    </Button>
                </div>
            </div>

            <form onSubmit={handleSubmit} className="space-y-6">
                {/* Configuración General */}
                <div className="rounded-[14px] border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900/50">
                    <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">Datos Fiscales Generales</h3>
                    <div className="grid grid-cols-1 md:grid-cols-4 gap-6">
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">CUIT Emisor</label>
                            <input
                                type="number"
                                required
                                className="mt-1 block w-full rounded-[10px] border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-primary focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.cuit}
                                onChange={e => setForm({ ...form, cuit: e.target.value })}
                                placeholder="20123456789"
                            />
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Punto de Venta</label>
                            <input
                                type="number"
                                required
                                className="mt-1 block w-full rounded-[10px] border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-primary focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.puntoDeVenta}
                                onChange={e => setForm({ ...form, puntoDeVenta: e.target.value })}
                            />
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Condición Fiscal</label>
                            <select
                                className="mt-1 block w-full rounded-[10px] border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-primary focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.taxCondition}
                                onChange={e => setForm({ ...form, taxCondition: e.target.value })}
                            >
                                <option value="Responsable Inscripto">Responsable Inscripto</option>
                                <option value="Monotributo">Monotributo</option>
                                <option value="Exento">Exento</option>
                            </select>
                        </div>
                        <div className="flex items-center justify-between p-3 rounded-[10px] border border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/50">
                            <div>
                                <span className="block text-xs font-semibold uppercase text-slate-500">Entorno Activo</span>
                                {/* "Homologación" en sky (no azul boleto): necesita distinguirse de un
                                    vistazo de "Producción" (ambar = plata real, factura legal — regla
                                    "pruebas SOLO homologación"). El indigo se retira, pero la señal de
                                    seguridad entre los dos entornos no puede perderse. */}
                                <span className={`text-sm font-bold ${form.isProduction ? "text-amber-600" : "text-sky-600"}`}>
                                    {form.isProduction ? "PRODUCCIÓN" : "HOMOLOGACIÓN"}
                                </span>
                            </div>
                            <label className="relative inline-flex items-center cursor-pointer">
                                <input
                                    type="checkbox"
                                    className="sr-only peer"
                                    checked={form.isProduction}
                                    onChange={e => setForm({ ...form, isProduction: e.target.checked })}
                                />
                                <div className="w-11 h-6 bg-slate-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-ring rounded-full peer dark:bg-slate-700 peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-slate-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all dark:border-gray-600 peer-checked:bg-amber-500"></div>
                            </label>
                        </div>
                    </div>
                </div>

                {/* Certificados */}
                <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
                    {/* Homologación — theme sky (no azul boleto): mismo criterio que el label de
                        "Entorno Activo" de arriba, para que se distinga de un vistazo del panel
                        de Producción (ambar) de al lado. Es una señal de seguridad real (subir el
                        PFX de pruebas al lugar equivocado no es un detalle estetico), no decorativa. */}
                    <div className="rounded-[14px] border border-sky-100 bg-sky-50/30 p-6 dark:border-sky-900/30 dark:bg-sky-900/10">
                        <div className="flex items-center gap-3 mb-4">
                            <div className="p-2 bg-sky-100 dark:bg-sky-900/50 rounded-[10px]">
                                <FileKey className="h-5 w-5 text-sky-600 dark:text-sky-400" />
                            </div>
                            <div>
                                <h4 className="font-bold text-slate-900 dark:text-white">Certificado Homologación</h4>
                                <p className="text-xs text-slate-500">Para pruebas y desarrollo (Testing)</p>
                            </div>
                        </div>

                        <div className="space-y-4">
                            <div className="border-2 border-dashed border-sky-200 dark:border-sky-800 rounded-[10px] p-4 flex flex-col items-center justify-center bg-white dark:bg-slate-900/50 hover:bg-slate-50 transition-colors">
                                <p className="text-xs text-slate-600 dark:text-slate-300 mb-3 text-center line-clamp-1">
                                    {certificateName || "Sin certificado de pruebas"}
                                </p>
                                <label className="cursor-pointer rounded-[10px] bg-sky-600 px-4 py-2 text-xs font-medium text-white hover:bg-sky-500 shadow-sm transition-all active:scale-95">
                                    Cargar PFX Pruebas
                                    <input type="file" accept=".pfx" className="hidden" onChange={(e) => handleFileChange(e, false)} />
                                </label>
                            </div>

                            <div>
                                <label className="block text-xs font-medium text-slate-700 dark:text-slate-300 mb-1">Contraseña PFX Pruebas</label>
                                <div className="relative">
                                    <div className="absolute inset-y-0 left-0 pl-3 flex items-center pointer-events-none">
                                        <Key className="h-3.5 w-3.5 text-slate-400" />
                                    </div>
                                    <input
                                        type="password"
                                        className="block w-full rounded-[10px] border border-slate-200 bg-white pl-9 px-3 py-2 text-sm focus:border-primary focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                        value={form.certificatePassword}
                                        onChange={e => setForm({ ...form, certificatePassword: e.target.value })}
                                        placeholder="Solo para cambiar"
                                    />
                                </div>
                            </div>
                        </div>
                    </div>

                    {/* Producción */}
                    <div className="rounded-[14px] border border-amber-100 bg-amber-50/30 p-6 dark:border-amber-900/30 dark:bg-amber-900/10">
                        <div className="flex items-center gap-3 mb-4">
                            <div className="p-2 bg-amber-100 dark:bg-amber-900/50 rounded-[10px]">
                                <ShieldCheck className="h-5 w-5 text-amber-600 dark:text-amber-400" />
                            </div>
                            <div>
                                <h4 className="font-bold text-slate-900 dark:text-white">Certificado Producción</h4>
                                <p className="text-xs text-slate-500">Para facturación real y legal</p>
                            </div>
                        </div>

                        <div className="space-y-4">
                            <div className="border-2 border-dashed border-amber-200 dark:border-amber-800 rounded-[10px] p-4 flex flex-col items-center justify-center bg-white dark:bg-slate-900/50 hover:bg-slate-100/50 transition-colors">
                                <p className="text-xs text-slate-600 dark:text-slate-300 mb-3 text-center line-clamp-1">
                                    {prodCertificateName || "Sin certificado real"}
                                </p>
                                <label className="cursor-pointer rounded-[10px] bg-amber-600 px-4 py-2 text-xs font-medium text-white hover:bg-amber-500 shadow-sm transition-all active:scale-95">
                                    Cargar PFX Real
                                    <input type="file" accept=".pfx" className="hidden" onChange={(e) => handleFileChange(e, true)} />
                                </label>
                            </div>

                            <div>
                                <label className="block text-xs font-medium text-slate-700 dark:text-slate-300 mb-1">Contraseña PFX Real</label>
                                <div className="relative">
                                    <div className="absolute inset-y-0 left-0 pl-3 flex items-center pointer-events-none">
                                        <Key className="h-3.5 w-3.5 text-slate-400" />
                                    </div>
                                    <input
                                        type="password"
                                        className="block w-full rounded-[10px] border border-slate-200 bg-white pl-9 px-3 py-2 text-sm focus:border-amber-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                        value={form.prodCertificatePassword}
                                        onChange={e => setForm({ ...form, prodCertificatePassword: e.target.value })}
                                        placeholder="Solo para cambiar"
                                    />
                                </div>
                            </div>
                        </div>
                    </div>
                </div>

                <div className="flex justify-end pt-2">
                    {/* Antes mezclaba slate-900 en claro con indigo-600 en oscuro (dos colores
                        de accion distintos segun el tema) — pasa al molde compartido, que ya
                        resuelve claro/oscuro con el mismo azul boleto (B.1). */}
                    <Button type="submit" disabled={loading} className="gap-2 px-10 py-3">
                        {loading ? (
                            <><RefreshCw className="h-4 w-4 animate-spin" aria-hidden="true" /> Guardando...</>
                        ) : (
                            <>
                                <Upload className="h-4 w-4" aria-hidden="true" />
                                Guardar Toda la Configuración
                            </>
                        )}
                    </Button>
                </div>
            </form>
        </div>
    );
}

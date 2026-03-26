import { useState, useEffect } from "react";
import { api } from "../api";
import { showSuccess, showError } from "../alerts";
import { Upload, CheckCircle2, AlertCircle, Key, FileKey, ShieldCheck, RefreshCw } from "lucide-react";

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
            setStatus("Error: " + error.message);
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
            showError("Error guardando configuración: " + (error.response?.data?.message || error.message));
        } finally {
            setLoading(false);
        }
    };

    return (
        <div className="space-y-6 max-w-5xl">
            {/* Header / Status */}
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4 rounded-2xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900/50">
                <div>
                    <h3 className="text-lg font-semibold text-slate-900 dark:text-white flex items-center gap-2">
                        <ShieldCheck className="h-5 w-5 text-indigo-600" />
                        Estado de Conexión
                    </h3>
                    <p className="text-sm text-slate-500 mt-1">Verifica la conexión con los servidores de AFIP en el entorno actual ({form.isProduction ? "Producción" : "Homologación"})</p>
                </div>
                <div className="flex items-center gap-4">
                    <div className={`flex items-center gap-2 px-4 py-2 rounded-xl text-sm font-medium ${status?.includes("Online")
                        ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400"
                        : "bg-red-100 text-red-700 dark:bg-red-900/30 dark:text-red-400"
                        }`}>
                        {status?.includes("Online") ? <CheckCircle2 className="h-4 w-4" /> : <AlertCircle className="h-4 w-4" />}
                        {status}
                    </div>
                    <button
                        onClick={checkStatus}
                        disabled={checkingStatus}
                        className="p-2 text-slate-500 hover:text-indigo-600 hover:bg-indigo-50 rounded-lg transition-colors"
                        title="Verificar Estado"
                    >
                        <RefreshCw className={`h-5 w-5 ${checkingStatus ? "animate-spin" : ""}`} />
                    </button>
                </div>
            </div>

            <form onSubmit={handleSubmit} className="space-y-6">
                {/* Configuración General */}
                <div className="rounded-2xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900/50">
                    <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">Datos Fiscales Generales</h3>
                    <div className="grid grid-cols-1 md:grid-cols-4 gap-6">
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">CUIT Emisor</label>
                            <input
                                type="number"
                                required
                                className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
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
                                className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.puntoDeVenta}
                                onChange={e => setForm({ ...form, puntoDeVenta: e.target.value })}
                            />
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Condición Fiscal</label>
                            <select
                                className="mt-1 block w-full rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                value={form.taxCondition}
                                onChange={e => setForm({ ...form, taxCondition: e.target.value })}
                            >
                                <option value="Responsable Inscripto">Responsable Inscripto</option>
                                <option value="Monotributo">Monotributo</option>
                                <option value="Exento">Exento</option>
                            </select>
                        </div>
                        <div className="flex items-center justify-between p-3 rounded-xl border border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/50">
                            <div>
                                <span className="block text-xs font-semibold uppercase text-slate-500">Entorno Activo</span>
                                <span className={`text-sm font-bold ${form.isProduction ? "text-amber-600" : "text-indigo-600"}`}>
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
                                <div className="w-11 h-6 bg-slate-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-indigo-300 dark:peer-focus:ring-indigo-800 rounded-full peer dark:bg-slate-700 peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-slate-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all dark:border-gray-600 peer-checked:bg-amber-500"></div>
                            </label>
                        </div>
                    </div>
                </div>

                {/* Certificados */}
                <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
                    {/* Homologación */}
                    <div className="rounded-2xl border border-indigo-100 bg-indigo-50/30 p-6 dark:border-indigo-900/30 dark:bg-indigo-900/10">
                        <div className="flex items-center gap-3 mb-4">
                            <div className="p-2 bg-indigo-100 dark:bg-indigo-900/50 rounded-lg">
                                <FileKey className="h-5 w-5 text-indigo-600 dark:text-indigo-400" />
                            </div>
                            <div>
                                <h4 className="font-bold text-slate-900 dark:text-white">Certificado Homologación</h4>
                                <p className="text-xs text-slate-500">Para pruebas y desarrollo (Testing)</p>
                            </div>
                        </div>

                        <div className="space-y-4">
                            <div className="border-2 border-dashed border-indigo-200 dark:border-indigo-800 rounded-xl p-4 flex flex-col items-center justify-center bg-white dark:bg-slate-900/50 hover:bg-slate-50 transition-colors">
                                <p className="text-xs text-slate-600 dark:text-slate-300 mb-3 text-center line-clamp-1">
                                    {certificateName || "Sin certificado de pruebas"}
                                </p>
                                <label className="cursor-pointer rounded-lg bg-indigo-600 px-4 py-2 text-xs font-medium text-white hover:bg-indigo-500 shadow-sm transition-all active:scale-95">
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
                                        className="block w-full rounded-xl border border-slate-200 bg-white pl-9 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                        value={form.certificatePassword}
                                        onChange={e => setForm({ ...form, certificatePassword: e.target.value })}
                                        placeholder="Solo para cambiar"
                                    />
                                </div>
                            </div>
                        </div>
                    </div>

                    {/* Producción */}
                    <div className="rounded-2xl border border-amber-100 bg-amber-50/30 p-6 dark:border-amber-900/30 dark:bg-amber-900/10">
                        <div className="flex items-center gap-3 mb-4">
                            <div className="p-2 bg-amber-100 dark:bg-amber-900/50 rounded-lg">
                                <ShieldCheck className="h-5 w-5 text-amber-600 dark:text-amber-400" />
                            </div>
                            <div>
                                <h4 className="font-bold text-slate-900 dark:text-white">Certificado Producción</h4>
                                <p className="text-xs text-slate-500">Para facturación real y legal</p>
                            </div>
                        </div>

                        <div className="space-y-4">
                            <div className="border-2 border-dashed border-amber-200 dark:border-amber-800 rounded-xl p-4 flex flex-col items-center justify-center bg-white dark:bg-slate-900/50 hover:bg-slate-100/50 transition-colors">
                                <p className="text-xs text-slate-600 dark:text-slate-300 mb-3 text-center line-clamp-1">
                                    {prodCertificateName || "Sin certificado real"}
                                </p>
                                <label className="cursor-pointer rounded-lg bg-amber-600 px-4 py-2 text-xs font-medium text-white hover:bg-amber-500 shadow-sm transition-all active:scale-95">
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
                                        className="block w-full rounded-xl border border-slate-200 bg-white pl-9 px-3 py-2 text-sm focus:border-amber-500 focus:outline-none dark:border-slate-700 dark:bg-slate-800"
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
                    <button
                        type="submit"
                        disabled={loading}
                        className="flex items-center gap-2 rounded-2xl bg-slate-900 px-10 py-3 text-sm font-bold text-white shadow-xl hover:bg-slate-800 disabled:opacity-50 transition-all active:scale-95 dark:bg-indigo-600 dark:hover:bg-indigo-500"
                    >
                        {loading ? (
                            <><RefreshCw className="h-4 w-4 animate-spin" /> Guardando...</>
                        ) : (
                            <>
                                <Upload className="h-4 w-4" />
                                Guardar Toda la Configuración
                            </>
                        )}
                    </button>
                </div>
            </form>
        </div>
    );
}

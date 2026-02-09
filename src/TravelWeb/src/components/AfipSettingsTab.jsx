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
        certificatePassword: ""
    });
    const [certificateFile, setCertificateFile] = useState(null);
    const [certificateName, setCertificateName] = useState(null);

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
                    certificatePassword: "" // Don't show password
                });
                if (data.certificatePath) {
                    setCertificateName("Certificado cargado (pfx)");
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

    const handleFileChange = (e) => {
        const file = e.target.files[0];
        if (file) {
            setCertificateFile(file);
            setCertificateName(file.name);
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

        try {
            await api.post("/afip/settings", formData);
            showSuccess("Configuración de AFIP guardada.");
            checkStatus();
        } catch (error) {
            showError("Error guardando configuración: " + (error.response?.data || error.message));
        } finally {
            setLoading(false);
        }
    };

    return (
        <div className="space-y-6 max-w-4xl">
            {/* Header / Status */}
            <div className="flex flex-col sm:flex-row sm:items-center justify-between gap-4 rounded-2xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900/50">
                <div>
                    <h3 className="text-lg font-semibold text-slate-900 dark:text-white flex items-center gap-2">
                        <ShieldCheck className="h-5 w-5 text-indigo-600" />
                        Estado de Conexión
                    </h3>
                    <p className="text-sm text-slate-500 mt-1">Verifica la conexión con los servidores de AFIP</p>
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

            <form onSubmit={handleSubmit} className="grid grid-cols-1 lg:grid-cols-2 gap-6">
                {/* Configuración General */}
                <div className="space-y-6">
                    <div className="rounded-2xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900/50 h-full">
                        <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">Datos Fiscales</h3>

                        <div className="space-y-4">
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
                                <p className="text-xs text-slate-500 mt-1">Determina el tipo de comprobante (A, B o C).</p>
                            </div>

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
                                <p className="text-xs text-slate-500 mt-1">Debe coincidir con el del certificado.</p>
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
                                <p className="text-xs text-slate-500 mt-1">Número de Punto de Venta dado de alta en AFIP para Web Services.</p>
                            </div>

                            <div className="flex items-center justify-between p-4 rounded-xl border border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-800/50">
                                <div>
                                    <span className="block text-sm font-medium text-slate-900 dark:text-white">Modo Producción</span>
                                    <span className="text-xs text-slate-500">Activa el envío real de facturas.</span>
                                </div>
                                <label className="relative inline-flex items-center cursor-pointer">
                                    <input
                                        type="checkbox"
                                        className="sr-only peer"
                                        checked={form.isProduction}
                                        onChange={e => setForm({ ...form, isProduction: e.target.checked })}
                                    />
                                    <div className="w-11 h-6 bg-slate-200 peer-focus:outline-none peer-focus:ring-4 peer-focus:ring-indigo-300 dark:peer-focus:ring-indigo-800 rounded-full peer dark:bg-slate-700 peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-slate-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all dark:border-gray-600 peer-checked:bg-indigo-600"></div>
                                </label>
                            </div>
                        </div>
                    </div>
                </div>

                {/* Certificado */}
                <div className="space-y-6">
                    <div className="rounded-2xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900/50 h-full">
                        <h3 className="text-lg font-semibold text-slate-900 dark:text-white mb-4">Certificado Digital</h3>

                        <div className="space-y-4">
                            <div>
                                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300 mb-2">Archivo .PFX</label>
                                <div className="border-2 border-dashed border-slate-300 dark:border-slate-700 rounded-xl p-6 flex flex-col items-center justify-center bg-slate-50 dark:bg-slate-800/50 hover:bg-slate-100 transition-colors">
                                    <FileKey className="h-10 w-10 text-slate-400 mb-2" />
                                    <p className="text-sm text-slate-600 dark:text-slate-300 mb-2 text-center">
                                        {certificateName || "Arrastra o selecciona tu certificado"}
                                    </p>
                                    <label className="cursor-pointer rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500">
                                        Seleccionar Archivo
                                        <input type="file" accept=".pfx" className="hidden" onChange={handleFileChange} />
                                    </label>
                                </div>
                            </div>

                            <div>
                                <label className="block text-sm font-medium text-slate-700 dark:text-slate-300">Contraseña del Certificado</label>
                                <div className="relative mt-1">
                                    <div className="absolute inset-y-0 left-0 pl-3 flex items-center pointer-events-none">
                                        <Key className="h-4 w-4 text-slate-400" />
                                    </div>
                                    <input
                                        type="password"
                                        className="block w-full rounded-xl border border-slate-200 bg-slate-50 pl-10 px-3 py-2 text-sm focus:border-indigo-500 focus:bg-white focus:outline-none dark:border-slate-700 dark:bg-slate-800"
                                        value={form.certificatePassword}
                                        onChange={e => setForm({ ...form, certificatePassword: e.target.value })}
                                        placeholder="Solo si la vas a cambiar"
                                    />
                                </div>
                                <p className="text-xs text-slate-500 mt-1">Déjalo en blanco para mantener la contraseña actual.</p>
                            </div>

                            <div className="pt-4 flex justify-end">
                                <button
                                    type="submit"
                                    disabled={loading}
                                    className="flex items-center gap-2 rounded-xl bg-indigo-600 px-6 py-2 text-sm font-medium text-white shadow-sm hover:bg-indigo-500 disabled:opacity-50"
                                >
                                    {loading ? (
                                        <>Guardando...</>
                                    ) : (
                                        <>
                                            <Upload className="h-4 w-4" />
                                            Guardar Configuración
                                        </>
                                    )}
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
            </form>
        </div>
    );
}


import { useState, useEffect } from "react";
import { X, Save, User } from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";

export default function PassengerFormModal({ isOpen, onClose, fileId, onSuccess, passengerToEdit }) {
    const [formData, setFormData] = useState({
        fullName: "",
        documentType: "DNI",
        documentNumber: "",
        birthDate: "",
        nationality: "",
        phone: "",
        email: "",
        gender: "M",
        notes: ""
    });
    const [loading, setLoading] = useState(false);

    useEffect(() => {
        if (isOpen) {
            if (passengerToEdit) {
                setFormData({
                    ...passengerToEdit,
                    birthDate: passengerToEdit.birthDate ? passengerToEdit.birthDate.split('T')[0] : ""
                });
            } else {
                setFormData({
                    fullName: "", documentType: "DNI", documentNumber: "",
                    birthDate: "", nationality: "", phone: "", email: "", gender: "M", notes: ""
                });
            }
        }
    }, [isOpen, passengerToEdit]);

    const handleSubmit = async (e) => {
        e.preventDefault();
        setLoading(true);

        // Sanitize Payload
        const payload = { ...formData };
        if (!payload.birthDate) payload.birthDate = null; // Fix for empty string => validation error
        if (payload.nationality === "") payload.nationality = null;
        if (payload.phone === "") payload.phone = null;
        if (payload.email === "") payload.email = null;

        try {
            if (passengerToEdit) {
                await api.put(`/travelfiles/passengers/${passengerToEdit.id}`, payload);
                showSuccess("Pasajero actualizado");
            } else {
                await api.post(`/travelfiles/${fileId}/passengers`, payload);
                showSuccess("Pasajero agregado");
            }
            onSuccess();
            onClose();
        } catch (error) {
            console.error(error);
            showError("Error al guardar pasajero: " + (error.response?.data || error.message));
        } finally {
            setLoading(false);
        }
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm p-4">
            <div className="bg-white rounded-xl shadow-2xl w-full max-w-2xl overflow-hidden animate-in fade-in zoom-in-95 duration-200">

                {/* Header */}
                <div className="bg-gray-50 border-b border-gray-100 px-6 py-4 flex justify-between items-center">
                    <h3 className="text-lg font-semibold text-gray-800 flex items-center gap-2">
                        <User className="w-5 h-5 text-blue-600" />
                        {passengerToEdit ? "Editar Pasajero" : "Nuevo Pasajero"}
                    </h3>
                    <button onClick={onClose} className="text-gray-400 hover:text-gray-600 transition-colors">
                        <X className="w-5 h-5" />
                    </button>
                </div>

                {/* Body */}
                <div className="p-6">
                    <form onSubmit={handleSubmit} className="grid grid-cols-1 md:grid-cols-2 gap-4">

                        {/* Name - Full Width */}
                        <div className="md:col-span-2">
                            <label className="block text-sm font-medium text-gray-700 mb-1">Nombre Completo *</label>
                            <input
                                required
                                type="text"
                                className="w-full rounded-lg border-gray-300 focus:ring-blue-500 focus:border-blue-500"
                                placeholder="Ej: Juan Pérez"
                                value={formData.fullName}
                                onChange={(e) => setFormData({ ...formData, fullName: e.target.value })}
                            />
                        </div>

                        {/* Document */}
                        <div>
                            <label className="block text-sm font-medium text-gray-700 mb-1">Tipo Documento</label>
                            <select
                                className="w-full rounded-lg border-gray-300 focus:ring-blue-500 focus:border-blue-500"
                                value={formData.documentType}
                                onChange={(e) => setFormData({ ...formData, documentType: e.target.value })}
                            >
                                <option value="DNI">DNI</option>
                                <option value="Pasaporte">Pasaporte</option>
                                <option value="Cedula">Cédula</option>
                                <option value="Otro">Otro</option>
                            </select>
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-gray-700 mb-1">Número Documento</label>
                            <input
                                type="text"
                                className="w-full rounded-lg border-gray-300 focus:ring-blue-500 focus:border-blue-500"
                                value={formData.documentNumber}
                                onChange={(e) => setFormData({ ...formData, documentNumber: e.target.value })}
                            />
                        </div>

                        {/* Personal Info */}
                        <div>
                            <label className="block text-sm font-medium text-gray-700 mb-1">Fecha Nacimiento</label>
                            <input
                                type="date"
                                className="w-full rounded-lg border-gray-300 focus:ring-blue-500 focus:border-blue-500"
                                value={formData.birthDate}
                                onChange={(e) => setFormData({ ...formData, birthDate: e.target.value })}
                            />
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-gray-700 mb-1">Nacionalidad</label>
                            <input
                                type="text"
                                className="w-full rounded-lg border-gray-300 focus:ring-blue-500 focus:border-blue-500"
                                placeholder="Ej: Argentina"
                                value={formData.nationality || ""}
                                onChange={(e) => setFormData({ ...formData, nationality: e.target.value })}
                            />
                        </div>

                        <div>
                            <label className="block text-sm font-medium text-gray-700 mb-1">Género</label>
                            <select
                                className="w-full rounded-lg border-gray-300 focus:ring-blue-500 focus:border-blue-500"
                                value={formData.gender || "M"}
                                onChange={(e) => setFormData({ ...formData, gender: e.target.value })}
                            >
                                <option value="M">Masculino</option>
                                <option value="F">Femenino</option>
                                <option value="X">Otro</option>
                            </select>
                        </div>

                        {/* Contact */}
                        <div>
                            <label className="block text-sm font-medium text-gray-700 mb-1">Teléfono</label>
                            <input
                                type="tel"
                                className="w-full rounded-lg border-gray-300 focus:ring-blue-500 focus:border-blue-500"
                                placeholder="+54 9 11..."
                                value={formData.phone || ""}
                                onChange={(e) => setFormData({ ...formData, phone: e.target.value })}
                            />
                        </div>

                        <div className="md:col-span-2">
                            <label className="block text-sm font-medium text-gray-700 mb-1">Email</label>
                            <input
                                type="email"
                                className="w-full rounded-lg border-gray-300 focus:ring-blue-500 focus:border-blue-500"
                                placeholder="correo@ejemplo.com"
                                value={formData.email || ""}
                                onChange={(e) => setFormData({ ...formData, email: e.target.value })}
                            />
                        </div>

                        {/* Notes */}
                        <div className="md:col-span-2">
                            <label className="block text-sm font-medium text-gray-700 mb-1">Notas Adicionales</label>
                            <textarea
                                rows={2}
                                className="w-full rounded-lg border-gray-300 focus:ring-blue-500 focus:border-blue-500"
                                placeholder="Preferencias alimenticias, asistencia especial..."
                                value={formData.notes || ""}
                                onChange={(e) => setFormData({ ...formData, notes: e.target.value })}
                            />
                        </div>

                        {/* Footer Actions */}
                        <div className="md:col-span-2 flex justify-end gap-3 mt-4 pt-4 border-t border-gray-100">
                            <button
                                type="button"
                                onClick={onClose}
                                className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 transition-colors"
                            >
                                Cancelar
                            </button>
                            <button
                                type="submit"
                                disabled={loading}
                                className="px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700 focus:ring-4 focus:ring-blue-300 transition-colors flex items-center gap-2"
                            >
                                <Save className="w-4 h-4" />
                                {loading ? "Guardando..." : "Guardar Pasajero"}
                            </button>
                        </div>

                    </form>
                </div>
            </div>
        </div>
    );
}

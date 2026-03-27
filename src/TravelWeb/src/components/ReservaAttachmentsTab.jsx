import React, { useState, useEffect, useCallback } from 'react';
import { api } from '../api';
import {
    AlertTriangle,
    CheckCircle2,
    Clock3,
    Download,
    Eye,
    File,
    FileText,
    Loader2,
    MessageSquare,
    Paperclip,
    Save,
    Send,
    Smartphone,
    Trash2,
    UploadCloud
} from 'lucide-react';
import Swal from 'sweetalert2';
import { toast } from 'sonner';
import { getPublicId } from '../lib/publicIds';

function formatFileSize(bytes) {
    if (!bytes) return '0 B';
    const units = ['B', 'KB', 'MB', 'GB'];
    const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
    return `${(bytes / Math.pow(1024, index)).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function formatPhoneSource(source) {
    switch (source) {
        case 'override':
            return 'Override de reserva';
        case 'payer':
            return 'Titular de la reserva';
        case 'lead':
            return 'Posible cliente asociado';
        default:
            return 'Sin numero';
    }
}

function formatDeliveryStatus(item) {
    if (item.kind === 'IncomingMessage') return 'Mensaje recibido';
    if (item.kind === 'OperationalAck') return 'Acuse automatico';

    switch (item.status) {
        case 'PendingApproval':
            return 'Pendiente';
        case 'Sent':
            return 'Enviado';
        case 'Failed':
            return 'Fallido';
        case 'NeedsAgent':
            return 'Requiere agente';
        default:
            return item.status || 'Sin estado';
    }
}

function getDeliveryTone(item) {
    if (item.kind === 'IncomingMessage' || item.status === 'NeedsAgent') {
        return 'bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-900/20 dark:text-amber-300 dark:border-amber-900/40';
    }

    switch (item.status) {
        case 'Sent':
            return 'bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-900/20 dark:text-emerald-300 dark:border-emerald-900/40';
        case 'Failed':
            return 'bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-900/20 dark:text-rose-300 dark:border-rose-900/40';
        default:
            return 'bg-slate-100 text-slate-700 border-slate-200 dark:bg-slate-800 dark:text-slate-300 dark:border-slate-700';
    }
}

function downloadBlob(blob, fileName) {
    const url = window.URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.setAttribute('download', fileName);
    document.body.appendChild(link);
    link.click();
    link.remove();
    window.setTimeout(() => window.URL.revokeObjectURL(url), 1000);
}

const AttachmentRow = ({ file, onDelete, onDownload }) => {
    const [previewUrl, setPreviewUrl] = useState(null);

    const id = getPublicId(file);
    const fileName = file.fileName || file.FileName;
    const fileSize = file.fileSize || file.FileSize;
    const contentType = file.contentType || file.ContentType;
    const uploadedBy = file.uploadedBy || file.UploadedBy;
    const uploadedAt = file.uploadedAt || file.UploadedAt;

    const isImage = contentType?.includes('image');
    const isPdf = contentType?.includes('pdf');

    useEffect(() => {
        let active = true;
        let objectUrl = null;

        const loadThumbnail = async () => {
            if (!isImage) return;

            try {
                const blob = await api.get(`/attachments/${id}/download`, { responseType: 'blob' });
                if (!active) return;
                objectUrl = URL.createObjectURL(blob);
                setPreviewUrl(objectUrl);
            } catch (error) {
                console.warn('Could not load thumbnail', error);
            }
        };

        loadThumbnail();

        return () => {
            active = false;
            if (objectUrl) {
                URL.revokeObjectURL(objectUrl);
            }
        };
    }, [id, isImage]);

    const handlePreviewClick = async (event) => {
        event.stopPropagation();

        if (isImage && previewUrl) {
            Swal.fire({
                imageUrl: previewUrl,
                imageAlt: fileName,
                showConfirmButton: false,
                showCloseButton: true,
                customClass: {
                    popup: 'w-auto max-w-[90vw] max-h-[90vh] p-0 overflow-hidden bg-transparent shadow-none',
                    image: 'max-h-[85vh] object-contain rounded-lg shadow-2xl bg-white'
                },
                backdrop: 'rgba(0,0,0,0.8)'
            });
            return;
        }

        if (isPdf) {
            try {
                const blob = await api.get(`/attachments/${id}/download`, { responseType: 'blob' });
                const url = URL.createObjectURL(blob);
                window.open(url, '_blank', 'noopener,noreferrer');
                window.setTimeout(() => URL.revokeObjectURL(url), 2000);
            } catch (error) {
                toast.error('Error al abrir PDF');
            }
        }
    };

    return (
        <div className="group flex items-center p-3 bg-white dark:bg-slate-800 border-b border-gray-100 dark:border-slate-700 hover:bg-gray-50 dark:hover:bg-slate-700/50 transition-colors">
            <div
                className="flex-shrink-0 w-12 h-12 mr-4 flex items-center justify-center bg-gray-100 dark:bg-slate-900 rounded-lg overflow-hidden cursor-pointer border border-gray-200 dark:border-slate-600"
                onClick={handlePreviewClick}
            >
                {isImage && previewUrl ? (
                    <img src={previewUrl} alt={fileName} className="w-full h-full object-cover" />
                ) : isPdf ? (
                    <FileText className="w-6 h-6 text-rose-500" />
                ) : (
                    <File className="w-6 h-6 text-gray-400" />
                )}
            </div>

            <div className="flex-1 min-w-0 mr-4 cursor-pointer" onClick={handlePreviewClick}>
                <p className="text-sm font-medium text-gray-900 dark:text-gray-100 truncate" title={fileName}>
                    {fileName}
                </p>
                <div className="flex items-center text-xs text-gray-500 dark:text-gray-400 mt-0.5 space-x-3">
                    <span>{formatFileSize(fileSize)}</span>
                    <span>&bull;</span>
                    <span>
                        {new Date(uploadedAt).toLocaleDateString()} {new Date(uploadedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                    </span>
                    <span>&bull;</span>
                    <span>Subido por {uploadedBy}</span>
                </div>
            </div>

            <div className="flex items-center space-x-1 transition-opacity">
                {(isImage || isPdf) && (
                    <button
                        onClick={handlePreviewClick}
                        className="p-2 text-gray-500 hover:text-blue-600 hover:bg-blue-50 dark:hover:bg-blue-900/30 rounded-full transition-colors"
                        title="Ver"
                    >
                        <Eye className="w-4 h-4" />
                    </button>
                )}
                <button
                    onClick={(event) => {
                        event.stopPropagation();
                        onDownload(id, fileName);
                    }}
                    className="p-2 text-gray-500 hover:text-green-600 hover:bg-green-50 dark:hover:bg-green-900/30 rounded-full transition-colors"
                    title="Descargar"
                >
                    <Download className="w-4 h-4" />
                </button>
                <button
                    onClick={(event) => {
                        event.stopPropagation();
                        onDelete(id, fileName);
                    }}
                    className="p-2 text-gray-500 hover:text-red-600 hover:bg-red-50 dark:hover:bg-red-900/30 rounded-full transition-colors"
                    title="Eliminar"
                >
                    <Trash2 className="w-4 h-4" />
                </button>
            </div>
        </div>
    );
};

function DeliveryRow({ item }) {
    const sentAt = item.sentAt || item.createdAt;

    return (
        <div className="rounded-2xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 p-4 space-y-3">
            <div className="flex flex-col gap-2 md:flex-row md:items-center md:justify-between">
                <div className="flex items-center gap-2">
                    <span className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-[10px] font-black uppercase tracking-widest border ${getDeliveryTone(item)}`}>
                        {item.kind === 'IncomingMessage' ? <MessageSquare className="w-3 h-3" /> : <Clock3 className="w-3 h-3" />}
                        {formatDeliveryStatus(item)}
                    </span>
                    <span className="text-[11px] font-bold text-slate-400 uppercase tracking-widest">
                        {item.direction === 'Inbound' ? 'Entrante' : 'Saliente'}
                    </span>
                </div>
                <div className="text-xs text-slate-500 dark:text-slate-400">
                    {new Date(sentAt).toLocaleDateString()} {new Date(sentAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                </div>
            </div>

            <div className="text-sm text-slate-700 dark:text-slate-200 whitespace-pre-wrap break-words">
                {item.messageText || 'Sin texto registrado.'}
            </div>

            <div className="flex flex-wrap gap-4 text-xs text-slate-500 dark:text-slate-400">
                <span>Telefono: {item.phone}</span>
                {item.attachmentName && <span>Adjunto: {item.attachmentName}</span>}
                {item.sentBy && <span>Enviado por: {item.sentBy}</span>}
                {item.error && <span className="text-rose-500">Error: {item.error}</span>}
            </div>
        </div>
    );
}

export const ReservaAttachmentsTab = ({ reservaId }) => {
    const [attachments, setAttachments] = useState([]);
    const [loading, setLoading] = useState(true);
    const [isDragging, setIsDragging] = useState(false);
    const [uploading, setUploading] = useState(false);

    const [preview, setPreview] = useState(null);
    const [history, setHistory] = useState([]);
    const [whatsAppLoading, setWhatsAppLoading] = useState(true);
    const [savingContact, setSavingContact] = useState(false);
    const [sendingVoucher, setSendingVoucher] = useState(false);
    const [downloadingVoucher, setDownloadingVoucher] = useState(false);
    const [phoneOverride, setPhoneOverride] = useState('');
    const [caption, setCaption] = useState('');

    const fetchAttachments = useCallback(async () => {
        try {
            setLoading(true);
            const data = await api.get(`/attachments/reserva/${reservaId}`);
            setAttachments(Array.isArray(data) ? data : []);
        } catch (error) {
            console.error('Error loading attachments:', error);
            toast.error('No se pudieron cargar los adjuntos.');
        } finally {
            setLoading(false);
        }
    }, [reservaId]);

    const fetchWhatsApp = useCallback(async () => {
        try {
            setWhatsAppLoading(true);
            const [nextPreview, nextHistory] = await Promise.all([
                api.get(`/reservas/${reservaId}/whatsapp/voucher-preview`),
                api.get(`/reservas/${reservaId}/whatsapp/history`)
            ]);

            setPreview(nextPreview);
            setHistory(Array.isArray(nextHistory) ? nextHistory : []);
            setPhoneOverride(nextPreview?.phoneOverride || '');
            setCaption(nextPreview?.caption || '');
        } catch (error) {
            console.error('Error loading WhatsApp voucher data:', error);
            setPreview(null);
            setHistory([]);
        } finally {
            setWhatsAppLoading(false);
        }
    }, [reservaId]);

    useEffect(() => {
        fetchAttachments();
        fetchWhatsApp();
    }, [fetchAttachments, fetchWhatsApp]);

    const handleDragOver = (event) => {
        event.preventDefault();
        setIsDragging(true);
    };

    const handleDragLeave = (event) => {
        event.preventDefault();
        setIsDragging(false);
    };

    const handleDrop = async (event) => {
        event.preventDefault();
        setIsDragging(false);
        const files = event.dataTransfer.files;
        if (files.length > 0) {
            await handleUpload(files[0]);
        }
    };

    const handleFileInput = async (event) => {
        const files = event.target.files;
        if (files.length > 0) {
            await handleUpload(files[0]);
        }
    };

    const handleUpload = async (file) => {
        if (!file) return;
        if (file.size > 25 * 1024 * 1024) {
            toast.error('El archivo es demasiado grande (max 25 MB).');
            return;
        }

        const formData = new FormData();
        formData.append('file', file);

        try {
            setUploading(true);
            await api.post(`/attachments/upload/${reservaId}`, formData);
            toast.success('Archivo subido correctamente.');
            fetchAttachments();
        } catch (error) {
            console.error('Error uploading:', error);
            toast.error('Error al subir el archivo.');
        } finally {
            setUploading(false);
        }
    };

    const handleDelete = async (id, fileName) => {
        const result = await Swal.fire({
            title: 'Eliminar archivo?',
            text: `Se eliminara "${fileName}" permanentemente.`,
            icon: 'warning',
            showCancelButton: true,
            confirmButtonText: 'Si, eliminar',
            cancelButtonText: 'Cancelar',
            confirmButtonColor: '#ef4444'
        });

        if (!result.isConfirmed) return;

        try {
            await api.delete(`/attachments/${id}`);
            toast.success('Archivo eliminado.');
            setAttachments((previous) => previous.filter((item) => getPublicId(item) !== id));
        } catch (error) {
            console.error('Error deleting:', error);
            toast.error('No se pudo eliminar el archivo.');
        }
    };

    const handleDownload = async (id, fileName) => {
        const loadingToast = toast.loading('Preparando descarga...');

        try {
            const blob = await api.get(`/attachments/${id}/download`, { responseType: 'blob' });
            if (!blob || blob.size === 0) {
                throw new Error('El archivo descargado esta vacio o corrupto.');
            }

            downloadBlob(blob, fileName);
            toast.dismiss(loadingToast);
            toast.success('Descarga iniciada.');
        } catch (error) {
            console.error('Download error:', error);
            toast.dismiss(loadingToast);
            toast.error(`Error al descargar: ${error.message || 'Error desconocido'}`);
        }
    };

    const handleSaveContact = async () => {
        try {
            setSavingContact(true);
            const updatedPreview = await api.patch(`/reservas/${reservaId}/whatsapp-contact`, {
                whatsAppPhoneOverride: phoneOverride.trim() || null
            });

            setPreview(updatedPreview);
            setPhoneOverride(updatedPreview?.phoneOverride || '');
            setCaption(updatedPreview?.caption || '');
            toast.success('Telefono operativo actualizado.');
            fetchWhatsApp();
        } catch (error) {
            console.error('Error updating WhatsApp contact:', error);
            toast.error(error.message || 'No se pudo actualizar el numero.');
        } finally {
            setSavingContact(false);
        }
    };

    const handleDownloadVoucher = async () => {
        try {
            setDownloadingVoucher(true);
            const blob = await api.get(`/reservas/${reservaId}/voucher/pdf`, { responseType: 'blob' });
            downloadBlob(blob, preview?.attachmentName || `voucher-${reservaId}.pdf`);
            toast.success('Voucher PDF descargado.');
        } catch (error) {
            console.error('Error downloading voucher PDF:', error);
            toast.error(error.message || 'No se pudo descargar el voucher.');
        } finally {
            setDownloadingVoucher(false);
        }
    };

    const handleSendVoucher = async () => {
        try {
            setSendingVoucher(true);
            await api.post(`/reservas/${reservaId}/whatsapp/send-voucher`, {
                caption
            });
            toast.success('Voucher enviado por WhatsApp.');
            fetchWhatsApp();
        } catch (error) {
            console.error('Error sending voucher:', error);
            toast.error(error.message || 'No se pudo enviar el voucher.');
        } finally {
            setSendingVoucher(false);
        }
    };

    return (
        <div className="space-y-6">
            <div className="bg-white dark:bg-slate-800 rounded-xl border border-slate-200 dark:border-slate-700 shadow-sm overflow-hidden">
                <div className="px-4 py-3 border-b border-slate-200 dark:border-slate-700 bg-slate-50 dark:bg-slate-900/50 flex items-center justify-between">
                    <div className="flex items-center gap-2">
                        <div className="w-9 h-9 rounded-xl bg-emerald-500 text-white flex items-center justify-center shadow-sm">
                            <Smartphone className="w-4 h-4" />
                        </div>
                        <div>
                            <h3 className="text-sm font-semibold text-slate-800 dark:text-slate-100">Voucher por WhatsApp</h3>
                            <p className="text-xs text-slate-500 dark:text-slate-400">Resolucion de telefono, override operativo e historial del canal.</p>
                        </div>
                    </div>
                    {whatsAppLoading && <Loader2 className="w-5 h-5 animate-spin text-emerald-500" />}
                </div>

                <div className="p-4 sm:p-5 space-y-5">
                    <div className="grid grid-cols-1 xl:grid-cols-[1.2fr,0.8fr] gap-5">
                        <div className="space-y-4">
                            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                <div className="rounded-2xl border border-slate-200 dark:border-slate-700 bg-slate-50/70 dark:bg-slate-900/40 p-4">
                                    <div className="text-[11px] font-black uppercase tracking-widest text-slate-400 mb-1">Telefono resuelto</div>
                                    <div className="text-lg font-black text-slate-900 dark:text-white">
                                        {preview?.resolvedPhone || 'No disponible'}
                                    </div>
                                    <div className="mt-1 text-xs text-slate-500 dark:text-slate-400">
                                        Fuente: {formatPhoneSource(preview?.phoneSource)}
                                    </div>
                                </div>

                                <div className="rounded-2xl border border-slate-200 dark:border-slate-700 bg-slate-50/70 dark:bg-slate-900/40 p-4">
                                    <div className="text-[11px] font-black uppercase tracking-widest text-slate-400 mb-1">Documento</div>
                                    <div className="text-sm font-bold text-slate-900 dark:text-white break-all">
                                        {preview?.attachmentName || `voucher-${reservaId}.pdf`}
                                    </div>
                                    <div className="mt-3">
                                        <button
                                            onClick={handleDownloadVoucher}
                                            disabled={downloadingVoucher}
                                            className="inline-flex items-center gap-2 px-3 py-2 rounded-xl border border-slate-200 dark:border-slate-700 text-sm font-semibold text-slate-700 dark:text-slate-200 hover:bg-slate-100 dark:hover:bg-slate-800 transition-colors disabled:opacity-60"
                                        >
                                            {downloadingVoucher ? <Loader2 className="w-4 h-4 animate-spin" /> : <Download className="w-4 h-4" />}
                                            Descargar PDF
                                        </button>
                                    </div>
                                </div>
                            </div>

                            <div className="space-y-2">
                                <label className="text-[11px] font-black uppercase tracking-widest text-slate-400">Override de WhatsApp</label>
                                <div className="flex flex-col md:flex-row gap-3">
                                    <input
                                        value={phoneOverride}
                                        onChange={(event) => setPhoneOverride(event.target.value)}
                                        placeholder="Ej: 5491160000000"
                                        className="flex-1 px-4 py-3 rounded-2xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 text-sm font-medium focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                                    />
                                    <button
                                        onClick={handleSaveContact}
                                        disabled={savingContact}
                                        className="inline-flex items-center justify-center gap-2 px-4 py-3 rounded-2xl bg-slate-900 text-white text-sm font-bold hover:bg-slate-800 transition-colors disabled:opacity-60"
                                    >
                                        {savingContact ? <Loader2 className="w-4 h-4 animate-spin" /> : <Save className="w-4 h-4" />}
                                        Guardar numero
                                    </button>
                                </div>
                                <p className="text-xs text-slate-500 dark:text-slate-400">
                                    Este override solo afecta a esta reserva y no modifica el telefono maestro del cliente.
                                </p>
                            </div>

                            <div className="space-y-2">
                                <label className="text-[11px] font-black uppercase tracking-widest text-slate-400">Caption del envio</label>
                                <textarea
                                    value={caption}
                                    onChange={(event) => setCaption(event.target.value)}
                                    rows={4}
                                    className="w-full px-4 py-3 rounded-2xl border border-slate-200 dark:border-slate-700 bg-white dark:bg-slate-900 text-sm font-medium focus:outline-none focus:ring-2 focus:ring-emerald-500/20 focus:border-emerald-500"
                                />
                            </div>

                            {preview?.error && (
                                <div className="flex items-start gap-3 rounded-2xl border border-amber-200 bg-amber-50 px-4 py-3 text-amber-800 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-200">
                                    <AlertTriangle className="w-5 h-5 mt-0.5 flex-shrink-0" />
                                    <div>
                                        <div className="text-sm font-bold">Envio bloqueado</div>
                                        <div className="text-xs mt-1">{preview.error}</div>
                                    </div>
                                </div>
                            )}

                            <div className="flex flex-wrap gap-3">
                                <button
                                    onClick={handleSendVoucher}
                                    disabled={!preview?.canSend || sendingVoucher}
                                    className="inline-flex items-center gap-2 px-5 py-3 rounded-2xl bg-emerald-600 text-white text-sm font-black hover:bg-emerald-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                                >
                                    {sendingVoucher ? <Loader2 className="w-4 h-4 animate-spin" /> : <Send className="w-4 h-4" />}
                                    Enviar voucher por WhatsApp
                                </button>

                                {preview?.canSend && (
                                    <div className="inline-flex items-center gap-2 px-4 py-3 rounded-2xl bg-emerald-50 text-emerald-700 text-sm font-semibold dark:bg-emerald-900/20 dark:text-emerald-300">
                                        <CheckCircle2 className="w-4 h-4" />
                                        Listo para enviar
                                    </div>
                                )}
                            </div>
                        </div>

                        <div className="rounded-2xl border border-slate-200 dark:border-slate-700 bg-slate-50/70 dark:bg-slate-900/40 p-4 space-y-4">
                            <div className="flex items-center justify-between">
                                <div>
                                    <div className="text-sm font-bold text-slate-900 dark:text-white">Historial operativo</div>
                                    <div className="text-xs text-slate-500 dark:text-slate-400">{history.length} evento(s) registrados</div>
                                </div>
                            </div>

                            <div className="space-y-3 max-h-[430px] overflow-y-auto pr-1">
                                {whatsAppLoading ? (
                                    <div className="py-10 flex justify-center">
                                        <Loader2 className="w-7 h-7 animate-spin text-emerald-500" />
                                    </div>
                                ) : history.length === 0 ? (
                                    <div className="py-10 text-center text-slate-500 dark:text-slate-400">
                                        <MessageSquare className="w-8 h-8 mx-auto mb-3 text-slate-300 dark:text-slate-600" />
                                        <p className="text-sm font-medium">Todavia no hay actividad de WhatsApp para esta reserva.</p>
                                    </div>
                                ) : (
                                    history.map((item) => <DeliveryRow key={item.id} item={item} />)
                                )}
                            </div>
                        </div>
                    </div>
                </div>
            </div>

            <div
                className={`border-2 border-dashed rounded-xl p-8 text-center transition-all cursor-pointer relative overflow-hidden ${
                    isDragging
                        ? 'border-blue-500 bg-blue-50 dark:bg-blue-900/20'
                        : 'border-gray-300 dark:border-slate-700 hover:border-gray-400 bg-gray-50 dark:bg-slate-800/50'
                }`}
                onDragOver={handleDragOver}
                onDragLeave={handleDragLeave}
                onDrop={handleDrop}
                onClick={() => document.getElementById('fileInput')?.click()}
            >
                <input
                    id="fileInput"
                    type="file"
                    className="hidden"
                    onChange={handleFileInput}
                    accept="image/*,application/pdf,.doc,.docx,.xls,.xlsx"
                />
                <div className="flex flex-col items-center justify-center space-y-3 relative z-10">
                    {uploading ? (
                        <Loader2 className="w-12 h-12 text-blue-500 animate-spin" />
                    ) : (
                        <div className="p-3 bg-white dark:bg-slate-800 rounded-full shadow-sm">
                            <UploadCloud className={`w-8 h-8 ${isDragging ? 'text-blue-500' : 'text-gray-400'}`} />
                        </div>
                    )}
                    <div>
                        <div className="text-base font-medium text-gray-900 dark:text-gray-100">
                            {uploading ? 'Subiendo archivo...' : 'Haz clic o arrastra archivos aqui'}
                        </div>
                        <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                            Soporta imagenes, PDF y documentos Office (max 25 MB)
                        </p>
                    </div>
                </div>
            </div>

            <div className="bg-white dark:bg-slate-800 rounded-xl border border-gray-200 dark:border-slate-700 shadow-sm overflow-hidden">
                <div className="px-4 py-3 border-b border-gray-200 dark:border-slate-700 bg-gray-50 dark:bg-slate-900/50 flex items-center justify-between">
                    <h3 className="text-sm font-semibold text-gray-700 dark:text-gray-200 flex items-center">
                        <Paperclip className="w-4 h-4 mr-2" />
                        Documentos adjuntos <span className="text-xs font-normal text-gray-500 ml-1">({attachments?.length || 0})</span>
                    </h3>
                </div>

                {loading ? (
                    <div className="p-8 flex justify-center">
                        <Loader2 className="w-8 h-8 animate-spin text-blue-500" />
                    </div>
                ) : (attachments?.length || 0) === 0 ? (
                    <div className="text-center py-12 text-gray-500 text-sm">
                        <FileText className="w-10 h-10 mx-auto text-gray-300 mb-3" />
                        <p>No hay documentos cargados todavia.</p>
                    </div>
                ) : (
                    <div className="divide-y divide-gray-100 dark:divide-slate-700">
                        {attachments.map((file) => (
                            <AttachmentRow
                                key={getPublicId(file)}
                                file={file}
                                onDelete={handleDelete}
                                onDownload={handleDownload}
                            />
                        ))}
                    </div>
                )}
            </div>
        </div>
    );
};

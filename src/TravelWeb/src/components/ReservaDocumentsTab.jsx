import { useCallback, useEffect, useRef, useState } from "react";
import Swal from "sweetalert2";
import { toast } from "sonner";
import { Check, Download, Eye, File, FileText, Loader2, Paperclip, Pencil, Trash2, UploadCloud, X } from "lucide-react";
import { api } from "../api";
import { getApiErrorMessage } from "../lib/errors";
import { getPublicId } from "../lib/publicIds";
import { formatDateTime } from "../lib/utils";

function formatFileSize(bytes) {
  if (!bytes) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / Math.pow(1024, index)).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
}

function downloadBlob(blob, fileName) {
  const url = window.URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.setAttribute("download", fileName);
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(() => window.URL.revokeObjectURL(url), 1000);
}

/**
 * Fila de un documento adjunto en la pestaña de documentos de la reserva.
 * Muestra nombre, tamaño, fecha/usuario de carga, y botones de acción.
 * Incluye edición inline del nombre: al hacer click en el lápiz, el nombre
 * se convierte en un input de texto confirmable con Enter o el botón checkmark.
 *
 * Props:
 *   soloLectura — cuando true, el botón Renombrar y el botón Eliminar se ocultan.
 *                 Se usa cuando la reserva no permite agregar/modificar documentos.
 *                 Ver y descargar siguen disponibles siempre.
 */
function DocumentRow({ file, onDelete, onDownload, onRename, soloLectura = false }) {
  const [previewUrl, setPreviewUrl] = useState(null);

  // Estado de edición inline del nombre
  const [isEditing, setIsEditing] = useState(false);
  const [editingName, setEditingName] = useState("");
  const [isSavingName, setIsSavingName] = useState(false);
  const editInputRef = useRef(null);

  const id = getPublicId(file);
  const fileName = file.fileName || file.FileName;
  const fileSize = file.fileSize || file.FileSize;
  const contentType = file.contentType || file.ContentType;
  const uploadedBy = file.uploadedBy || file.UploadedBy;
  const uploadedAt = file.uploadedAt || file.UploadedAt;
  const isImage = contentType?.includes("image");
  const isPdf = contentType?.includes("pdf");

  useEffect(() => {
    let active = true;
    let objectUrl = null;

    const loadThumbnail = async () => {
      if (!isImage || !id) return;

      try {
        const blob = await api.get(`/attachments/${id}/download`, { responseType: "blob" });
        if (!active) return;
        objectUrl = URL.createObjectURL(blob);
        setPreviewUrl(objectUrl);
      } catch (error) {
        console.warn("Could not load document thumbnail", error);
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

  // Cuando el modo edición se activa, enfocamos el input automáticamente
  // para que el usuario pueda escribir de inmediato sin un click extra.
  useEffect(() => {
    if (isEditing && editInputRef.current) {
      editInputRef.current.focus();
      editInputRef.current.select();
    }
  }, [isEditing]);

  const handleStartEditing = (event) => {
    event.stopPropagation();
    setEditingName(fileName);
    setIsEditing(true);
  };

  const handleCancelEditing = (event) => {
    event?.stopPropagation();
    setIsEditing(false);
    setEditingName("");
  };

  const handleConfirmRename = async (event) => {
    event?.stopPropagation();

    const trimmedName = editingName.trim();
    if (!trimmedName) {
      toast.error("El nombre no puede estar vacío.");
      editInputRef.current?.focus();
      return;
    }

    // Si el usuario no cambió nada, simplemente cerramos sin llamar al backend
    if (trimmedName === fileName) {
      setIsEditing(false);
      return;
    }

    try {
      setIsSavingName(true);
      await onRename(id, trimmedName);
      setIsEditing(false);
    } finally {
      setIsSavingName(false);
    }
  };

  const handleEditKeyDown = (event) => {
    if (event.key === "Enter") {
      handleConfirmRename(event);
    } else if (event.key === "Escape") {
      handleCancelEditing(event);
    }
  };

  const handlePreviewClick = async (event) => {
    event.stopPropagation();

    if (isImage && previewUrl) {
      Swal.fire({
        imageUrl: previewUrl,
        imageAlt: fileName,
        showConfirmButton: false,
        showCloseButton: true,
        customClass: {
          popup: "w-auto max-w-[90vw] max-h-[90vh] p-0 overflow-hidden bg-transparent shadow-none",
          image: "max-h-[85vh] object-contain rounded-lg shadow-2xl bg-white",
        },
        backdrop: "rgba(0,0,0,0.8)",
      });
      return;
    }

    if (isPdf) {
      try {
        const blob = await api.get(`/attachments/${id}/download`, { responseType: "blob" });
        const url = URL.createObjectURL(blob);
        window.open(url, "_blank", "noopener,noreferrer");
        window.setTimeout(() => URL.revokeObjectURL(url), 2000);
      } catch {
        toast.error("Error al abrir PDF");
      }
    }
  };

  return (
    <div className="group flex items-center border-b border-gray-100 bg-white p-3 transition-colors hover:bg-gray-50 dark:border-slate-700 dark:bg-slate-800 dark:hover:bg-slate-700/50">
      <div
        className="mr-4 flex h-12 w-12 flex-shrink-0 cursor-pointer items-center justify-center overflow-hidden rounded-lg border border-gray-200 bg-gray-100 dark:border-slate-600 dark:bg-slate-900"
        onClick={handlePreviewClick}
      >
        {isImage && previewUrl ? (
          <img src={previewUrl} alt={fileName} className="h-full w-full object-cover" />
        ) : isPdf ? (
          <FileText className="h-6 w-6 text-rose-500" />
        ) : (
          <File className="h-6 w-6 text-gray-400" />
        )}
      </div>

      <div className="mr-4 min-w-0 flex-1">
        {isEditing ? (
          // Modo edición inline: reemplaza el nombre por un input de texto
          <div className="flex items-center gap-1.5" onClick={(event) => event.stopPropagation()}>
            <input
              ref={editInputRef}
              data-testid="document-rename-input"
              type="text"
              value={editingName}
              onChange={(event) => setEditingName(event.target.value)}
              onKeyDown={handleEditKeyDown}
              disabled={isSavingName}
              aria-label="Nuevo nombre del documento"
              className="min-w-0 flex-1 rounded-md border border-blue-400 bg-white px-2 py-1 text-sm font-medium text-gray-900 outline-none ring-2 ring-blue-200 focus:border-blue-500 disabled:opacity-60 dark:border-blue-600 dark:bg-slate-800 dark:text-gray-100 dark:ring-blue-800"
            />
            <button
              type="button"
              onClick={handleConfirmRename}
              disabled={isSavingName}
              aria-label="Confirmar nuevo nombre"
              className="rounded-full p-1.5 text-green-600 transition-colors hover:bg-green-50 disabled:opacity-60 dark:hover:bg-green-900/30"
            >
              {isSavingName ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />}
            </button>
            <button
              type="button"
              onClick={handleCancelEditing}
              disabled={isSavingName}
              aria-label="Cancelar renombrar"
              className="rounded-full p-1.5 text-gray-500 transition-colors hover:bg-gray-100 disabled:opacity-60 dark:hover:bg-slate-700"
            >
              <X className="h-4 w-4" />
            </button>
          </div>
        ) : (
          // Modo normal: muestra el nombre clicable para previsualizar
          <div className="cursor-pointer" onClick={handlePreviewClick}>
            <p className="truncate text-sm font-medium text-gray-900 dark:text-gray-100" title={fileName}>
              {fileName}
            </p>
          </div>
        )}
        <div className="mt-0.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-gray-500 dark:text-gray-400">
          <span>{formatFileSize(fileSize)}</span>
          {uploadedAt ? (
            <span>
              {formatDateTime(uploadedAt)}
            </span>
          ) : null}
          {uploadedBy ? <span>Subido por {uploadedBy}</span> : null}
        </div>
      </div>

      <div className="flex items-center gap-1">
        {(isImage || isPdf) && !isEditing && (
          <button
            type="button"
            onClick={handlePreviewClick}
            className="rounded-full p-2 text-gray-500 transition-colors hover:bg-blue-50 hover:text-blue-600 dark:hover:bg-blue-900/30"
            title="Ver"
          >
            <Eye className="h-4 w-4" />
          </button>
        )}
        {/* Botón renombrar: solo cuando NO está en modo edición Y la reserva permite escritura.
            En solo lectura (estado terminal) renombrar es escritura → se oculta. */}
        {!isEditing && !soloLectura && (
          <button
            type="button"
            data-testid="document-rename-button"
            onClick={handleStartEditing}
            className="rounded-full p-2 text-gray-500 transition-colors hover:bg-amber-50 hover:text-amber-600 dark:hover:bg-amber-900/30"
            title="Renombrar"
            aria-label="Renombrar documento"
          >
            <Pencil className="h-4 w-4" />
          </button>
        )}
        {/* Descargar: siempre visible, incluso en solo lectura (lectura != escritura) */}
        {!isEditing && (
          <button
            type="button"
            onClick={(event) => {
              event.stopPropagation();
              onDownload(id, fileName);
            }}
            className="rounded-full p-2 text-gray-500 transition-colors hover:bg-green-50 hover:text-green-600 dark:hover:bg-green-900/30"
            title="Descargar"
          >
            <Download className="h-4 w-4" />
          </button>
        )}
        {/* Eliminar: escritura → se oculta en solo lectura */}
        {!isEditing && !soloLectura && (
          <button
            type="button"
            onClick={(event) => {
              event.stopPropagation();
              onDelete(id, fileName);
            }}
            className="rounded-full p-2 text-gray-500 transition-colors hover:bg-red-50 hover:text-red-600 dark:hover:bg-red-900/30"
            title="Eliminar"
          >
            <Trash2 className="h-4 w-4" />
          </button>
        )}
      </div>
    </div>
  );
}

/**
 * Pestaña de documentos adjuntos (DNI, pasaportes, autorizaciones, etc.) de una reserva.
 *
 * Props:
 *   reservaId         — publicId de la reserva.
 *   canUploadDocument — CapabilityDto {allowed, reason} del backend (B3, 2026-06-24).
 *                       Cuando allowed=false, se ocultan la zona de carga, el botón Renombrar
 *                       y el botón Eliminar. Ver y descargar documentos ya cargados sigue disponible.
 *                       Si no se pasa (undefined/null), se degrada mostrando todos los botones
 *                       (comportamiento anterior — compatible con DTOs viejos sin esta capability).
 */
export function ReservaDocumentsTab({ reservaId, canUploadDocument }) {
  const [attachments, setAttachments] = useState([]);
  const [loading, setLoading] = useState(true);
  const [isDragging, setIsDragging] = useState(false);
  const [uploading, setUploading] = useState(false);

  // soloLecturaDocumentos: true cuando el backend indica que no se pueden agregar/modificar docs.
  // Degradacion elegante: sin capability (DTO viejo), mostramos todo (allowed=true por defecto).
  const soloLecturaDocumentos = canUploadDocument != null && !canUploadDocument.allowed;

  const fetchAttachments = useCallback(async () => {
    try {
      setLoading(true);
      const data = await api.get(`/attachments/reserva/${reservaId}`);
      setAttachments(Array.isArray(data) ? data : []);
    } catch (error) {
      console.error("Error loading documents:", error);
      toast.error("No se pudieron cargar los documentos.");
    } finally {
      setLoading(false);
    }
  }, [reservaId]);

  useEffect(() => {
    fetchAttachments();
  }, [fetchAttachments]);

  const handleUpload = async (file) => {
    if (!file) return;
    if (file.size > 25 * 1024 * 1024) {
      toast.error("El archivo es demasiado grande (max 25 MB).");
      return;
    }

    const formData = new FormData();
    formData.append("file", file);

    try {
      setUploading(true);
      await api.post(`/attachments/upload/${reservaId}`, formData);
      toast.success("Documento subido correctamente.");
      fetchAttachments();
    } catch (error) {
      console.error("Error uploading document:", error);
      toast.error("Error al subir el documento.");
    } finally {
      setUploading(false);
    }
  };

  /**
   * Llama al PATCH /attachments/{publicId} para cambiar el nombre del archivo.
   * Actualiza el ítem en el estado local con el objeto devuelto por el backend
   * para evitar un refetch completo (más rápido para el usuario).
   */
  const handleRename = async (id, newFileName) => {
    try {
      const updated = await api.patch(`/attachments/${id}`, { fileName: newFileName });
      toast.success("Nombre actualizado.");
      // Actualizamos solo el ítem modificado en lugar de recargar toda la lista
      if (updated) {
        setAttachments((previous) =>
          previous.map((item) => (getPublicId(item) === id ? updated : item))
        );
      } else {
        // Fallback: si el backend no devuelve el objeto actualizado, recargamos
        fetchAttachments();
      }
    } catch (error) {
      console.error("Error renaming document:", error);
      toast.error(getApiErrorMessage(error, "No se pudo cambiar el nombre."));
      // Re-lanzamos para que DocumentRow sepa que falló y no cierre el input
      throw error;
    }
  };

  const handleDelete = async (id, fileName) => {
    const result = await Swal.fire({
      title: "Eliminar documento?",
      text: `Se eliminara "${fileName}" permanentemente.`,
      icon: "warning",
      showCancelButton: true,
      confirmButtonText: "Si, eliminar",
      cancelButtonText: "Cancelar",
      confirmButtonColor: "#ef4444",
    });

    if (!result.isConfirmed) return;

    try {
      await api.delete(`/attachments/${id}`);
      toast.success("Documento eliminado.");
      setAttachments((previous) => previous.filter((item) => getPublicId(item) !== id));
    } catch (error) {
      console.error("Error deleting document:", error);
      toast.error("No se pudo eliminar el documento.");
    }
  };

  const handleDownload = async (id, fileName) => {
    const loadingToast = toast.loading("Preparando descarga...");

    try {
      const blob = await api.get(`/attachments/${id}/download`, { responseType: "blob" });
      if (!blob || blob.size === 0) {
        throw new Error("El archivo descargado esta vacio o corrupto.");
      }

      downloadBlob(blob, fileName);
      toast.dismiss(loadingToast);
      toast.success("Descarga iniciada.");
    } catch (error) {
      console.error("Download error:", error);
      toast.dismiss(loadingToast);
      toast.error(`Error al descargar: ${error.message || "Error desconocido"}`);
    }
  };

  return (
    <div className="space-y-6">
      {/* Zona de carga: solo cuando la reserva permite agregar documentos (canUploadDocument.allowed).
          En estados terminales (Finalizada/Anulada/Perdida/Esperando reembolso) el backend manda
          canUploadDocument.allowed=false y esta zona se oculta (B3, 2026-06-24).
          La lista de documentos ya cargados sigue visible debajo para poder descargarlos. */}
      {!soloLecturaDocumentos && (
        <div
          className={`relative cursor-pointer overflow-hidden rounded-xl border-2 border-dashed p-8 text-center transition-all ${
            isDragging
              ? "border-blue-500 bg-blue-50 dark:bg-blue-900/20"
              : "border-gray-300 bg-gray-50 hover:border-gray-400 dark:border-slate-700 dark:bg-slate-800/50"
          }`}
          onDragOver={(event) => {
            event.preventDefault();
            setIsDragging(true);
          }}
          onDragLeave={(event) => {
            event.preventDefault();
            setIsDragging(false);
          }}
          onDrop={async (event) => {
            event.preventDefault();
            setIsDragging(false);
            if (event.dataTransfer.files.length > 0) {
              await handleUpload(event.dataTransfer.files[0]);
            }
          }}
          onClick={() => document.getElementById("reservationDocumentInput")?.click()}
        >
          <input
            id="reservationDocumentInput"
            type="file"
            className="hidden"
            onChange={async (event) => {
              if (event.target.files?.length > 0) {
                await handleUpload(event.target.files[0]);
                event.target.value = "";
              }
            }}
            accept="image/*,application/pdf,.doc,.docx,.xls,.xlsx"
          />
          <div className="relative z-10 flex flex-col items-center justify-center space-y-3">
            {uploading ? (
              <Loader2 className="h-12 w-12 animate-spin text-blue-500" />
            ) : (
              <div className="rounded-full bg-white p-3 shadow-sm dark:bg-slate-800">
                <UploadCloud className={`h-8 w-8 ${isDragging ? "text-blue-500" : "text-gray-400"}`} />
              </div>
            )}
            <div>
              <div className="text-base font-medium text-gray-900 dark:text-gray-100">
                {uploading ? "Subiendo documento..." : "Haz clic o arrastra documentos aqui"}
              </div>
              <p className="mt-1 text-xs text-gray-500 dark:text-gray-400">
                DNI, pasaportes, permisos, autorizaciones y adjuntos generales (max 25 MB)
              </p>
            </div>
          </div>
        </div>
      )}

      <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm dark:border-slate-700 dark:bg-slate-800">
        <div className="flex items-center justify-between border-b border-gray-200 bg-gray-50 px-4 py-3 dark:border-slate-700 dark:bg-slate-900/50">
          <h3 className="flex items-center text-sm font-semibold text-gray-700 dark:text-gray-200">
            <Paperclip className="mr-2 h-4 w-4" />
            Documentos <span className="ml-1 text-xs font-normal text-gray-500">({attachments?.length || 0})</span>
          </h3>
        </div>

        {loading ? (
          <div className="flex justify-center p-8">
            <Loader2 className="h-8 w-8 animate-spin text-blue-500" />
          </div>
        ) : (attachments?.length || 0) === 0 ? (
          <div className="py-12 text-center text-sm text-gray-500">
            <FileText className="mx-auto mb-3 h-10 w-10 text-gray-300" />
            <p>No hay documentos cargados todavia.</p>
            {/* En solo lectura informamos que no se pueden agregar documentos nuevos */}
            {soloLecturaDocumentos && (
              <p className="mt-1 text-xs text-slate-400">
                Esta reserva está en solo lectura: no se pueden agregar documentos nuevos.
              </p>
            )}
          </div>
        ) : (
          <div className="divide-y divide-gray-100 dark:divide-slate-700">
            {attachments.map((file) => (
              <DocumentRow
                key={getPublicId(file)}
                file={file}
                onDelete={handleDelete}
                onDownload={handleDownload}
                onRename={handleRename}
                soloLectura={soloLecturaDocumentos}
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}

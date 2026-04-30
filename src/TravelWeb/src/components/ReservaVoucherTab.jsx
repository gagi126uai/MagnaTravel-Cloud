import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, Ban, CheckCircle2, Download, Eye, FilePlus2, FileText, Loader2, UploadCloud, Plus, X, ThumbsUp, ThumbsDown } from "lucide-react";
import { toast } from "sonner";
import { api } from "../api";
import { getApiErrorMessage } from "../lib/errors";
import { getPublicId } from "../lib/publicIds";
import { useAuthState, isAdmin, hasPermission } from "../auth";

function getPassengerName(passenger) {
  return passenger?.fullName || passenger?.FullName || passenger?.name || passenger?.Name || "Pasajero";
}

function getPassengerId(passenger) {
  return getPublicId(passenger);
}

function formatDateTime(value) {
  if (!value) return "-";
  return `${new Date(value).toLocaleDateString()} ${new Date(value).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`;
}

function formatMoney(value) {
  return Number(value || 0).toLocaleString("es-AR", { style: "currency", currency: "ARS" });
}

function formatStatus(status) {
  switch (status) {
    case "Draft":
      return "Borrador";
    case "PendingAuthorization":
      return "Pendiente Autorización";
    case "Issued":
      return "Emitido";
    case "UploadedExternal":
      return "Cargado Externo";
    case "Revoked":
      return "Anulado";
    default:
      return status || "Sin estado";
  }
}

function formatScope(scope) {
  if (scope === "ReservaCompleta") return "Toda la reserva";
  if (scope === "TodosLosPasajeros") return "Todos los pasajeros";
  if (scope === "PasajerosSeleccionados") return "Pasajeros específicos";
  return scope || "Toda la reserva";
}

function normalizeVoucher(item) {
  return {
    publicId: item.publicId || item.PublicId,
    source: item.source || item.Source,
    status: item.status || item.Status,
    scope: item.scope || item.Scope,
    fileName: item.fileName || item.FileName || "voucher.pdf",
    externalOrigin: item.externalOrigin || item.ExternalOrigin,
    isEnabledForSending: item.isEnabledForSending ?? item.IsEnabledForSending ?? false,
    canSend: item.canSend ?? item.CanSend ?? false,
    reservationHasOutstandingBalance: item.reservationHasOutstandingBalance ?? item.ReservationHasOutstandingBalance ?? false,
    outstandingBalance: item.outstandingBalance ?? item.OutstandingBalance ?? 0,
    createdByUserName: item.createdByUserName || item.CreatedByUserName,
    createdAt: item.createdAt || item.CreatedAt,
    issuedByUserName: item.issuedByUserName || item.IssuedByUserName,
    issuedAt: item.issuedAt || item.IssuedAt,
    wasExceptionalIssue: item.wasExceptionalIssue ?? item.WasExceptionalIssue ?? false,
    exceptionalReason: item.exceptionalReason || item.ExceptionalReason,
    authorizedBySuperiorUserName: item.authorizedBySuperiorUserName || item.AuthorizedBySuperiorUserName,
    authorizationStatus: item.authorizationStatus || item.AuthorizationStatus,
    rejectReason: item.rejectReason || item.RejectReason,
    revokedAt: item.revokedAt || item.RevokedAt,
    revokedByUserId: item.revokedByUserId || item.RevokedByUserId,
    revokedByUserName: item.revokedByUserName || item.RevokedByUserName,
    revocationReason: item.revocationReason || item.RevocationReason,
    passengerPublicIds: item.passengerPublicIds || item.PassengerPublicIds || [],
    passengerNames: item.passengerNames || item.PassengerNames || [],
    authorizedBySuperiorUserId: item.authorizedBySuperiorUserId || item.AuthorizedBySuperiorUserId,
  };
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

function getPreviewKind(fileName, contentType) {
  const normalizedType = (contentType || "").toLowerCase();
  const normalizedName = (fileName || "").toLowerCase();

  if (normalizedType.includes("pdf") || normalizedName.endsWith(".pdf")) {
    return "pdf";
  }

  if (
    normalizedType.startsWith("image/") ||
    [".png", ".jpg", ".jpeg", ".webp", ".gif"].some((extension) => normalizedName.endsWith(extension))
  ) {
    return "image";
  }

  return "unsupported";
}

function ScopeSelector({ label, value, selectedPassengerIds, passengers, onScopeChange, onPassengersChange }) {
  const togglePassenger = (passengerId) => {
    onPassengersChange(
      selectedPassengerIds.includes(passengerId)
        ? selectedPassengerIds.filter((id) => id !== passengerId)
        : [...selectedPassengerIds, passengerId]
    );
  };

  return (
    <div className="space-y-3">
      <label className="text-[11px] font-black uppercase tracking-widest text-slate-400">{label}</label>
      <select
        value={value}
        onChange={(event) => onScopeChange(event.target.value)}
        className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
      >
        <option value="ReservaCompleta">Toda la reserva</option>
        <option value="TodosLosPasajeros">Todos los pasajeros</option>
        <option value="PasajerosSeleccionados">Pasajeros seleccionados</option>
      </select>

      {value === "PasajerosSeleccionados" ? (
        <div className="grid gap-2 sm:grid-cols-2 mt-2">
          {passengers.length === 0 ? (
            <div className="rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs font-semibold text-amber-800 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-200">
              Aún no has agregado pasajeros a esta reserva.
            </div>
          ) : (
            passengers.map((passenger) => {
              const passengerId = getPassengerId(passenger);
              return (
                <label
                  key={passengerId}
                  className="flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm font-semibold text-slate-700 dark:border-slate-700 dark:bg-slate-900/60 dark:text-slate-200"
                >
                  <input
                    type="checkbox"
                    checked={selectedPassengerIds.includes(passengerId)}
                    onChange={() => togglePassenger(passengerId)}
                    className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                  />
                  <span className="truncate">{getPassengerName(passenger)}</span>
                </label>
              );
            })
          )}
        </div>
      ) : null}
    </div>
  );
}

function Modal({ isOpen, onClose, title, children }) {
  if (!isOpen) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="absolute inset-0 bg-slate-900/40 backdrop-blur-sm" />
      <div className="relative w-full max-w-lg rounded-2xl bg-white p-6 shadow-2xl ring-1 ring-slate-900/5 dark:bg-slate-900 dark:ring-slate-50/10">
        <div className="mb-5 flex items-center justify-between">
          <h2 className="text-lg font-black text-slate-900 dark:text-white">{title}</h2>
          <button
            onClick={onClose}
            className="rounded-full p-2 text-slate-400 hover:bg-slate-100 hover:text-slate-600 dark:hover:bg-slate-800 dark:hover:text-slate-300"
          >
            <X className="h-5 w-5" />
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}

export function ReservaVoucherTab({ reservaId, reserva }) {
  const { user } = useAuthState();
  const passengers = useMemo(() => (Array.isArray(reserva?.passengers) ? reserva.passengers : []), [reserva]);
  const [vouchers, setVouchers] = useState([]);
  const [loading, setLoading] = useState(true);
  const [documentView, setDocumentView] = useState("active");

  // Modal states
  const [isAddModalOpen, setIsAddModalOpen] = useState(false);
  const [addMode, setAddMode] = useState("select"); // select, generate, upload

  const [generating, setGenerating] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [issuingId, setIssuingId] = useState(null);
  const [downloadingId, setDownloadingId] = useState(null);
  const [previewingId, setPreviewingId] = useState(null);
  const [previewDocument, setPreviewDocument] = useState(null);
  const [processingAuthId, setProcessingAuthId] = useState(null);

  // Form states
  const [scope, setScope] = useState("ReservaCompleta");
  const [selectedPassengerIds, setSelectedPassengerIds] = useState([]);
  const [externalOrigin, setExternalOrigin] = useState("Operador externo");
  const [externalFile, setExternalFile] = useState(null);

  // Authorization Issue Modal
  const [isAuthModalOpen, setIsAuthModalOpen] = useState(false);
  const [voucherToIssue, setVoucherToIssue] = useState(null);
  const [issueReason, setIssueReason] = useState("");
  const [exceptionalReason, setExceptionalReason] = useState("");
  const [authorizedBySuperiorUserId, setAuthorizedBySuperiorUserId] = useState("");
  const [supervisors, setSupervisors] = useState([]);

  // Reject Modal
  const [isRejectModalOpen, setIsRejectModalOpen] = useState(false);
  const [voucherToReject, setVoucherToReject] = useState(null);
  const [rejectReason, setRejectReason] = useState("");

  // Revoke Modal
  const [isRevokeModalOpen, setIsRevokeModalOpen] = useState(false);
  const [voucherToRevoke, setVoucherToRevoke] = useState(null);
  const [revokeReason, setRevokeReason] = useState("");
  const [revokingId, setRevokingId] = useState(null);

  const outstandingBalance = Number(reserva?.balance ?? 0) > 0;
  const activeVouchers = useMemo(() => vouchers.filter((voucher) => voucher.status !== "Revoked"), [vouchers]);
  const revokedVouchers = useMemo(() => vouchers.filter((voucher) => voucher.status === "Revoked"), [vouchers]);
  const visibleVouchers = documentView === "revoked" ? revokedVouchers : activeVouchers;

  useEffect(() => {
    if (outstandingBalance) {
      api.get("/users/supervisors").then(data => {
        setSupervisors(Array.isArray(data) ? data : []);
      }).catch(err => console.error("No se pudieron cargar supervisores", err));
    }
  }, [outstandingBalance]);

  const fetchVouchers = useCallback(async () => {
    if (!reservaId) return;
    try {
      setLoading(true);
      const data = await api.get(`/reservas/${reservaId}/vouchers`);
      setVouchers(Array.isArray(data) ? data.map(normalizeVoucher) : []);
    } catch (error) {
      console.error("Error loading vouchers:", error);
      toast.error(getApiErrorMessage(error, "No se pudieron cargar los documentos."));
    } finally {
      setLoading(false);
    }
  }, [reservaId]);

  useEffect(() => {
    fetchVouchers();
  }, [fetchVouchers]);

  useEffect(() => {
    if (documentView === "revoked" && revokedVouchers.length === 0) {
      setDocumentView("active");
    }
  }, [documentView, revokedVouchers.length]);

  useEffect(() => () => {
    if (previewDocument?.url) {
      window.URL.revokeObjectURL(previewDocument.url);
    }
  }, [previewDocument?.url]);

  const validateScope = () => {
    if (scope === "PasajerosSeleccionados" && selectedPassengerIds.length === 0) {
      toast.error("Selecciona al menos un pasajero para este alcance.");
      return false;
    }
    if (scope === "TodosLosPasajeros" && passengers.length === 0) {
      toast.error("La reserva no tiene pasajeros para asociar.");
      return false;
    }
    return true;
  };

  const resetAddModal = () => {
    setAddMode("select");
    setScope("ReservaCompleta");
    setSelectedPassengerIds([]);
    setExternalOrigin("Operador externo");
    setExternalFile(null);
  };

  const handleGenerate = async () => {
    if (!validateScope()) return;
    try {
      setGenerating(true);
      await api.post(`/reservas/${reservaId}/vouchers/generate`, {
        scope,
        passengerIds: scope === "PasajerosSeleccionados" ? selectedPassengerIds : [],
      });
      toast.success("Documento generado exitosamente.");
      setIsAddModalOpen(false);
      resetAddModal();
      await fetchVouchers();
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo generar el documento."));
    } finally {
      setGenerating(false);
    }
  };

  const handleUploadExternal = async () => {
    if (!externalFile) {
      toast.error("Selecciona el archivo del documento externo.");
      return;
    }
    if (!externalOrigin.trim()) {
      toast.error("Indica el origen del documento externo.");
      return;
    }
    if (!validateScope()) return;

    const formData = new FormData();
    formData.append("file", externalFile);
    formData.append("scope", scope);
    formData.append("externalOrigin", externalOrigin.trim());
    if (scope === "PasajerosSeleccionados") {
      selectedPassengerIds.forEach((pid) => formData.append("passengerIds", pid));
    }

    try {
      setUploading(true);
      await api.post(`/reservas/${reservaId}/vouchers/external`, formData);
      toast.success("Documento externo cargado exitosamente.");
      setIsAddModalOpen(false);
      resetAddModal();
      await fetchVouchers();
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo cargar el documento."));
    } finally {
      setUploading(false);
    }
  };

  const promptIssue = (voucher) => {
    setVoucherToIssue(voucher);
    setIssueReason("");
    setExceptionalReason("");
    setAuthorizedBySuperiorUserId("");
    if (outstandingBalance && !isAdmin()) {
      setIsAuthModalOpen(true);
    } else {
      executeIssue(voucher.publicId, null, null, "");
    }
  };

  const handleAuthSubmit = () => {
    if (exceptionalReason.trim().length < 10) {
      toast.error("Para emitir con saldo pendiente, indica una justificación de al menos 10 caracteres.");
      return;
    }
    if (!authorizedBySuperiorUserId) {
      toast.error("Selecciona el supervisor que debe autorizar esta emisión.");
      return;
    }
    executeIssue(voucherToIssue.publicId, exceptionalReason.trim(), authorizedBySuperiorUserId, issueReason.trim());
  };

  const executeIssue = async (voucherId, exReason, authUserId, normalReason) => {
    try {
      setIssuingId(voucherId);
      setIsAuthModalOpen(false);
      const updated = await api.post(`/vouchers/${voucherId}/issue`, {
        reason: normalReason || null,
        exceptionalReason: exReason || null,
        authorizedBySuperiorUserId: authUserId || null,
      });
      if (exReason && authUserId && !isAdmin()) {
        toast.success("Solicitud de autorización enviada al supervisor.");
      } else {
        toast.success("Documento emitido correctamente.");
      }
      if (updated) {
        const normalized = normalizeVoucher(updated);
        setVouchers(prev => prev.map(v => v.publicId === normalized.publicId ? normalized : v));
      } else {
        await fetchVouchers();
      }
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo procesar la emisión."));
    } finally {
      setIssuingId(null);
      setVoucherToIssue(null);
    }
  };

  const handleApprove = async (voucher) => {
    try {
      setProcessingAuthId(voucher.publicId);
      const updated = await api.post(`/vouchers/${voucher.publicId}/approve`);
      toast.success("Emisión autorizada correctamente.");
      if (updated) {
        const normalized = normalizeVoucher(updated);
        setVouchers(prev => prev.map(v => v.publicId === normalized.publicId ? normalized : v));
      } else {
        await fetchVouchers();
      }
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo autorizar."));
    } finally {
      setProcessingAuthId(null);
    }
  };

  const handleRejectSubmit = async () => {
    if (rejectReason.trim().length < 10) {
      toast.error("Por favor, indica un motivo de rechazo válido.");
      return;
    }
    try {
      setProcessingAuthId(voucherToReject.publicId);
      setIsRejectModalOpen(false);
      const updated = await api.post(`/vouchers/${voucherToReject.publicId}/reject`, { reason: rejectReason.trim() });
      toast.success("Solicitud rechazada correctamente.");
      if (updated) {
        const normalized = normalizeVoucher(updated);
        setVouchers(prev => prev.map(v => v.publicId === normalized.publicId ? normalized : v));
      } else {
        await fetchVouchers();
      }
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo rechazar."));
    } finally {
      setProcessingAuthId(null);
      setVoucherToReject(null);
      setRejectReason("");
    }
  };

  const handleRevokeSubmit = async () => {
    if (!isAdmin() && revokeReason.trim().length < 10) {
      toast.error("Indica un motivo de anulacion de al menos 10 caracteres.");
      return;
    }
    try {
      setRevokingId(voucherToRevoke.publicId);
      setIsRevokeModalOpen(false);
      const updated = await api.post(`/vouchers/${voucherToRevoke.publicId}/revoke`, { reason: revokeReason.trim() });
      toast.success("Documento anulado correctamente.");
      if (updated) {
        const normalized = normalizeVoucher(updated);
        setVouchers(prev => prev.map(v => v.publicId === normalized.publicId ? normalized : v));
      } else {
        await fetchVouchers();
      }
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo anular el documento."));
    } finally {
      setRevokingId(null);
      setVoucherToRevoke(null);
      setRevokeReason("");
    }
  };

  const handleDownload = async (voucher) => {
    try {
      setDownloadingId(voucher.publicId);
      const blob = await api.get(`/vouchers/${voucher.publicId}/download`, { responseType: "blob" });
      downloadBlob(blob, voucher.fileName || `documento-${reservaId}.pdf`);
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo descargar el documento."));
    } finally {
      setDownloadingId(null);
    }
  };

  const closePreview = () => {
    if (previewDocument?.url) {
      window.URL.revokeObjectURL(previewDocument.url);
    }
    setPreviewDocument(null);
  };

  const handlePreview = async (voucher) => {
    try {
      setPreviewingId(voucher.publicId);
      if (previewDocument?.url) {
        window.URL.revokeObjectURL(previewDocument.url);
      }

      const blob = await api.get(`/vouchers/${voucher.publicId}/download`, { responseType: "blob" });
      const url = window.URL.createObjectURL(blob);
      const contentType = blob.type || voucher.contentType;
      setPreviewDocument({
        voucher,
        url,
        contentType,
        kind: getPreviewKind(voucher.fileName, contentType),
      });
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo abrir la vista previa."));
    } finally {
      setPreviewingId(null);
    }
  };

  return (
    <div className="space-y-6">
      {/* HEADER LIMPIO */}
      <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-xl font-black text-slate-900 dark:text-white">Documentación</h2>
          <p className="text-sm font-semibold text-slate-500 dark:text-slate-400">
            Gestiona vouchers generados por el sistema y archivos cargados externamente.
          </p>
        </div>
        <button
          type="button"
          onClick={() => {
            resetAddModal();
            setIsAddModalOpen(true);
          }}
          className="inline-flex items-center justify-center gap-2 rounded-xl bg-indigo-600 px-5 py-2.5 text-sm font-black text-white shadow-sm transition hover:bg-indigo-700"
        >
          <Plus className="h-4 w-4" />
          Añadir Documento
        </button>
      </div>

      {/* LISTA PRINCIPAL (GESTIÓN DOCUMENTAL) */}
      {revokedVouchers.length > 0 ? (
        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={() => setDocumentView("active")}
            className={`rounded-xl px-4 py-2 text-xs font-black uppercase tracking-widest transition ${
              documentView === "active"
                ? "bg-slate-900 text-white shadow-sm dark:bg-white dark:text-slate-900"
                : "bg-slate-100 text-slate-500 hover:bg-slate-200 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700"
            }`}
          >
            Vigentes ({activeVouchers.length})
          </button>
          <button
            type="button"
            onClick={() => setDocumentView("revoked")}
            className={`rounded-xl px-4 py-2 text-xs font-black uppercase tracking-widest transition ${
              documentView === "revoked"
                ? "bg-rose-600 text-white shadow-sm"
                : "bg-rose-50 text-rose-700 hover:bg-rose-100 dark:bg-rose-900/20 dark:text-rose-300 dark:hover:bg-rose-900/30"
            }`}
          >
            Anulados ({revokedVouchers.length})
          </button>
        </div>
      ) : null}

      {documentView === "revoked" ? (
        <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-xs font-semibold text-rose-900 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-200">
          Estos documentos estan anulados y se conservan solo como trazabilidad. No se pueden emitir, aprobar, rechazar ni enviar.
        </div>
      ) : null}

      <div className="rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
        {loading ? (
          <div className="flex justify-center p-12">
            <Loader2 className="h-8 w-8 animate-spin text-indigo-500" />
          </div>
        ) : visibleVouchers.length === 0 ? (
          <div className="py-16 text-center text-sm text-slate-500">
            <FileText className="mx-auto mb-4 h-12 w-12 text-slate-300" />
            <span className="font-semibold">
              {documentView === "revoked" ? "No hay documentos anulados." : "No hay documentos vigentes en esta reserva."}
            </span>
            <p className="mt-1 text-xs">
              {documentView === "revoked"
                ? "Los documentos anulados se mostraran aca cuando existan."
                : revokedVouchers.length > 0
                ? "Revisa la solapa Anulados para ver documentos historicos."
                : "Añade uno usando el botón superior derecho."}
            </p>
          </div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {visibleVouchers.map((voucher) => (
              <div key={voucher.publicId} className={`p-5 transition hover:bg-slate-50/50 dark:hover:bg-slate-800/30 ${voucher.status === "Revoked" ? "bg-rose-50/30 dark:bg-rose-950/10" : ""}`}>
                <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
                  <div className="min-w-0 space-y-2.5">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className={`rounded-full px-2.5 py-1 text-[10px] font-black uppercase tracking-widest ${
                        voucher.status === "PendingAuthorization" 
                        ? "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-300"
                        : voucher.status === "Revoked"
                        ? "bg-rose-100 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300"
                        : voucher.status === "Draft"
                        ? "bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300"
                        : "bg-indigo-50 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300"
                      }`}>
                        {formatStatus(voucher.status)}
                      </span>
                      <span className="rounded-full bg-slate-100 px-2.5 py-1 text-[10px] font-black uppercase tracking-widest text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                        {formatScope(voucher.scope)}
                      </span>
                      {voucher.canSend ? (
                        <span className="inline-flex items-center gap-1 rounded-full bg-emerald-50 px-2.5 py-1 text-[10px] font-black uppercase tracking-widest text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300">
                          <CheckCircle2 className="h-3 w-3" />
                          Enviable
                        </span>
                      ) : null}
                    </div>
                    
                    <div className="flex items-center gap-3">
                      <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-400">
                        {voucher.source === "Generated" ? <FilePlus2 className="h-5 w-5" /> : <UploadCloud className="h-5 w-5" />}
                      </div>
                      <div className="truncate text-base font-black text-slate-900 dark:text-white">
                        {voucher.fileName}
                      </div>
                    </div>

                    <div className="flex flex-wrap gap-x-5 gap-y-1.5 text-xs font-semibold text-slate-500 dark:text-slate-400">
                      <span>Creado: {formatDateTime(voucher.createdAt)} {voucher.createdByUserName ? `por ${voucher.createdByUserName}` : ""}</span>
                      {voucher.issuedAt ? <span>Emitido: {formatDateTime(voucher.issuedAt)} {voucher.issuedByUserName ? `por ${voucher.issuedByUserName}` : ""}</span> : null}
                      {voucher.externalOrigin ? <span>Origen: {voucher.externalOrigin}</span> : null}
                    </div>
                    
                    {voucher.passengerNames.length > 0 ? (
                      <div className="text-xs font-semibold text-slate-600 dark:text-slate-300">
                        <span className="text-slate-400">Pasajeros: </span>{voucher.passengerNames.join(", ")}
                      </div>
                    ) : null}
                    
                    {voucher.exceptionalReason ? (
                      <div className="rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs font-semibold text-amber-900 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-200">
                        Autorización solicitada a {voucher.authorizedBySuperiorUserName || "Supervisor"} por: {voucher.exceptionalReason}
                      </div>
                    ) : null}
                    {voucher.rejectReason ? (
                      <div className="rounded-xl border border-red-200 bg-red-50 px-3 py-2 text-xs font-semibold text-red-900 dark:border-red-900/40 dark:bg-red-900/20 dark:text-red-200">
                        Rechazado: {voucher.rejectReason}
                      </div>
                    ) : null}
                    {voucher.revocationReason ? (
                      <div className="rounded-xl border border-rose-200 bg-rose-50 px-3 py-2 text-xs font-semibold text-rose-900 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-200">
                        Anulado{voucher.revokedAt ? `: ${formatDateTime(voucher.revokedAt)}` : ""}{voucher.revokedByUserName ? ` por ${voucher.revokedByUserName}` : ""}. Motivo: {voucher.revocationReason}
                      </div>
                    ) : null}
                  </div>

                  <div className="flex flex-wrap gap-2 lg:justify-end shrink-0 pt-2 lg:pt-0">
                    <button
                      type="button"
                      onClick={() => handlePreview(voucher)}
                      disabled={previewingId === voucher.publicId}
                      className="inline-flex items-center justify-center gap-2 rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-bold text-slate-700 transition hover:bg-slate-50 disabled:opacity-60 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                    >
                      {previewingId === voucher.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <Eye className="h-4 w-4" />}
                      Ver
                    </button>
                    <button
                      type="button"
                      onClick={() => handleDownload(voucher)}
                      disabled={downloadingId === voucher.publicId}
                      className="inline-flex items-center justify-center gap-2 rounded-xl border border-slate-200 px-4 py-2.5 text-sm font-bold text-slate-700 transition hover:bg-slate-50 disabled:opacity-60 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                    >
                      {downloadingId === voucher.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
                      Descargar
                    </button>
                    {voucher.status === "Draft" ? (
                      <button
                        type="button"
                        onClick={() => promptIssue(voucher)}
                        disabled={issuingId === voucher.publicId}
                        className="inline-flex items-center justify-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-bold text-white shadow-sm transition hover:bg-slate-800 disabled:opacity-60 dark:bg-white dark:text-slate-900 dark:hover:bg-slate-100"
                      >
                        {issuingId === voucher.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
                        Emitir
                      </button>
                    ) : null}
                    
                    {voucher.status === "PendingAuthorization" && (isAdmin() || user?.id === voucher.authorizedBySuperiorUserId) ? (
                      <>
                        <button
                          type="button"
                          onClick={() => handleApprove(voucher)}
                          disabled={processingAuthId === voucher.publicId}
                          className="inline-flex items-center justify-center gap-2 rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-bold text-white shadow-sm transition hover:bg-emerald-700 disabled:opacity-60"
                        >
                          {processingAuthId === voucher.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <ThumbsUp className="h-4 w-4" />}
                          Aprobar
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            setVoucherToReject(voucher);
                            setRejectReason("");
                            setIsRejectModalOpen(true);
                          }}
                          disabled={processingAuthId === voucher.publicId}
                          className="inline-flex items-center justify-center gap-2 rounded-xl bg-red-600 px-4 py-2.5 text-sm font-bold text-white shadow-sm transition hover:bg-red-700 disabled:opacity-60"
                        >
                          {processingAuthId === voucher.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <ThumbsDown className="h-4 w-4" />}
                          Rechazar
                        </button>
                      </>
                    ) : null}

                    {voucher.status !== "Revoked" && hasPermission("vouchers.revoke") ? (
                      <button
                        type="button"
                        onClick={() => {
                          setVoucherToRevoke(voucher);
                          setRevokeReason("");
                          setIsRevokeModalOpen(true);
                        }}
                        disabled={revokingId === voucher.publicId}
                        className="inline-flex items-center justify-center gap-2 rounded-xl border border-rose-200 px-4 py-2.5 text-sm font-bold text-rose-700 transition hover:bg-rose-50 disabled:opacity-60 dark:border-rose-900/50 dark:text-rose-300 dark:hover:bg-rose-900/20"
                      >
                        {revokingId === voucher.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <Ban className="h-4 w-4" />}
                        Anular
                      </button>
                    ) : null}
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* MODAL DE AÑADIR DOCUMENTO */}
      <Modal 
        isOpen={isAddModalOpen} 
        onClose={() => setIsAddModalOpen(false)} 
        title={addMode === "select" ? "Añadir Documento" : addMode === "generate" ? "Generar Documento del Sistema" : "Subir Documento Externo"}
      >
        {addMode === "select" ? (
          <div className="grid gap-4 sm:grid-cols-2">
            <button
              onClick={() => setAddMode("generate")}
              className="flex flex-col items-center justify-center gap-3 rounded-2xl border border-slate-200 bg-white p-6 transition hover:border-indigo-300 hover:bg-indigo-50 hover:shadow-sm dark:border-slate-700 dark:bg-slate-800 dark:hover:border-indigo-700 dark:hover:bg-slate-800"
            >
              <div className="flex h-12 w-12 items-center justify-center rounded-full bg-indigo-100 text-indigo-600 dark:bg-indigo-900/30 dark:text-indigo-400">
                <FilePlus2 className="h-6 w-6" />
              </div>
              <div className="text-center">
                <div className="text-sm font-black text-slate-900 dark:text-white">Generar Sistema</div>
                <div className="mt-1 text-xs font-medium text-slate-500">Crea un voucher automáticamente usando los datos de la reserva</div>
              </div>
            </button>
            <button
              onClick={() => setAddMode("upload")}
              className="flex flex-col items-center justify-center gap-3 rounded-2xl border border-slate-200 bg-white p-6 transition hover:border-emerald-300 hover:bg-emerald-50 hover:shadow-sm dark:border-slate-700 dark:bg-slate-800 dark:hover:border-emerald-700 dark:hover:bg-slate-800"
            >
              <div className="flex h-12 w-12 items-center justify-center rounded-full bg-emerald-100 text-emerald-600 dark:bg-emerald-900/30 dark:text-emerald-400">
                <UploadCloud className="h-6 w-6" />
              </div>
              <div className="text-center">
                <div className="text-sm font-black text-slate-900 dark:text-white">Subir Externo</div>
                <div className="mt-1 text-xs font-medium text-slate-500">Carga un documento en formato PDF o imagen emitido por un tercero</div>
              </div>
            </button>
          </div>
        ) : (
          <div className="space-y-5">
            <ScopeSelector
              label="Alcance del Documento"
              value={scope}
              selectedPassengerIds={selectedPassengerIds}
              passengers={passengers}
              onScopeChange={setScope}
              onPassengersChange={setSelectedPassengerIds}
            />

            {addMode === "upload" && (
              <div className="space-y-4">
                <div>
                  <label className="mb-1.5 block text-[11px] font-black uppercase tracking-widest text-slate-400">Origen Externo</label>
                  <input
                    value={externalOrigin}
                    onChange={(event) => setExternalOrigin(event.target.value)}
                    className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-emerald-500 focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
                    placeholder="Ej. Despegar, Aerolíneas..."
                  />
                </div>
                <div>
                  <label className="mb-1.5 block text-[11px] font-black uppercase tracking-widest text-slate-400">Archivo</label>
                  <input
                    type="file"
                    onChange={(event) => setExternalFile(event.target.files?.[0] || null)}
                    accept="image/*,application/pdf,.doc,.docx,.xls,.xlsx"
                    className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-1.5 file:text-xs file:font-bold file:text-slate-700 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:file:bg-slate-800 dark:file:text-slate-200"
                  />
                </div>
              </div>
            )}

            <div className="flex justify-end gap-3 pt-2">
              <button
                type="button"
                onClick={() => setAddMode("select")}
                className="rounded-xl px-4 py-2.5 text-sm font-bold text-slate-600 transition hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
              >
                Volver
              </button>
              {addMode === "generate" ? (
                <button
                  type="button"
                  onClick={handleGenerate}
                  disabled={generating}
                  className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-5 py-2.5 text-sm font-black text-white transition hover:bg-indigo-700 disabled:opacity-60"
                >
                  {generating ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                  Generar
                </button>
              ) : (
                <button
                  type="button"
                  onClick={handleUploadExternal}
                  disabled={uploading}
                  className="inline-flex items-center gap-2 rounded-xl bg-emerald-600 px-5 py-2.5 text-sm font-black text-white transition hover:bg-emerald-700 disabled:opacity-60"
                >
                  {uploading ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
                  Cargar
                </button>
              )}
            </div>
          </div>
        )}
      </Modal>

      {/* MODAL DE AUTORIZACIÓN COMERCIAL */}
      <Modal 
        isOpen={isAuthModalOpen} 
        onClose={() => setIsAuthModalOpen(false)} 
        title="Autorización Comercial Requerida"
      >
        <div className="space-y-4">
          <div className="flex items-start gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-amber-900 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-200">
            <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0" />
            <div>
              <div className="text-sm font-black">Cobro Pendiente: {formatMoney(reserva?.balance)}</div>
              <div className="mt-1 text-xs font-medium">
                Esta reserva tiene un saldo deudor. Debes solicitar autorización a un supervisor para emitir los documentos.
              </div>
            </div>
          </div>

          <div>
            <label className="mb-1.5 block text-[11px] font-black uppercase tracking-widest text-slate-400">Justificación</label>
            <textarea
              value={exceptionalReason}
              onChange={(event) => setExceptionalReason(event.target.value)}
              rows={3}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-amber-500 focus:ring-2 focus:ring-amber-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
              placeholder="¿Por qué se deben entregar los documentos sin haber cobrado?"
            />
          </div>

          <div>
            <label className="mb-1.5 block text-[11px] font-black uppercase tracking-widest text-slate-400">Supervisor que Autoriza</label>
            <select
              value={authorizedBySuperiorUserId}
              onChange={(event) => setAuthorizedBySuperiorUserId(event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-amber-500 focus:ring-2 focus:ring-amber-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
            >
              <option value="">Selecciona el Supervisor...</option>
              {supervisors.map(sup => (
                <option key={sup.id} value={sup.id}>{sup.fullName || sup.email}</option>
              ))}
            </select>
          </div>

          <div className="flex justify-end gap-3 pt-3">
            <button
              type="button"
              onClick={() => setIsAuthModalOpen(false)}
              className="rounded-xl px-4 py-2.5 text-sm font-bold text-slate-600 transition hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
            >
              Cancelar
            </button>
            <button
              type="button"
              onClick={handleAuthSubmit}
              disabled={issuingId === voucherToIssue?.publicId}
              className="inline-flex items-center gap-2 rounded-xl bg-amber-600 px-5 py-2.5 text-sm font-black text-white transition hover:bg-amber-700 disabled:opacity-60"
            >
              {issuingId === voucherToIssue?.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              Solicitar Autorización
            </button>
          </div>
        </div>
      </Modal>

      {/* MODAL RECHAZAR */}
      <Modal 
        isOpen={isRejectModalOpen} 
        onClose={() => setIsRejectModalOpen(false)} 
        title="Rechazar Autorización"
      >
        <div className="space-y-4">
          <div>
            <label className="mb-1.5 block text-[11px] font-black uppercase tracking-widest text-slate-400">Motivo de Rechazo</label>
            <textarea
              value={rejectReason}
              onChange={(event) => setRejectReason(event.target.value)}
              rows={3}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-red-500 focus:ring-2 focus:ring-red-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
              placeholder="Indica al vendedor por qué no autorizas la emisión..."
            />
          </div>
          <div className="flex justify-end gap-3 pt-3">
            <button
              type="button"
              onClick={() => setIsRejectModalOpen(false)}
              className="rounded-xl px-4 py-2.5 text-sm font-bold text-slate-600 transition hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
            >
              Cancelar
            </button>
            <button
              type="button"
              onClick={handleRejectSubmit}
              disabled={processingAuthId === voucherToReject?.publicId}
              className="inline-flex items-center gap-2 rounded-xl bg-red-600 px-5 py-2.5 text-sm font-black text-white transition hover:bg-red-700 disabled:opacity-60"
            >
              {processingAuthId === voucherToReject?.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              Rechazar
            </button>
          </div>
        </div>
      </Modal>

      {/* MODAL ANULAR */}
      <Modal
        isOpen={isRevokeModalOpen}
        onClose={() => setIsRevokeModalOpen(false)}
        title="Anular Documento"
      >
        <div className="space-y-4">
          <div className="flex items-start gap-3 rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-rose-900 dark:border-rose-900/40 dark:bg-rose-900/20 dark:text-rose-200">
            <Ban className="mt-0.5 h-5 w-5 shrink-0" />
            <div>
              <div className="text-sm font-black">El documento quedara trazable como anulado.</div>
              <div className="mt-1 text-xs font-medium">
                No se podra emitir, aprobar, rechazar ni enviar. El historial conservara quien lo anulo y por que.
              </div>
            </div>
          </div>
          <div>
            <label className="mb-1.5 block text-[11px] font-black uppercase tracking-widest text-slate-400">
              Motivo de Anulacion {isAdmin() ? <span className="text-slate-400 font-normal normal-case tracking-normal">(opcional para administradores)</span> : null}
            </label>
            <textarea
              value={revokeReason}
              onChange={(event) => setRevokeReason(event.target.value)}
              rows={3}
              className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-rose-500 focus:ring-2 focus:ring-rose-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
              placeholder={isAdmin() ? "Opcional: indica el motivo de la anulación..." : "Ej. Se genero con datos incorrectos, se subio el archivo equivocado..."}
            />
          </div>
          <div className="flex justify-end gap-3 pt-3">
            <button
              type="button"
              onClick={() => setIsRevokeModalOpen(false)}
              className="rounded-xl px-4 py-2.5 text-sm font-bold text-slate-600 transition hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
            >
              Cancelar
            </button>
            <button
              type="button"
              onClick={handleRevokeSubmit}
              disabled={revokingId === voucherToRevoke?.publicId}
              className="inline-flex items-center gap-2 rounded-xl bg-rose-600 px-5 py-2.5 text-sm font-black text-white transition hover:bg-rose-700 disabled:opacity-60"
            >
              {revokingId === voucherToRevoke?.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : null}
              Anular Documento
            </button>
          </div>
        </div>
      </Modal>

      {previewDocument ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center p-3 sm:p-5">
          <div className="absolute inset-0 bg-slate-950/60 backdrop-blur-sm" />
          <div className="relative flex h-[92vh] w-full max-w-6xl flex-col overflow-hidden rounded-2xl bg-white shadow-2xl ring-1 ring-slate-900/10 dark:bg-slate-900 dark:ring-slate-50/10">
            <div className="flex items-center justify-between gap-3 border-b border-slate-200 px-4 py-3 dark:border-slate-800 sm:px-5">
              <div className="min-w-0">
                <h2 className="truncate text-base font-black text-slate-900 dark:text-white">
                  {previewDocument.voucher.fileName}
                </h2>
                <p className="text-xs font-semibold text-slate-500 dark:text-slate-400">
                  {formatStatus(previewDocument.voucher.status)} · {formatScope(previewDocument.voucher.scope)}
                </p>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                <button
                  type="button"
                  onClick={() => handleDownload(previewDocument.voucher)}
                  disabled={downloadingId === previewDocument.voucher.publicId}
                  className="inline-flex items-center justify-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm font-bold text-slate-700 transition hover:bg-slate-50 disabled:opacity-60 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                >
                  {downloadingId === previewDocument.voucher.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
                  Descargar
                </button>
                <button
                  type="button"
                  onClick={closePreview}
                  className="rounded-full p-2 text-slate-400 hover:bg-slate-100 hover:text-slate-600 dark:hover:bg-slate-800 dark:hover:text-slate-300"
                >
                  <X className="h-5 w-5" />
                </button>
              </div>
            </div>

            <div className="min-h-0 flex-1 bg-slate-100 dark:bg-slate-950">
              {previewDocument.kind === "pdf" ? (
                <iframe
                  src={previewDocument.url}
                  title={previewDocument.voucher.fileName}
                  className="h-full w-full border-0 bg-white"
                />
              ) : null}

              {previewDocument.kind === "image" ? (
                <div className="flex h-full items-center justify-center p-4">
                  <img
                    src={previewDocument.url}
                    alt={previewDocument.voucher.fileName}
                    className="max-h-full max-w-full rounded-lg bg-white object-contain shadow-xl"
                  />
                </div>
              ) : null}

              {previewDocument.kind === "unsupported" ? (
                <div className="flex h-full items-center justify-center p-6 text-center">
                  <div className="max-w-md rounded-2xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900">
                    <FileText className="mx-auto mb-3 h-10 w-10 text-slate-400" />
                    <h3 className="text-base font-black text-slate-900 dark:text-white">Vista previa no disponible</h3>
                    <p className="mt-2 text-sm font-medium text-slate-500 dark:text-slate-400">
                      Este formato no se puede previsualizar en el navegador. Puedes descargarlo para revisarlo.
                    </p>
                  </div>
                </div>
              ) : null}
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}

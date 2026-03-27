import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { QRCodeCanvas } from "qrcode.react";
import Swal from "sweetalert2";
import {
  Check,
  ChevronRight,
  ExternalLink,
  Loader2,
  LogOut,
  MapPin,
  MessageSquare,
  Phone,
  RefreshCcw,
  Search,
  Smartphone,
  Users2,
  CalendarRange,
} from "lucide-react";
import { api } from "../api";
import { showError, showSuccess } from "../alerts";
import { Button } from "./ui/button";

function formatDateTime(value) {
  if (!value) return "";
  return new Date(value).toLocaleString("es-AR", {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function timeAgo(value) {
  if (!value) return "";
  const diff = Date.now() - new Date(value).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return "ahora";
  if (mins < 60) return `hace ${mins}m`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `hace ${hours}h`;
  const days = Math.floor(hours / 24);
  return `hace ${days}d`;
}

function ConversationListItem({ item, selected, onSelect }) {
  return (
    <button
      type="button"
      onClick={() => onSelect(item)}
      className={`w-full text-left rounded-2xl border px-4 py-4 transition-all ${
        selected
          ? "border-indigo-500 bg-indigo-50 dark:bg-indigo-900/20 dark:border-indigo-700"
          : "border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-900 hover:border-slate-300 dark:hover:border-slate-700"
      }`}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0 space-y-1">
          <div className="flex items-center gap-2">
            <span className="text-sm font-semibold text-slate-900 dark:text-white truncate">{item.title}</span>
            <span
              className={`inline-flex rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider ${
                item.conversationType === "lead"
                  ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-300"
                  : "bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-300"
              }`}
            >
              {item.conversationType === "lead" ? "Posible cliente" : "Operativa"}
            </span>
            {item.needsAttention && (
              <span className="inline-flex h-2.5 w-2.5 rounded-full bg-amber-500" title="Requiere atencion" />
            )}
          </div>
          <div className="text-xs text-slate-500 dark:text-slate-400 truncate">{item.subtitle || item.phone}</div>
          <div className="text-sm text-slate-600 dark:text-slate-300 truncate">{item.lastMessagePreview || "Sin mensajes visibles"}</div>
        </div>

        <div className="flex flex-col items-end gap-2 flex-shrink-0">
          <div className="text-[11px] font-semibold text-slate-400">{timeAgo(item.lastMessageAt)}</div>
          <ChevronRight className="h-4 w-4 text-slate-300" />
        </div>
      </div>
    </button>
  );
}

function MessageBubble({ message }) {
  const isClient = message.sender === "client";
  const isAgent = message.sender === "agent";
  const alignClass = isClient || isAgent ? "justify-end" : "justify-start";
  const bubbleClass = isAgent
    ? "bg-indigo-600 text-white rounded-br-none"
    : isClient
      ? "bg-emerald-600 text-white rounded-br-none"
      : "bg-white dark:bg-slate-800 text-slate-700 dark:text-slate-200 border border-slate-200 dark:border-slate-700 rounded-bl-none";

  return (
    <div className={`flex ${alignClass}`}>
      <div className="max-w-[85%] space-y-1">
        <div className={`px-4 py-3 rounded-[1.5rem] text-sm leading-relaxed shadow-sm ${bubbleClass}`}>
          {message.text}
        </div>
        <div className={`text-[10px] font-semibold text-slate-400 ${isClient || isAgent ? "text-right mr-1" : "ml-1"}`}>
          {message.senderLabel} · {formatDateTime(message.createdAt)}
        </div>
      </div>
    </div>
  );
}

export default function WhatsAppBotTab() {
  const navigate = useNavigate();
  const selectedConversationKeyRef = useRef(null);
  const [botStatus, setBotStatus] = useState("STARTING");
  const [qrCode, setQrCode] = useState(null);
  const [loadingStatus, setLoadingStatus] = useState(true);
  const [conversations, setConversations] = useState([]);
  const [loadingConversations, setLoadingConversations] = useState(true);
  const [selectedConversation, setSelectedConversation] = useState(null);
  const [conversationDetail, setConversationDetail] = useState(null);
  const [loadingDetail, setLoadingDetail] = useState(false);
  const [searchTerm, setSearchTerm] = useState("");

  const getConversationKey = useCallback(
    (item) => (item ? `${item.conversationType}-${item.entityPublicId}` : null),
    []
  );

  const loadBotStatus = useCallback(async () => {
    try {
      const data = await api.get("/webhooks/status");
      setBotStatus(data.status);
      setQrCode(data.qr);
    } catch {
      setBotStatus("OFFLINE");
      setQrCode(null);
    } finally {
      setLoadingStatus(false);
    }
  }, []);

  const loadConversationDetail = useCallback(async (item) => {
    selectedConversationKeyRef.current = getConversationKey(item);
    setSelectedConversation(item);
    setLoadingDetail(true);
    try {
      const data = await api.get(`/whatsapp/conversations/${item.conversationType}/${item.entityPublicId}`);
      setConversationDetail(data);
    } catch (error) {
      setConversationDetail(null);
      showError(error.message || "No se pudo cargar la conversacion.");
    } finally {
      setLoadingDetail(false);
    }
  }, [getConversationKey]);

  const loadConversations = useCallback(async (preferredKey = null) => {
    setLoadingConversations(true);
    try {
      const data = await api.get("/whatsapp/conversations");
      const items = Array.isArray(data) ? data : [];
      setConversations(items);

      const selectedKey = preferredKey ?? selectedConversationKeyRef.current;
      const nextSelected =
        items.find((item) => getConversationKey(item) === selectedKey) ||
        items[0] ||
        null;

      if (nextSelected) {
        await loadConversationDetail(nextSelected);
      } else {
        selectedConversationKeyRef.current = null;
        setSelectedConversation(null);
        setConversationDetail(null);
      }
    } catch (error) {
      showError(error.message || "No se pudo cargar la bandeja del bot.");
    } finally {
      setLoadingConversations(false);
    }
  }, [getConversationKey, loadConversationDetail]);

  useEffect(() => {
    loadBotStatus();
    loadConversations();

    const interval = setInterval(loadBotStatus, 5000);
    return () => clearInterval(interval);
  }, [loadBotStatus, loadConversations]);

  const filteredConversations = useMemo(() => {
    const needle = searchTerm.trim().toLowerCase();
    if (!needle) return conversations;

    return conversations.filter((item) =>
      [item.title, item.subtitle, item.phone, item.lastMessagePreview]
        .filter(Boolean)
        .some((value) => value.toLowerCase().includes(needle))
    );
  }, [conversations, searchTerm]);

  const handleReload = async () => {
    setLoadingStatus(true);
    try {
      await api.post("/webhooks/reload");
      showSuccess("Bot sincronizado.");
      await Promise.all([loadBotStatus(), loadConversations()]);
    } catch (error) {
      showError(error.message || "No se pudo sincronizar el bot.");
    } finally {
      setLoadingStatus(false);
    }
  };

  const handleLogoutBot = async () => {
    const result = await Swal.fire({
      title: "Cerrar sesion de WhatsApp",
      text: "El bot dejara de operar hasta volver a escanear el QR.",
      icon: "warning",
      showCancelButton: true,
      confirmButtonText: "Cerrar sesion",
      cancelButtonText: "Cancelar",
    });

    if (!result.isConfirmed) return;

    try {
      await api.post("/webhooks/logout");
      showSuccess("Sesion cerrada.");
      await loadBotStatus();
    } catch (error) {
      showError(error.message || "No se pudo cerrar la sesion.");
    }
  };

  return (
    <div className="max-w-6xl mx-auto space-y-6">
      <div className="grid gap-6 lg:grid-cols-[1.1fr,0.9fr]">
        <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 space-y-4">
          <div className="flex flex-col sm:flex-row sm:items-start justify-between gap-4">
            <div>
              <h2 className="text-2xl font-bold text-slate-900 dark:text-white flex items-center gap-3">
                <Smartphone className="h-6 w-6 text-indigo-600" />
                WhatsApp Bot
              </h2>
              <p className="text-sm text-slate-500 dark:text-slate-400 mt-1">
                El nombre visible del bot toma el nombre de fantasia configurado para la agencia.
              </p>
            </div>

            <div className="flex gap-2">
              <Button variant="outline" className="rounded-xl" onClick={handleReload} disabled={loadingStatus}>
                <RefreshCcw className="h-4 w-4 mr-2" />
                Refrescar
              </Button>
              {botStatus === "READY" && (
                <Button variant="outline" className="rounded-xl text-rose-600 border-rose-100 hover:bg-rose-50" onClick={handleLogoutBot}>
                  <LogOut className="h-4 w-4 mr-2" />
                  Cerrar sesion
                </Button>
              )}
            </div>
          </div>

          {botStatus === "READY" ? (
            <div className="rounded-2xl border border-emerald-200 dark:border-emerald-900/40 bg-emerald-50 dark:bg-emerald-900/10 px-5 py-4 flex items-center gap-3">
              <div className="h-12 w-12 rounded-2xl bg-emerald-100 dark:bg-emerald-900/30 text-emerald-600 dark:text-emerald-300 flex items-center justify-center">
                <Check className="h-6 w-6" />
              </div>
              <div>
                <div className="font-semibold text-emerald-700 dark:text-emerald-300">Bot conectado</div>
                <div className="text-sm text-emerald-700/80 dark:text-emerald-300/80">
                  Operando con respuestas naturales y seguimiento comercial.
                </div>
              </div>
            </div>
          ) : (
            <div className="rounded-2xl border border-slate-200 dark:border-slate-800 bg-slate-50/50 dark:bg-slate-800/10 p-6">
              {(botStatus === "SCAN_QR" || qrCode) ? (
                <div className="text-center space-y-4">
                  <div className="inline-block rounded-[28px] bg-white p-5 shadow-lg">
                    <QRCodeCanvas value={qrCode} size={220} level="H" />
                  </div>
                  <div className="text-sm text-slate-500 dark:text-slate-400">
                    Escanea este codigo desde WhatsApp para volver a vincular el bot.
                  </div>
                </div>
              ) : (
                <div className="flex items-center gap-3 text-slate-500 dark:text-slate-400">
                  <Loader2 className="h-5 w-5 animate-spin" />
                  <span>{botStatus === "OFFLINE" ? "El bot esta fuera de linea." : "Iniciando conexion con WhatsApp..."}</span>
                </div>
              )}
            </div>
          )}
        </div>

        <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm p-6 space-y-3">
          <div className="text-sm font-semibold text-slate-900 dark:text-white">Bandeja de conversaciones</div>
          <p className="text-sm text-slate-500 dark:text-slate-400">
            Todas las conversaciones que el bot deja registradas en la gestion comercial y en la operativa.
          </p>
          <div className="grid gap-3 sm:grid-cols-3">
            <div className="rounded-2xl border border-slate-200 dark:border-slate-800 px-4 py-3">
              <div className="text-[11px] uppercase tracking-wider font-semibold text-slate-400">Conversaciones</div>
              <div className="text-2xl font-semibold text-slate-900 dark:text-white">{conversations.length}</div>
            </div>
            <div className="rounded-2xl border border-slate-200 dark:border-slate-800 px-4 py-3">
              <div className="text-[11px] uppercase tracking-wider font-semibold text-slate-400">Posibles clientes</div>
              <div className="text-2xl font-semibold text-slate-900 dark:text-white">
                {conversations.filter((item) => item.conversationType === "lead").length}
              </div>
            </div>
            <div className="rounded-2xl border border-slate-200 dark:border-slate-800 px-4 py-3">
              <div className="text-[11px] uppercase tracking-wider font-semibold text-slate-400">Operativas</div>
              <div className="text-2xl font-semibold text-slate-900 dark:text-white">
                {conversations.filter((item) => item.conversationType === "operational").length}
              </div>
            </div>
          </div>
        </div>
      </div>

      <div className="grid gap-6 xl:grid-cols-[360px,1fr]">
        <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
          <div className="p-4 border-b border-slate-100 dark:border-slate-800">
            <div className="relative">
              <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-slate-400" />
              <input
                type="text"
                value={searchTerm}
                onChange={(event) => setSearchTerm(event.target.value)}
                placeholder="Buscar por nombre, telefono o mensaje..."
                className="w-full pl-9 pr-4 py-2.5 text-sm rounded-xl border border-slate-200 dark:border-slate-800 bg-slate-50 dark:bg-slate-950 dark:text-white"
              />
            </div>
          </div>

          <div className="p-4 space-y-3 max-h-[720px] overflow-y-auto">
            {loadingConversations ? (
              <div className="py-10 text-center text-slate-400">
                <Loader2 className="h-6 w-6 animate-spin mx-auto mb-3" />
                Cargando conversaciones...
              </div>
            ) : filteredConversations.length === 0 ? (
              <div className="py-10 text-center text-slate-400">
                <MessageSquare className="h-8 w-8 mx-auto mb-3 opacity-50" />
                No hay conversaciones registradas.
              </div>
            ) : (
              filteredConversations.map((item) => (
                <ConversationListItem
                  key={`${item.conversationType}-${item.entityPublicId}`}
                  item={item}
                  selected={
                    selectedConversation &&
                    selectedConversation.conversationType === item.conversationType &&
                    selectedConversation.entityPublicId === item.entityPublicId
                  }
                  onSelect={loadConversationDetail}
                />
              ))
            )}
          </div>
        </div>

        <div className="bg-white dark:bg-slate-900 rounded-2xl border border-slate-200 dark:border-slate-800 shadow-sm overflow-hidden">
          {!conversationDetail ? (
            <div className="h-full min-h-[500px] flex items-center justify-center text-center p-8">
              <div className="space-y-3">
                <MessageSquare className="h-10 w-10 mx-auto text-slate-300" />
                <div className="text-lg font-semibold text-slate-900 dark:text-white">Selecciona una conversacion</div>
                <div className="text-sm text-slate-500 dark:text-slate-400">
                  Aqui vas a ver el detalle completo del chat registrado por el bot.
                </div>
              </div>
            </div>
          ) : (
            <>
              <div className="px-6 py-5 border-b border-slate-100 dark:border-slate-800">
                <div className="flex flex-col lg:flex-row lg:items-start justify-between gap-4">
                  <div className="space-y-2">
                    <div className="flex items-center gap-2">
                      <h3 className="text-xl font-semibold text-slate-900 dark:text-white">{conversationDetail.title}</h3>
                      <span
                        className={`inline-flex rounded-full px-2 py-0.5 text-[10px] font-bold uppercase tracking-wider ${
                          conversationDetail.conversationType === "lead"
                            ? "bg-emerald-50 text-emerald-600 dark:bg-emerald-900/20 dark:text-emerald-300"
                            : "bg-indigo-50 text-indigo-600 dark:bg-indigo-900/20 dark:text-indigo-300"
                        }`}
                      >
                        {conversationDetail.conversationType === "lead" ? "Posible cliente" : "Operativa"}
                      </span>
                    </div>
                    <div className="text-sm text-slate-500 dark:text-slate-400">{conversationDetail.subtitle || conversationDetail.phone}</div>
                    <div className="flex flex-wrap gap-2 text-xs text-slate-500 dark:text-slate-400">
                      {conversationDetail.phone && (
                        <span className="inline-flex items-center gap-1 rounded-full bg-slate-100 dark:bg-slate-800 px-3 py-1">
                          <Phone className="h-3.5 w-3.5" />
                          {conversationDetail.phone}
                        </span>
                      )}
                      {conversationDetail.interestedIn && (
                        <span className="inline-flex items-center gap-1 rounded-full bg-slate-100 dark:bg-slate-800 px-3 py-1">
                          <MapPin className="h-3.5 w-3.5" />
                          {conversationDetail.interestedIn}
                        </span>
                      )}
                      {conversationDetail.travelDates && (
                        <span className="inline-flex items-center gap-1 rounded-full bg-slate-100 dark:bg-slate-800 px-3 py-1">
                          <CalendarRange className="h-3.5 w-3.5" />
                          {conversationDetail.travelDates}
                        </span>
                      )}
                      {conversationDetail.travelers && (
                        <span className="inline-flex items-center gap-1 rounded-full bg-slate-100 dark:bg-slate-800 px-3 py-1">
                          <Users2 className="h-3.5 w-3.5" />
                          {conversationDetail.travelers}
                        </span>
                      )}
                    </div>
                  </div>

                  <div className="flex gap-2">
                    {conversationDetail.leadPublicId && (
                      <Button
                        variant="outline"
                        className="rounded-xl"
                        onClick={() => navigate("/crm", { state: { openLeadId: conversationDetail.leadPublicId } })}
                      >
                        <ExternalLink className="h-4 w-4 mr-2" />
                        Abrir lead
                      </Button>
                    )}
                    {conversationDetail.reservaPublicId && (
                      <Button
                        variant="outline"
                        className="rounded-xl"
                        onClick={() => navigate(`/reservas/${conversationDetail.reservaPublicId}`)}
                      >
                        <ExternalLink className="h-4 w-4 mr-2" />
                        Abrir reserva
                      </Button>
                    )}
                  </div>
                </div>
              </div>

              <div className="p-6 min-h-[500px] max-h-[720px] overflow-y-auto space-y-4 bg-slate-50/40 dark:bg-slate-950/30">
                {loadingDetail ? (
                  <div className="py-10 text-center text-slate-400">
                    <Loader2 className="h-6 w-6 animate-spin mx-auto mb-3" />
                    Cargando detalle...
                  </div>
                ) : conversationDetail.messages?.length > 0 ? (
                  conversationDetail.messages.map((message) => (
                    <MessageBubble key={message.id} message={message} />
                  ))
                ) : (
                  <div className="py-10 text-center text-slate-400">
                    No hay mensajes disponibles para esta conversacion.
                  </div>
                )}
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

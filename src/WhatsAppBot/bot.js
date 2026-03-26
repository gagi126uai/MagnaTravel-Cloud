/**
 * MagnaTravel WhatsApp Bot
 * Lead capture + operational document delivery.
 */

require("dotenv").config();
const { Client, LocalAuth, MessageMedia } = require("whatsapp-web.js");
const axios = require("axios");
const express = require("express");
const fs = require("fs");
const path = require("path");

const WEBHOOK_URL = process.env.WEBHOOK_URL || "http://localhost:5000/api/webhooks/whatsapp";
const API_URL = process.env.API_URL || "http://api:8080";
const WEBHOOK_SECRET = process.env.WEBHOOK_SECRET || "CHANGE_THIS_SECRET";
const SESSION_TIMEOUT = (parseInt(process.env.BOT_SESSION_TIMEOUT_MINUTES, 10) || 30) * 60 * 1000;
const HTTP_PORT = parseInt(process.env.BOT_HTTP_PORT, 10) || 3001;
const AUTH_PATH = path.join(__dirname, ".wwebjs_auth");
const CLIENT_RESTART_DELAY_MS = (parseInt(process.env.BOT_RESTART_DELAY_SECONDS, 10) || 10) * 1000;

let isShowingQR = false;
let isBotReady = false;
let lastQR = null;
let botStatus = "STARTING";
const botLogs = [];
const MAX_LOGS = 100;

const processedMessages = new Set();
const MESSAGE_COOLDOWN_MS = 3000;
const lastMessageTime = new Map();
const sessions = new Map();
let restartTimer = null;
let isStartingClient = false;

let agencyName = "MagnaTravel";
let MSG = {
    welcome: () => `¡Hola! Soy el asistente virtual de *${agencyName}*.\n\nPara poder brindarte una mejor atención, ¿me podrías indicar tu nombre?`,
    badName: "No logré registrar tu nombre correctamente. ¿Podrías confirmármelo, por favor?",
    askNameAfterInterest: (interest) => `¡Excelente elección! Ya dejé asentado que te interesa *${interest}*.\n\nAntes de continuar, ¿me podrías indicar tu nombre?`,
    askInterest: (name) => `¡Un gusto saludarte, *${shortName(name)}*!\n\n¿Qué destino o tipo de viaje tenés en mente?`,
    askDates: (interest) => `¡Perfecto!\n\n¿Tenés alguna fecha o época estimada para viajar a *${interest}*?`,
    askTravelers: () => "Por último, ¿cuántas personas viajarían?",
    thanks: (name) => `¡Muchas gracias por los datos, *${shortName(name)}*!\n\nYa derivé tu solicitud a uno de nuestros asesores en *${agencyName}*. En breve un especialista continuará la atención por acá mismo.`,
    agentRequest: (name) => `Entendido${name ? `, *${shortName(name)}*` : ""}.\n\nEn un momento te contactaré con uno de nuestros asesores expertos de *${agencyName}* para continuar por este medio.`,
    duplicate: `Ya tenemos registrada tu consulta en nuestro sistema.\n\nEn breve uno de nuestros asesores de *${agencyName}* se pondrá en contacto con vos por esta misma vía.`,
    error: `Tuvimos un inconveniente al procesar tu solicitud.\n\nPor favor, volvé a escribirnos en unos minutos o comunicate con las oficinas de *${agencyName}*.`
};

const RECOVERABLE_BROWSER_ERRORS = /(Execution context was destroyed|Target closed|Session closed|Protocol error|frame was detached)/i;

function botLog(msg) {
    const timestamp = new Date().toISOString().split("T")[1].split(".")[0];
    const entry = `[${timestamp}] ${msg}`;
    console.log(entry);
    botLogs.push(entry);
    if (botLogs.length > MAX_LOGS) botLogs.shift();
}

async function fetchConfig() {
    try {
        botLog("Sincronizando configuracion con API...");
        const res = await axios.get(`${API_URL}/api/whatsapp/config/env`, {
            headers: { "X-Webhook-Secret": WEBHOOK_SECRET }
        });
        const { agencyName: loadedAgencyName } = res.data;

        if (loadedAgencyName) {
            agencyName = loadedAgencyName;
        }

        botLog("Configuracion actualizada.");
    } catch (err) {
        botLog(`No se pudo cargar configuracion: ${err.message}`);
    }
}

function cleanLockFiles() {
    try {
        if (!fs.existsSync(AUTH_PATH)) return;

        const deletePattern = (dirPath) => {
            if (!fs.existsSync(dirPath)) return;
            const entries = fs.readdirSync(dirPath);
            for (const entry of entries) {
                const fullPath = path.join(dirPath, entry);
                try {
                    const stat = fs.lstatSync(fullPath);
                    if (stat.isDirectory()) {
                        deletePattern(fullPath);
                    } else if (entry.includes("Singleton") || entry.includes("Lock")) {
                        fs.unlinkSync(fullPath);
                    }
                } catch {
                    // Ignore races.
                }
            }
        };

        deletePattern(AUTH_PATH);
    } catch (err) {
        botLog(`Error limpiando locks: ${err.message}`);
    }
}

function getSession(chatId) {
    if (!sessions.has(chatId)) return null;
    const session = sessions.get(chatId);
    clearTimeout(session._timer);
    session._timer = setTimeout(() => {
        sessions.delete(chatId);
        lastMessageTime.delete(chatId);
    }, SESSION_TIMEOUT);
    return session;
}

function createSession(chatId) {
    if (sessions.has(chatId)) clearTimeout(sessions.get(chatId)._timer);
    const session = {
        state: "GREETING",
        name: null,
        interest: null,
        dates: null,
        travelers: null,
        transcript: [],
        _timer: setTimeout(() => {
            sessions.delete(chatId);
            lastMessageTime.delete(chatId);
        }, SESSION_TIMEOUT)
    };
    sessions.set(chatId, session);
    return session;
}

const GREETINGS = /^(hola|buenas|buen[oa]s?\s*(tardes?|noches?|d[ií]as?)?|hey|hi|hello|ey|que\s*tal|holis|consulta|quiero\s*(viajar|info)|me\s*interesa)[\s!?.,]*$/i;
const AGENT_REQUEST = /\b(asesor|persona\s*real|humano|agente|hablar\s*con\s*alguien|operador)\b/i;
const TRAVEL_CONTEXT = /\b(brasil|bariloche|europa|cancun|disney|miami|caribe|crucero|mendoza|playa|viaje|vacaciones|pasajes?|paquete|hotel|octubre|noviembre|diciembre|enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|setiembre|familia|pareja|adultos?|pasajeros?|viajeros?|personas?)\b/i;

function isGreeting(text) {
    return GREETINGS.test(text.trim());
}

function shortName(name) {
    return (name || "").trim().split(/\s+/)[0] || name;
}

function looksLikeName(text) {
    const trimmed = text.trim();
    if (trimmed.length < 2 || trimmed.length > 200) return false;
    if (/\d/.test(trimmed)) return false;
    if (isGreeting(trimmed)) return false;
    if (TRAVEL_CONTEXT.test(trimmed)) return false;
    if (!/[a-záéíóúñü]/i.test(trimmed)) return false;
    return true;
}

function looksLikeInterest(text) {
    const trimmed = text.trim();
    if (trimmed.length < 2) return false;
    if (isGreeting(trimmed)) return false;
    return /[a-z0-9]/i.test(trimmed);
}

function looksLikeDates(text) {
    const trimmed = text.trim();
    if (trimmed.length < 2) return false;
    if (isGreeting(trimmed)) return false;
    return /(\d|enero|febrero|marzo|abril|mayo|junio|julio|agosto|septiembre|setiembre|octubre|noviembre|diciembre|verano|invierno|primavera|otono|otoÃ±o|semana|mes|definir|flexible|proximo|pr[oÃ³]ximo)/i.test(trimmed) || trimmed.length >= 4;
}

function looksLikeTravelers(text) {
    const trimmed = text.trim();
    if (trimmed.length < 1) return false;
    if (isGreeting(trimmed)) return false;
    return /(\d+|uno|una|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez|adult|menor|nene|bebe|beb[eÃ©]|familia|pareja|amigos|persona|personas|pasajero|pasajeros|viajero|viajeros|solo|sola)/i.test(trimmed);
}

function buildTravelerRetryMessage() {
    return "Para completar tu consulta, necesitaría saber la cantidad de viajeros. Por ejemplo: *2 adultos*, *familia de 4* o *3 pasajeros*.";
}

function extractPhone(chatId) {
    const match = chatId.match(/^(\d+)@/);
    return match ? `+${match[1]}` : chatId;
}

function phoneToChatId(phone) {
    return phone.replace(/^\+/, "").replace(/[^0-9]/g, "") + "@c.us";
}

async function sendChatText(chatId, text) {
    return client.sendMessage(chatId, text);
}

async function sendToWebhook(phone, session) {
    const payload = {
        name: session.name,
        phone,
        interest: session.interest,
        dates: session.dates,
        travelers: session.travelers,
        transcript: session.transcript.join("\n")
    };

    try {
        const res = await axios.post(WEBHOOK_URL, payload, {
            headers: { "Content-Type": "application/json", "X-Webhook-Secret": WEBHOOK_SECRET },
            timeout: 10000
        });
        botLog(`Lead ${res.data?.leadId || "?"} sincronizado para ${phone}`);
        return "ok";
    } catch (error) {
        if (error.response?.status === 409) return "duplicate";
        botLog(`Webhook error: ${error.response?.status || error.message}`);
        return "error";
    }
}

async function sendMessageToWebhook(phone, message, sender, options = {}) {
    try {
        const res = await axios.post(`${WEBHOOK_URL}/message`, {
            phone,
            message,
            sender,
            skipLeadAutoCreation: options.skipLeadAutoCreation === true
        }, {
            headers: { "Content-Type": "application/json", "X-Webhook-Secret": WEBHOOK_SECRET },
            timeout: 5000
        });
        return res.data || null;
    } catch (err) {
        botLog(`Webhook mensaje error: ${err.response?.status || err.message}`);
        return null;
    }
}

function buildThanksMessage(session) {
    return MSG.thanks(session.name);
}

function isRecoverableBrowserError(error) {
    const message = error?.message || String(error || "");
    return RECOVERABLE_BROWSER_ERRORS.test(message);
}

async function restartClient(reason) {
    if (restartTimer || isStartingClient) return;

    botStatus = "RECOVERING";
    isBotReady = false;
    lastQR = null;
    isShowingQR = false;
    botLog(`Reiniciando cliente WhatsApp: ${reason}`);

    restartTimer = setTimeout(async () => {
        restartTimer = null;
        try {
            await client.destroy();
        } catch {
            // Ignore destroy failures; initialize below is the source of truth.
        }

        try {
            await startBot();
        } catch (err) {
            botLog(`Reinicio fallido: ${err.message}`);
        }
    }, CLIENT_RESTART_DELAY_MS);
}

botLog("Iniciando motor del bot...");
cleanLockFiles();

const client = new Client({
    authStrategy: new LocalAuth(),
    authTimeoutMs: 60000,
    puppeteer: {
        headless: "new",
        args: [
            "--no-sandbox",
            "--disable-setuid-sandbox",
            "--disable-dev-shm-usage",
            "--disable-accelerated-2d-canvas",
            "--no-first-run",
            "--no-zygote",
            "--disable-gpu"
        ],
        executablePath: process.env.PUPPETEER_EXECUTABLE_PATH || "/usr/bin/chromium"
    }
});

async function startBot(retries = 3) {
    if (isStartingClient) return;
    isStartingClient = true;

    for (let attempt = 0; attempt < retries; attempt += 1) {
        try {
            cleanLockFiles();
            botStatus = "STARTING";
            botLog(`Iniciando cliente WhatsApp (${attempt + 1}/${retries})...`);
            await client.initialize();
            isStartingClient = false;
            return;
        } catch (err) {
            botLog(`Error lanzando Chromium: ${err.message}`);
            if (attempt < retries - 1) {
                await new Promise((resolve) => setTimeout(resolve, 10000));
            }
        }
    }

    isStartingClient = false;
    throw new Error("No se pudo inicializar el cliente de WhatsApp.");
}

startBot().catch((err) => {
    botLog(`Fallo inicializando bot: ${err.message}`);
});

client.on("qr", (qr) => {
    lastQR = qr;
    botStatus = "SCAN_QR";
    if (isShowingQR) return;
    isShowingQR = true;
    botLog("Nuevo QR generado.");
});

client.on("ready", () => {
    botStatus = "READY";
    lastQR = null;
    isShowingQR = false;
    isBotReady = true;
    botLog("Bot conectado.");
    fetchConfig();
});

client.on("authenticated", () => {
    botStatus = "AUTHENTICATED";
    botLog("Sesion autenticada.");
});

client.on("auth_failure", (message) => {
    botStatus = "AUTH_FAILURE";
    isBotReady = false;
    botLog(`Fallo de autenticacion: ${message}`);
});

client.on("change_state", (state) => {
    botLog(`Estado de WhatsApp Web: ${state}`);
});

client.on("disconnected", (reason) => {
    botStatus = "DISCONNECTED";
    isBotReady = false;
    botLog(`Bot desconectado: ${reason}`);
    restartClient(`desconexion (${reason})`);
});

client.on("message", async (message) => {
    if (message.from.includes("@g.us") || message.fromMe || message.from === "status@broadcast" || message.from.includes("broadcast")) {
        return;
    }

    const chatId = message.from;
    const body = message.body?.trim();
    if (!body) return;

    const msgKey = `${chatId}_${body}`;
    if (processedMessages.has(msgKey)) return;
    processedMessages.add(msgKey);
    setTimeout(() => processedMessages.delete(msgKey), 10000);

    const now = Date.now();
    if (now - (lastMessageTime.get(chatId) || 0) < MESSAGE_COOLDOWN_MS) return;
    lastMessageTime.set(chatId, now);

    const phone = extractPhone(chatId);
    botLog(`[${phone}] ${body.substring(0, 50)}`);
    const existingSession = getSession(chatId);
    const webhookResult = await sendMessageToWebhook(phone, body, "Cliente", { skipLeadAutoCreation: true });

    if (webhookResult?.handledBy === "operational") {
        botLog(`Mensaje operativo vinculado para ${phone}.`);
        return;
    }

    if (/nueva\s*consulta/i.test(body)) {
        const session = createSession(chatId);
        const welcome = typeof MSG.welcome === "function" ? MSG.welcome() : MSG.welcome;
        await sendChatText(chatId, welcome);
        session.transcript.push(`[Cliente]: ${body}`, `[Bot]: ${welcome}`);
        session.state = "WAITING_NAME";
        return;
    }

    if (AGENT_REQUEST.test(body)) {
        const session = existingSession || createSession(chatId);
        const agentMessage = typeof MSG.agentRequest === "function" ? MSG.agentRequest(session.name) : MSG.agentRequest;
        await sendChatText(chatId, agentMessage);
        session.transcript.push(`[Cliente]: ${body}`, `[Bot]: ${agentMessage}`);
        session.state = "DONE";
        if (session.name) await sendToWebhook(phone, session);
        return;
    }

    let session = existingSession;

    try {
        if (!session && webhookResult?.handledBy === "lead" && webhookResult?.allowBotCapture !== true) {
            botLog(`Mensaje asociado a lead existente #${webhookResult.leadId}. Sin reiniciar flujo.`);
            return;
        }

        if (!session) {
            session = createSession(chatId);
            session.transcript.push(`[Cliente]: ${body}`);

            if (!isGreeting(body) && looksLikeInterest(body) && !looksLikeName(body)) {
                session.interest = body;
                session.state = "WAITING_NAME";
                const nextMessage = MSG.askNameAfterInterest(session.interest);
                await sendChatText(chatId, nextMessage);
                session.transcript.push(`[Bot]: ${nextMessage}`);
                return;
            }

            if (looksLikeName(body)) {
                session.name = body;
                session.state = "WAITING_INTEREST";
                const nextMessage = MSG.askInterest(session.name);
                await sendChatText(chatId, nextMessage);
                session.transcript.push(`[Bot]: ${nextMessage}`);
                return;
            }

            const welcome = typeof MSG.welcome === "function" ? MSG.welcome() : MSG.welcome;
            await sendChatText(chatId, welcome);
            session.transcript.push(`[Bot]: ${welcome}`);
            session.state = "WAITING_NAME";
            return;
        }

        session.transcript.push(`[Cliente]: ${body}`);

        switch (session.state) {
            case "WAITING_NAME": {
                if (!looksLikeName(body)) {
                    await sendChatText(chatId, MSG.badName);
                    session.transcript.push(`[Bot]: ${MSG.badName}`);
                    return;
                }

                session.name = body;
                const nextMessage = session.interest
                    ? MSG.askDates(session.interest)
                    : MSG.askInterest(session.name);
                session.state = session.interest ? "WAITING_DATES" : "WAITING_INTEREST";
                await sendChatText(chatId, nextMessage);
                session.transcript.push(`[Bot]: ${nextMessage}`);
                break;
            }

            case "WAITING_INTEREST": {
                if (!looksLikeInterest(body)) {
                    const retryMessage = "Para orientarte mejor, ¿me contarías qué tipo de destino buscás? Por ejemplo: *Brasil*, *Bariloche* o *Playa en octubre*.";
                    await sendChatText(chatId, retryMessage);
                    session.transcript.push(`[Bot]: ${retryMessage}`);
                    return;
                }

                session.interest = body;
                session.state = "WAITING_DATES";
                const nextMessage = MSG.askDates(session.interest);
                await sendChatText(chatId, nextMessage);
                session.transcript.push(`[Bot]: ${nextMessage}`);
                break;
            }

            case "WAITING_DATES": {
                if (!looksLikeDates(body)) {
                    const retryMessage = "Por favor, indicame una fecha aproximada. Por ejemplo: *octubre 2026*, *vacaciones de invierno* o si tenés fechas *a definir*.";
                    await sendChatText(chatId, retryMessage);
                    session.transcript.push(`[Bot]: ${retryMessage}`);
                    return;
                }

                session.dates = body;
                session.state = "WAITING_TRAVELERS";
                const nextMessage = MSG.askTravelers();
                await sendChatText(chatId, nextMessage);
                session.transcript.push(`[Bot]: ${nextMessage}`);
                break;
            }

            case "WAITING_TRAVELERS": {
                if (!looksLikeTravelers(body)) {
                    const retryMessage = buildTravelerRetryMessage();
                    await sendChatText(chatId, retryMessage);
                    session.transcript.push(`[Bot]: ${retryMessage}`);
                    return;
                }

                session.travelers = body;
                session.state = "SENDING";
                const result = await sendToWebhook(phone, session);

                if (result === "duplicate") {
                    await sendChatText(chatId, MSG.duplicate);
                    session.transcript.push(`[Bot]: ${MSG.duplicate}`);
                } else if (result === "ok") {
                    const thanksMessage = buildThanksMessage(session);
                    await sendChatText(chatId, thanksMessage);
                    session.transcript.push(`[Bot]: ${thanksMessage}`);
                } else {
                    await sendChatText(chatId, MSG.error);
                    session.transcript.push(`[Bot]: ${MSG.error}`);
                }

                session.state = "DONE";
                break;
            }

            default:
                break;
        }
    } catch (err) {
        botLog(`Flow error: ${err.message}`);
    }
});

process.on("unhandledRejection", (reason) => {
    if (isRecoverableBrowserError(reason)) {
        botLog(`Error recuperable detectado: ${reason.message || reason}`);
        restartClient(reason.message || String(reason));
        return;
    }

    botLog(`Unhandled rejection: ${reason?.message || reason}`);
});

process.on("uncaughtException", (error) => {
    if (isRecoverableBrowserError(error)) {
        botLog(`Excepcion recuperable detectada: ${error.message}`);
        restartClient(error.message);
        return;
    }

    botLog(`Uncaught exception: ${error.message}`);
    process.exit(1);
});

const app = express();
app.use(express.json({ limit: "25mb" }));

app.post("/send", async (req, res) => {
    const { phone, message } = req.body || {};
    if (req.headers["x-webhook-secret"] !== WEBHOOK_SECRET) return res.status(401).send();
    if (!phone || !message) return res.status(400).json({ error: "phone y message son obligatorios" });

    try {
        const chatId = phoneToChatId(phone);
        const sent = await client.sendMessage(chatId, message);
        botLog(`Texto enviado a ${phone}`);
        res.json({ success: true, messageId: sent?.id?._serialized || null });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

app.post("/send-document", async (req, res) => {
    const { phone, caption, fileName, mimeType, base64 } = req.body || {};
    if (req.headers["x-webhook-secret"] !== WEBHOOK_SECRET) return res.status(401).send();
    if (!phone || !fileName || !mimeType || !base64) {
        return res.status(400).json({ error: "phone, fileName, mimeType y base64 son obligatorios" });
    }

    try {
        const chatId = phoneToChatId(phone);
        const media = new MessageMedia(mimeType, base64, fileName);
        const sent = await client.sendMessage(chatId, media, {
            caption: caption || "",
            sendMediaAsDocument: true
        });
        botLog(`Documento enviado a ${phone}: ${fileName}`);
        res.json({ success: true, messageId: sent?.id?._serialized || null });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

app.post("/reload", async (req, res) => {
    if (req.headers["x-webhook-secret"] !== WEBHOOK_SECRET) return res.status(401).send();
    await fetchConfig();
    res.json({ success: true, status: botStatus });
});

app.get("/status", (req, res) => {
    if (req.headers["x-webhook-secret"] !== WEBHOOK_SECRET) return res.status(401).send();
    res.json({ status: botStatus, qr: lastQR, ready: isBotReady });
});

app.get("/logs", (req, res) => {
    if (req.headers["x-webhook-secret"] !== WEBHOOK_SECRET) return res.status(401).send();
    res.json(botLogs);
});

app.post("/logout", async (req, res) => {
    if (req.headers["x-webhook-secret"] !== WEBHOOK_SECRET) return res.status(401).send();
    try {
        await client.logout();
        botStatus = "DISCONNECTED";
        isBotReady = false;
        lastQR = null;
        res.json({ success: true });
        setTimeout(() => process.exit(0), 1000);
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

app.listen(HTTP_PORT, "0.0.0.0", () => {
    botLog(`Bot escuchando comandos en puerto ${HTTP_PORT}`);
    fetchConfig();
});

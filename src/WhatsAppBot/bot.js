/**
 * MagnaTravel WhatsApp Bot v3.2
 * =============================
 * Bot personalizado con configuración dinámica desde API
 * y corrección automática de bloqueo de Chromium.
 */

require("dotenv").config();
const { Client, LocalAuth } = require("whatsapp-web.js");
const qrcode = require("qrcode-terminal");
const axios = require("axios");
const express = require("express");
const fs = require("fs");
const path = require("path");

// ─── Config ──────────────────────────────────────────────
const WEBHOOK_URL = process.env.WEBHOOK_URL || "http://localhost:5000/api/webhooks/whatsapp";
const API_URL = process.env.API_URL || "http://api:8080"; // URL interna en Docker
const WEBHOOK_SECRET = process.env.WEBHOOK_SECRET || "CHANGE_THIS_SECRET";
const SESSION_TIMEOUT = (parseInt(process.env.BOT_SESSION_TIMEOUT_MINUTES) || 30) * 60 * 1000;
const HTTP_PORT = parseInt(process.env.BOT_HTTP_PORT) || 3001;
const AUTH_PATH = path.join(__dirname, ".wwebjs_auth");

// ─── Control de Logs y Estado ─────────────────────────
let isShowingQR = false;
let isBotReady = false;
let lastQR = null;
let botStatus = "STARTING"; // STARTING, SCAN_QR, AUTHENTICATED, READY, DISCONNECTED
const botLogs = [];
const MAX_LOGS = 100;

function botLog(msg) {
    const timestamp = new Date().toISOString().split('T')[1].split('.')[0];
    const logEntry = `[${timestamp}] ${msg}`;
    console.log(logEntry);
    botLogs.push(logEntry);
    if (botLogs.length > MAX_LOGS) botLogs.shift();
}

// ─── Anti-spam ───────────────────────────────────────────
const processedMessages = new Set();
const MESSAGE_COOLDOWN_MS = 3000;
const lastMessageTime = new Map();

// ─── Bot Messages (Defaults) ─────────────────────────────
let agencyName = "MagnaTravel";

let MSG = {
    welcome: () => `¡Hola! 👋 Bienvenido/a a *${agencyName}* 🌎✈️\n\nSoy tu asistente virtual y estoy acá para ayudarte a planificar tu próximo viaje soñado. 🏖️🗺️\n\nPara empezar, *¿me decís tu nombre completo?*`,
    badName: `No pude captar tu nombre 😅 ¿Podés decirme tu *nombre y apellido*?`,
    askInterest: (name) => `¡Un placer, *${name}*! 🤩\n\nContame, *¿qué destino o tipo de viaje te gustaría hacer?*\n\n✈️ _Ej: Cancún, Europa, Crucero, Brasil, Bariloche..._`,
    askDates: (interest) => `¡*${interest}*! Excelente elección 😍\n\n¿Tenés alguna *fecha aproximada* en mente para viajar? 📅\n\n_Ej: "marzo 2026", "semana santa", "todavía no sé"_`,
    askTravelers: () => `Perfecto 📝\n\nÚltima pregunta: *¿cuántas personas viajan?* 👥\n\n_Ej: "somos 2", "familia de 4", "soy solo/a", "grupo de amigos"_`,
    thanks: (name) => `¡Genial, *${name}*! Ya tengo toda la info 🎉\n\n📋 *Tu consulta fue registrada* y un asesor se va a comunicar con vos a la brevedad.\n\n¡Gracias por confiar en *${agencyName}*! ✨🛫`,
    agentRequest: (name) => `Entendido, *${name || ""}*! 🤝 Ya le avisé a un asesor para que te contacte personalmente.📞`,
    duplicate: `¡Hola de nuevo! 😊 Tu consulta ya fue registrada y estamos trabajando en tu propuesta.\nSi es algo urgente, podés llamarnos directamente. 📞`,
    error: `Disculpá, hubo un problema al registrar la consulta 😔\nPor favor intentá de nuevo o llamanos por teléfono.`,
};

// ─── Dynamic Config ──────────────────────────────────────


async function fetchConfig() {
    try {
        botLog("🔄 Sincronizando configuración con API...");
        const res = await axios.get(`${API_URL}/api/whatsapp/config/env`);
        const { config, agencyName: name } = res.data;
        
        if (name) {
            agencyName = name;
            botLog(`🏢 Agencia cargada: ${agencyName}`);
        }

        if (config) {
            // Funciones con reemplazos dinámicos
            MSG.welcome = () => (config.welcomeMessage || "¡Hola! 👋 Bienvenido/a a *{agencyName}* 🌎✈️...").replace(/{agencyName}/g, agencyName);
            MSG.askInterest = (name) => (config.askInterestMessage || "¡Un placer, *{name}*! 🤩...").replace(/{name}/g, name);
            MSG.askDates = (interest) => (config.askDatesMessage || "¡*{interest}*! Excelente elección 😍...").replace(/{interest}/g, interest);
            MSG.askTravelers = () => config.askTravelersMessage || "Perfecto 📝...";
            MSG.thanks = (name) => (config.thanksMessage || "¡Genial, *{name}*!...").replace(/{name}/g, name).replace(/{agencyName}/g, agencyName);
            MSG.agentRequest = (name) => (config.agentRequestMessage || "Entendido, *{name}*! 🤝").replace(/{name}/g, name || "");
            MSG.duplicate = config.duplicateMessage || MSG.duplicate;

            botLog("✅ Configuración actualizada.");
        }
    } catch (err) {
        botLog("⚠️ No se pudo cargar info de la agencia.");
    }
}

// ─── Fix Chromium Lock ──────────────────────────────────
function cleanLockFiles() {
    try {
        if (!fs.existsSync(AUTH_PATH)) return;
        
        const deleteLock = (dirPath) => {
            const files = fs.readdirSync(dirPath);
            for (const file of files) {
                const fullPath = path.join(dirPath, file);
                if (fs.statSync(fullPath).isDirectory()) {
                    deleteLock(fullPath);
                } else if (file === "SingletonLock") {
                    botLog(`🧹 Limpiando bloqueo de Chromium: ${fullPath}`);
                    fs.unlinkSync(fullPath);
                }
            }
        };
        
        deleteLock(AUTH_PATH);
    } catch (err) {
        botLog(`❌ Error limpiando locks: ${err.message}`);
    }
}

// ─── Sesiones ────────────────────────────────────────────
const sessions = new Map();

function getSession(chatId) {
    if (sessions.has(chatId)) {
        const s = sessions.get(chatId);
        clearTimeout(s._timer);
        s._timer = setTimeout(() => { sessions.delete(chatId); lastMessageTime.delete(chatId); }, SESSION_TIMEOUT);
        return s;
    }
    return null;
}

function createSession(chatId) {
    if (sessions.has(chatId)) clearTimeout(sessions.get(chatId)._timer);
    const s = {
        state: "GREETING",
        name: null,
        interest: null,
        dates: null,
        travelers: null,
        transcript: [],
        _timer: setTimeout(() => { sessions.delete(chatId); lastMessageTime.delete(chatId); }, SESSION_TIMEOUT),
    };
    sessions.set(chatId, s);
    return s;
}

// ─── Helpers ─────────────────────────────────────────────
const GREETINGS = /^(hola|buenas|buen[oa]s?\s*(tardes?|noches?|d[ií]as?)?|hey|hi|hello|ey|que\s*tal|buenas\s*buenas|holis|holaa+|quiero\s*(viajar|info)|me\s*interesa|consulta)[\s!?.,]*$/i;
const AGENT_REQUEST = /\b(asesor|persona\s*real|humano|agente|hablar\s*con\s*alguien|operador)\b/i;

function isGreeting(text) { return GREETINGS.test(text.trim()); }

function looksLikeName(text) {
    const t = text.trim();
    if (t.length < 2 || t.length > 200) return false;
    if (/^\d+$/.test(t)) return false;
    if (isGreeting(t)) return false;
    if (!/[a-záéíóúñü]/i.test(t)) return false;
    return true;
}

function extractPhone(chatId) {
    const match = chatId.match(/^(\d+)@/);
    return match ? `+${match[1]}` : chatId;
}

function phoneToChatId(phone) {
    return phone.replace(/^\+/, "").replace(/[^0-9]/g, "") + "@c.us";
}

// ─── Webhook ─────────────────────────────────────────────
async function sendToWebhook(phone, session) {
    const payload = {
        name: session.name,
        phone,
        interest: session.interest,
        dates: session.dates,
        travelers: session.travelers,
        transcript: session.transcript.join("\n"),
    };
    try {
        const res = await axios.post(WEBHOOK_URL, payload, {
            headers: { "Content-Type": "application/json", "X-Webhook-Secret": WEBHOOK_SECRET },
            timeout: 10000,
        });
        console.log(`✅ Lead # ${res.data?.leadId} creado: ${session.name} (${phone})`);
        return "ok";
    } catch (error) {
        if (error.response?.status === 409) { console.log(`ℹ️ Duplicado: ${phone}`); return "duplicate"; }
        console.error(`❌ Webhook Error: ${error.response?.status || error.message}`);
        return "error";
    }
}

async function sendMessageToWebhook(phone, message, sender) {
    try {
        await axios.post(`${WEBHOOK_URL}/message`, { phone, message, sender }, {
            headers: { "Content-Type": "application/json", "X-Webhook-Secret": WEBHOOK_SECRET },
            timeout: 5000,
        });
    } catch (err) { /* silent on message log error */ }
}

// ─── WhatsApp Client ─────────────────────────────────────
botLog("🚀 Iniciando motor del bot...");
cleanLockFiles();

const client = new Client({
    authStrategy: new LocalAuth(),
    puppeteer: {
        headless: true,
        args: [
            "--no-sandbox", 
            "--disable-setuid-sandbox", 
            "--disable-dev-shm-usage", 
            "--disable-gpu",
            "--disable-features=SharedArrayBuffer",
            "--no-first-run",
            "--no-zygote",
            "--single-process"
        ],
        executablePath: process.env.PUPPETEER_EXECUTABLE_PATH || null
    }
});

botLog("📦 Cargando cliente de WhatsApp...");
client.initialize().catch(err => {
    botLog(`❌ ERROR FATAL AL LANZAR CHROMIUM: ${err.message}`);
    botLog("💡 Sugerencia: Revisa que no haya otros procesos de Chrome corriendo en el servidor.");
});

client.on("qr", (qr) => {
    lastQR = qr;
    botStatus = "SCAN_QR";
    if (isShowingQR) return;
    isShowingQR = true;
    botLog("📱 NUEVO CÓDIGO QR GENERADO. Esperando escaneo...");
});

client.on("ready", () => {
    botStatus = "READY";
    lastQR = null;
    if (isBotReady) return;
    isBotReady = true;
    isShowingQR = false;
    botLog("✅ BOT CONECTADO Y TRABAJANDO.");
    fetchConfig(); // Cargar al estar listo
});

client.on("authenticated", () => { 
    botStatus = "AUTHENTICATED";
    botLog("🔐 Sesión recuperada. Autenticado."); 
});

client.on("disconnected", (reason) => { 
    botLog(`📴 Bot desconectado: ${reason}`);
    botStatus = "DISCONNECTED";
    isBotReady = false;
    process.exit(1); 
});

// ─── Message Handler ─────────────────────────────────────
client.on("message", async (message) => {
    if (message.from.includes("@g.us") || message.fromMe) return;

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
    console.log(`💬 [${phone}]: ${body.substring(0, 50)}`);

    sendMessageToWebhook(phone, body, "Cliente");

    if (/nueva\s*consulta/i.test(body)) {
        const s = createSession(chatId);
        const welcomeMsg = typeof MSG.welcome === "function" ? MSG.welcome() : MSG.welcome;
        await message.reply(welcomeMsg);
        s.transcript.push(`[Cliente]: ${body}`, `[Bot]: ${welcomeMsg}`);
        s.state = "WAITING_NAME";
        return;
    }

    if (AGENT_REQUEST.test(body)) {
        const s = getSession(chatId) || createSession(chatId);
        const amsg = typeof MSG.agentRequest === "function" ? MSG.agentRequest(s.name) : MSG.agentRequest;
        await message.reply(amsg);
        s.transcript.push(`[Cliente]: ${body}`, `[Bot]: ${amsg}`);
        s.state = "DONE";
        if (s.name) await sendToWebhook(phone, s);
        return;
    }

    let session = getSession(chatId);

    try {
        if (!session) {
            session = createSession(chatId);
            const welcomeMsg = typeof MSG.welcome === "function" ? MSG.welcome() : MSG.welcome;
            await message.reply(welcomeMsg);
            session.transcript.push(`[Cliente]: ${body}`, `[Bot]: ${welcomeMsg}`);
            session.state = "WAITING_NAME";
            return;
        }

        session.transcript.push(`[Cliente]: ${body}`);

        switch (session.state) {
            case "WAITING_NAME":
                if (!looksLikeName(body)) {
                    await message.reply(MSG.badName);
                    session.transcript.push(`[Bot]: ${MSG.badName}`);
                    return;
                }
                session.name = body;
                session.state = "WAITING_INTEREST";
                const m2 = MSG.askInterest(session.name);
                await message.reply(m2);
                session.transcript.push(`[Bot]: ${m2}`);
                break;

            case "WAITING_INTEREST":
                session.interest = body;
                session.state = "WAITING_DATES";
                const m3 = MSG.askDates(session.interest);
                await message.reply(m3);
                session.transcript.push(`[Bot]: ${m3}`);
                break;

            case "WAITING_DATES":
                session.dates = body;
                session.state = "WAITING_TRAVELERS";
                const m4 = MSG.askTravelers();
                await message.reply(m4);
                session.transcript.push(`[Bot]: ${m4}`);
                break;

            case "WAITING_TRAVELERS":
                session.travelers = body;
                session.state = "SENDING";
                const res = await sendToWebhook(phone, session);
                if (res === "duplicate") {
                    await message.reply(MSG.duplicate);
                } else if (res === "ok") {
                    await message.reply(MSG.thanks(session.name));
                } else {
                    await message.reply(MSG.error);
                }
                session.state = "DONE";
                break;
        }
    } catch (err) { console.error("Flow error:", err.message); }
});

// ─── Express Server ──────────────────────────────────────
const app = express();
app.use(express.json());

app.post("/send", async (req, res) => {
    const { phone, message } = req.body;
    if (req.headers["x-webhook-secret"] !== WEBHOOK_SECRET) return res.status(401).send();
    try {
        const chatId = phoneToChatId(phone);
        await client.sendMessage(chatId, message);
        console.log(`📤 [CRM → ${phone}]: ${message.substring(0, 50)}`);
        res.json({ success: true });
    } catch (err) { res.status(500).json({ error: err.message }); }
});

// Endpoint para recargar configuración sin reiniciar
app.post("/reload", async (req, res) => {
    if (req.headers["x-webhook-secret"] !== WEBHOOK_SECRET) return res.status(401).send();
    await fetchConfig();
    res.json({ success: true, status: botStatus });
});

app.get("/status", (req, res) => {
    res.json({ status: botStatus, qr: lastQR, ready: isBotReady });
});

app.get("/logs", (req, res) => {
    res.json(botLogs);
});

app.post("/logout", async (req, res) => {
    if (req.headers["x-webhook-secret"] !== WEBHOOK_SECRET) return res.status(401).send();
    try {
        console.log("🚪 Petición de Logout recibida...");
        await client.logout();
        botStatus = "DISCONNECTED";
        isBotReady = false;
        lastQR = null;
        res.json({ success: true });
        // Dar tiempo a responder antes de salir para que Docker reinicie limpio si es necesario
        setTimeout(() => process.exit(0), 1000);
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

app.listen(HTTP_PORT, "0.0.0.0", () => {
    console.log(`🚀 Bot v3.3 escuchando comandos en puerto ${HTTP_PORT}`);
    fetchConfig(); // Cargar configuración al iniciar el servidor HTTP
});

client.initialize();

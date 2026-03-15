/**
 * MagnaTravel WhatsApp Bot v3
 * ===========================
 * Bot personalizado con flujo de 5 pasos + Express HTTP server
 * para enviar mensajes desde el CRM.
 */

require("dotenv").config();
const { Client, LocalAuth } = require("whatsapp-web.js");
const qrcode = require("qrcode-terminal");
const axios = require("axios");
const express = require("express");

// ─── Config ──────────────────────────────────────────────
const WEBHOOK_URL = process.env.WEBHOOK_URL || "http://localhost:5000/api/webhooks/whatsapp";
const WEBHOOK_SECRET = process.env.WEBHOOK_SECRET || "CHANGE_THIS_SECRET";
const SESSION_TIMEOUT = (parseInt(process.env.BOT_SESSION_TIMEOUT_MINUTES) || 30) * 60 * 1000;
const HTTP_PORT = parseInt(process.env.BOT_HTTP_PORT) || 3001;

// ─── Anti-spam ───────────────────────────────────────────
const processedMessages = new Set();
const MESSAGE_COOLDOWN_MS = 3000;
const lastMessageTime = new Map();

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
    // +5493364670633 → 5493364670633@c.us
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
        console.log(`✅ Lead creado: ID ${res.data?.leadId || "?"} — ${session.name} (${phone})`);
        return "ok";
    } catch (error) {
        if (error.response?.status === 409) { console.log(`ℹ️  Lead duplicado: ${phone}`); return "duplicate"; }
        console.error(`❌ Webhook error: ${error.response?.status || "?"} — ${error.response?.data?.message || error.message}`);
        return "error";
    }
}

// Enviar mensajes individuales al webhook como actividades
async function sendMessageToWebhook(phone, message, sender) {
    try {
        await axios.post(`${WEBHOOK_URL}/message`, {
            phone,
            message,
            sender, // "Cliente" o "Agente"
        }, {
            headers: { "Content-Type": "application/json", "X-Webhook-Secret": WEBHOOK_SECRET },
            timeout: 10000,
        });
    } catch (err) {
        console.error(`❌ Error enviando mensaje al webhook: ${err.message}`);
    }
}

// ─── Mensajes del Bot ────────────────────────────────────
const MSG = {
    welcome:
        `¡Hola! 👋 Bienvenido/a a *MagnaTravel* 🌎✈️\n\n` +
        `Soy tu asistente virtual y estoy acá para ayudarte a planificar tu próximo viaje soñado. 🏖️🗺️\n\n` +
        `Para empezar, *¿me decís tu nombre completo?*`,

    badName: `No pude captar tu nombre 😅 ¿Podés decirme tu *nombre y apellido*?`,

    askInterest: (name) =>
        `¡Un placer, *${name}*! 🤩\n\n` +
        `Contame, *¿qué destino o tipo de viaje te gustaría hacer?*\n\n` +
        `✈️ _Por ejemplo:_\n` +
        `• Cancún 🌴\n` +
        `• Europa (Italia, España, Francia) 🏰\n` +
        `• Crucero por el Caribe 🚢\n` +
        `• Brasil 🇧🇷\n` +
        `• Bariloche ⛷️\n\n` +
        `O cualquier otro destino que tengas en mente 🌍`,

    askDates: (interest) =>
        `¡*${interest}*! Excelente elección 😍\n\n` +
        `¿Tenés alguna *fecha aproximada* en mente para viajar?\n\n` +
        `_Por ejemplo: "marzo 2026", "semana santa", "vacaciones de julio", "todavía no sé"_`,

    askTravelers: () =>
        `Perfecto 📝\n\n` +
        `Última pregunta: *¿cuántas personas viajan?*\n\n` +
        `_Ej: "somos 2", "familia de 4", "soy solo/a", "grupo de 6 amigos"_`,

    thanks: (name) =>
        `¡Genial, *${name}*! Ya tengo toda la info 🎉\n\n` +
        `📋 *Tu consulta fue registrada* y un asesor especializado se va a comunicar con vos a la brevedad para armarte la mejor propuesta.\n\n` +
        `Mientras tanto, podés seguirnos en nuestras redes para ver destinos increíbles 🌟\n\n` +
        `¡Gracias por confiar en *MagnaTravel*! ✨🛫`,

    agentRequest: (name) =>
        `Entendido, *${name || ""}*! 🤝\n\n` +
        `Ya le avisé a un asesor que querés hablar con una persona real. Se va a comunicar con vos lo antes posible.\n\n` +
        `¡Quedate tranquilo/a! 📞`,

    duplicate:
        `¡Hola de nuevo! 😊\n\n` +
        `Tu consulta ya fue registrada y un asesor se va a poner en contacto pronto.\n` +
        `Si es urgente, llamanos directamente al teléfono de la agencia. 📞`,

    error: `Disculpá, tuvimos un inconveniente técnico 😔\nPor favor intentá de nuevo en unos minutos o llamanos directamente.`,

    alreadyDone:
        `¡Hola! Ya recibimos tu consulta anterior 😊\n` +
        `Un asesor se va a poner en contacto con vos pronto.\n\n` +
        `Si querés hacer una *nueva consulta*, escribí *"nueva consulta"* ✍️`,
};

// ─── WhatsApp Client ─────────────────────────────────────
const puppeteerConfig = {
    headless: true,
    args: [
        "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage",
        "--disable-accelerated-2d-canvas", "--no-first-run", "--disable-gpu",
    ],
};
if (process.env.PUPPETEER_EXECUTABLE_PATH) {
    puppeteerConfig.executablePath = process.env.PUPPETEER_EXECUTABLE_PATH;
    console.log(`🐳 Docker: Chromium → ${puppeteerConfig.executablePath}`);
}

const client = new Client({ authStrategy: new LocalAuth(), puppeteer: puppeteerConfig });

// ─── WhatsApp Events ─────────────────────────────────────
client.on("qr", (qr) => {
    console.log("\n📱 Escaneá este código QR con WhatsApp:\n");
    qrcode.generate(qr, { small: true });
    console.log("\n(WhatsApp → Dispositivos vinculados → Vincular dispositivo)\n");
});

client.on("ready", () => {
    console.log("══════════════════════════════════════════════");
    console.log("  🤖 MagnaTravel WhatsApp Bot v3 — CONECTADO");
    console.log("══════════════════════════════════════════════");
    console.log(`  Webhook: ${WEBHOOK_URL}`);
    console.log(`  HTTP Server: http://0.0.0.0:${HTTP_PORT}`);
    console.log(`  Timeout sesión: ${SESSION_TIMEOUT / 60000} min`);
    console.log("  Esperando mensajes...\n");
});

client.on("authenticated", () => console.log("🔐 Sesión autenticada."));
client.on("auth_failure", (msg) => {
    console.error("❌ Auth error:", msg);
    console.log("   Eliminá .wwebjs_auth/ y re-escaneá el QR.");
});
client.on("disconnected", (reason) => {
    console.log("📴 Desconectado:", reason, "— Saliendo para que Docker reinicie...");
    process.exit(1);
});

// ─── Message Handler ─────────────────────────────────────
client.on("message", async (message) => {
    if (message.from.includes("@g.us")) return;
    if (message.from === "status@broadcast") return;
    if (message.fromMe) return;

    const chatId = message.from;
    const body = message.body?.trim();
    if (!body) return;

    // Anti-spam
    const msgKey = `${chatId}_${message.id?.id || message.timestamp}`;
    if (processedMessages.has(msgKey)) return;
    processedMessages.add(msgKey);
    setTimeout(() => processedMessages.delete(msgKey), 60000);

    const now = Date.now();
    if (now - (lastMessageTime.get(chatId) || 0) < MESSAGE_COOLDOWN_MS) return;
    lastMessageTime.set(chatId, now);

    const phone = extractPhone(chatId);
    console.log(`💬 [${phone}]: ${body.substring(0, 100)}${body.length > 100 ? "..." : ""}`);

    // Guardar mensaje del cliente en el CRM (si ya tiene lead)
    sendMessageToWebhook(phone, body, "Cliente");

    // Keyword: nueva consulta
    if (/nueva\s*consulta/i.test(body)) {
        const s = createSession(chatId);
        const msg = MSG.welcome;
        await message.reply(msg);
        s.transcript.push(`[Cliente]: ${body}`, `[Bot]: ${msg}`);
        s.state = "WAITING_NAME";
        console.log(`🔄 [${phone}]: Reinició conversación`);
        return;
    }

    // Keyword: pide asesor humano
    if (AGENT_REQUEST.test(body)) {
        const s = getSession(chatId) || createSession(chatId);
        s.transcript.push(`[Cliente]: ${body}`);
        const msg = MSG.agentRequest(s.name);
        await message.reply(msg);
        s.transcript.push(`[Bot]: ${msg}`);
        s.state = "WAITING_AGENT";
        // Enviar al webhook lo que tenga hasta ahora
        if (s.name) await sendToWebhook(phone, s);
        return;
    }

    let session = getSession(chatId);

    try {
        // ── Sin sesión → crear ──
        if (!session) {
            session = createSession(chatId);

            if (isGreeting(body)) {
                session.transcript.push(`[Cliente]: ${body}`);
                const msg = MSG.welcome;
                await message.reply(msg);
                session.transcript.push(`[Bot]: ${msg}`);
                session.state = "WAITING_NAME";
                return;
            }

            if (looksLikeName(body)) {
                session.transcript.push(`[Cliente]: ${body}`);
                session.name = body.substring(0, 200);
                session.state = "WAITING_INTEREST";
                const msg = MSG.askInterest(session.name);
                await message.reply(msg);
                session.transcript.push(`[Bot]: ${msg}`);
                return;
            }

            session.transcript.push(`[Cliente]: ${body}`);
            const msg = MSG.welcome;
            await message.reply(msg);
            session.transcript.push(`[Bot]: ${msg}`);
            session.state = "WAITING_NAME";
            return;
        }

        session.transcript.push(`[Cliente]: ${body}`);

        // ── WAITING_NAME ──
        if (session.state === "WAITING_NAME") {
            if (!looksLikeName(body)) {
                const msg = MSG.badName;
                await message.reply(msg);
                session.transcript.push(`[Bot]: ${msg}`);
                return;
            }
            session.name = body.substring(0, 200);
            session.state = "WAITING_INTEREST";
            const msg = MSG.askInterest(session.name);
            await message.reply(msg);
            session.transcript.push(`[Bot]: ${msg}`);
            return;
        }

        // ── WAITING_INTEREST ──
        if (session.state === "WAITING_INTEREST") {
            session.interest = body.substring(0, 200);
            session.state = "WAITING_DATES";
            const msg = MSG.askDates(session.interest);
            await message.reply(msg);
            session.transcript.push(`[Bot]: ${msg}`);
            return;
        }

        // ── WAITING_DATES ──
        if (session.state === "WAITING_DATES") {
            session.dates = body.substring(0, 200);
            session.state = "WAITING_TRAVELERS";
            const msg = MSG.askTravelers();
            await message.reply(msg);
            session.transcript.push(`[Bot]: ${msg}`);
            return;
        }

        // ── WAITING_TRAVELERS ──
        if (session.state === "WAITING_TRAVELERS") {
            session.travelers = body.substring(0, 200);
            session.state = "SENDING";

            const result = await sendToWebhook(phone, session);

            if (result === "duplicate") {
                session.state = "DONE";
                await message.reply(MSG.duplicate);
                session.transcript.push(`[Bot]: ${MSG.duplicate}`);
            } else if (result === "ok") {
                session.state = "DONE";
                const msg = MSG.thanks(session.name);
                await message.reply(msg);
                session.transcript.push(`[Bot]: ${msg}`);
            } else {
                session.state = "WAITING_TRAVELERS";
                session.travelers = null;
                await message.reply(MSG.error);
                session.transcript.push(`[Bot]: ${MSG.error}`);
            }
            return;
        }

        // ── WAITING_AGENT (ya pidió humano, pasar mensajes al CRM) ──
        if (session.state === "WAITING_AGENT" || session.state === "DONE") {
            // No responder más, los mensajes se guardan en el CRM via webhook
            return;
        }

    } catch (err) {
        console.error(`❌ Error [${phone}]:`, err.message);
    }
});

// ═══════════════════════════════════════════════════════════
// EXPRESS HTTP SERVER — para enviar mensajes desde el CRM
// ═══════════════════════════════════════════════════════════
const app = express();
app.use(express.json());

// Auth middleware
function authMiddleware(req, res, next) {
    const secret = req.headers["x-webhook-secret"];
    if (!secret || secret !== WEBHOOK_SECRET) {
        return res.status(401).json({ error: "Secret inválido" });
    }
    next();
}

// Health check
app.get("/health", (req, res) => {
    const info = client.info;
    res.json({
        status: "ok",
        connected: !!info,
        phone: info?.wid?.user || null,
        uptime: process.uptime(),
    });
});

// POST /send — Enviar mensaje a un número de WhatsApp
app.post("/send", authMiddleware, async (req, res) => {
    const { phone, message } = req.body;

    if (!phone || !message) {
        return res.status(400).json({ error: "phone y message son obligatorios" });
    }

    try {
        const chatId = phoneToChatId(phone);
        await client.sendMessage(chatId, message);
        console.log(`📤 [CRM → ${phone}]: ${message.substring(0, 80)}${message.length > 80 ? "..." : ""}`);
        res.json({ success: true, chatId });
    } catch (err) {
        console.error(`❌ Error enviando a ${phone}:`, err.message);
        res.status(500).json({ error: err.message });
    }
});

app.listen(HTTP_PORT, "0.0.0.0", () => {
    console.log(`🌐 HTTP Server escuchando en puerto ${HTTP_PORT}`);
});

// ─── Arranque ────────────────────────────────────────────
console.log("🚀 Iniciando MagnaTravel WhatsApp Bot v3...\n");
client.initialize();

process.on("SIGINT", async () => { console.log("\n🛑 Cerrando bot..."); await client.destroy(); process.exit(0); });
process.on("SIGTERM", async () => { await client.destroy(); process.exit(0); });

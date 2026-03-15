/**
 * MagnaTravel WhatsApp Bot v2
 * ===========================
 * Bot inteligente que captura leads desde WhatsApp.
 * Flujo natural, tolerante a errores, anti-duplicados.
 */

require("dotenv").config();
const { Client, LocalAuth } = require("whatsapp-web.js");
const qrcode = require("qrcode-terminal");
const axios = require("axios");

// ─── Config ──────────────────────────────────────────────
const WEBHOOK_URL = process.env.WEBHOOK_URL || "http://localhost:5000/api/webhooks/whatsapp";
const WEBHOOK_SECRET = process.env.WEBHOOK_SECRET || "CHANGE_THIS_SECRET";
const SESSION_TIMEOUT = (parseInt(process.env.BOT_SESSION_TIMEOUT_MINUTES) || 30) * 60 * 1000;

// ─── Anti-spam: cooldown por mensaje ─────────────────────
const processedMessages = new Set();
const MESSAGE_COOLDOWN_MS = 3000; // Ignorar mensajes del mismo chat dentro de 3 seg

// ─── Sesiones ────────────────────────────────────────────
// Estados: GREETING → WAITING_NAME → WAITING_INTEREST → DONE
const sessions = new Map();
const lastMessageTime = new Map(); // Anti-spam per chat

function getSession(chatId) {
    if (sessions.has(chatId)) {
        const s = sessions.get(chatId);
        clearTimeout(s._timer);
        s._timer = setTimeout(() => {
            sessions.delete(chatId);
            lastMessageTime.delete(chatId);
        }, SESSION_TIMEOUT);
        return s;
    }
    return null;
}

function createSession(chatId) {
    // Limpiar sesión previa si existía
    if (sessions.has(chatId)) {
        clearTimeout(sessions.get(chatId)._timer);
    }
    const s = {
        state: "GREETING",
        name: null,
        interest: null,
        transcript: [],
        _timer: setTimeout(() => {
            sessions.delete(chatId);
            lastMessageTime.delete(chatId);
        }, SESSION_TIMEOUT),
    };
    sessions.set(chatId, s);
    return s;
}

// ─── Detección de saludos ────────────────────────────────
const GREETINGS = /^(hola|buenas|buen[oa]s?\s*(tardes?|noches?|d[ií]as?)?|hey|hi|hello|ey|que\s*tal|buenas\s*buenas|holis|holaa+)[\s!?.,]*$/i;

function isGreeting(text) {
    return GREETINGS.test(text.trim());
}

// ─── Detectar si parece un nombre ────────────────────────
function looksLikeName(text) {
    const trimmed = text.trim();
    // Un nombre tiene al menos 2 caracteres, no es solo números, no es un saludo
    if (trimmed.length < 2 || trimmed.length > 200) return false;
    if (/^\d+$/.test(trimmed)) return false;
    if (isGreeting(trimmed)) return false;
    // Tiene al menos una letra
    if (!/[a-záéíóúñü]/i.test(trimmed)) return false;
    return true;
}

// ─── Extraer teléfono del chatId ─────────────────────────
function extractPhone(chatId) {
    const match = chatId.match(/^(\d+)@/);
    return match ? `+${match[1]}` : chatId;
}

// ─── Webhook ─────────────────────────────────────────────
async function sendToWebhook(phone, session) {
    const payload = {
        name: session.name,
        phone: phone,
        interest: session.interest,
        transcript: session.transcript.join("\n"),
    };

    try {
        const response = await axios.post(WEBHOOK_URL, payload, {
            headers: {
                "Content-Type": "application/json",
                "X-Webhook-Secret": WEBHOOK_SECRET,
            },
            timeout: 10000,
        });
        console.log(`✅ Lead creado: ID ${response.data?.leadId || "?"} — ${session.name} (${phone})`);
        return "ok";
    } catch (error) {
        const status = error.response?.status;
        if (status === 409) {
            console.log(`ℹ️  Lead duplicado: ${phone}`);
            return "duplicate";
        }
        console.error(`❌ Webhook error: ${status || "?"} — ${error.response?.data?.message || error.message}`);
        return "error";
    }
}

// ─── Mensajes ────────────────────────────────────────────
const MSG = {
    welcome: "¡Hola! 👋 Bienvenido/a a *MagnaTravel* 🌎✈️\n\nSoy el asistente virtual de la agencia. Para poder ayudarte, ¿me decís tu nombre completo?",
    badName: "Disculpá, no entendí bien. ¿Podés decirme tu nombre y apellido? 😊",
    askInterest: (name) =>
        `¡Gracias, *${name}*! 😊\n\n¿Qué destino o tipo de viaje te interesa?\nPor ejemplo: _Cancún, Europa, Crucero, Bariloche, etc._`,
    thanks: (name) =>
        `¡Excelente, *${name}*! 🙌\n\nUn asesor de viajes va a comunicarse con vos a la brevedad para armarte la mejor propuesta.\n\n¡Gracias por confiar en *MagnaTravel*! ✨`,
    duplicate: "¡Hola de nuevo! 😊 Tu consulta ya fue registrada y un asesor se va a poner en contacto. Si es urgente, llamanos directamente al teléfono de la agencia. 📞",
    error: "Disculpá, tuvimos un inconveniente técnico. 😔 Por favor intentá de nuevo en unos minutos o llamanos directamente.",
    alreadyDone: "¡Hola! Ya recibimos tu consulta anterior. 😊 Un asesor se va a poner en contacto con vos pronto.\n\nSi querés hacer una nueva consulta, escribí *\"nueva consulta\"*.",
};

// ─── Cliente WhatsApp ────────────────────────────────────
const puppeteerConfig = {
    headless: true,
    args: [
        "--no-sandbox",
        "--disable-setuid-sandbox",
        "--disable-dev-shm-usage",
        "--disable-accelerated-2d-canvas",
        "--no-first-run",
        "--disable-gpu",
    ],
};

if (process.env.PUPPETEER_EXECUTABLE_PATH) {
    puppeteerConfig.executablePath = process.env.PUPPETEER_EXECUTABLE_PATH;
    console.log(`🐳 Docker: usando Chromium de ${puppeteerConfig.executablePath}`);
}

const client = new Client({
    authStrategy: new LocalAuth(),
    puppeteer: puppeteerConfig,
});

// ─── Eventos ─────────────────────────────────────────────
client.on("qr", (qr) => {
    console.log("\n📱 Escaneá este código QR con WhatsApp:\n");
    qrcode.generate(qr, { small: true });
    console.log("\n(WhatsApp → Dispositivos vinculados → Vincular dispositivo)\n");
});

client.on("ready", () => {
    console.log("══════════════════════════════════════════════");
    console.log("  🤖 MagnaTravel WhatsApp Bot v2 — CONECTADO");
    console.log("══════════════════════════════════════════════");
    console.log(`  Webhook: ${WEBHOOK_URL}`);
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
    process.exit(1); // Docker restart: unless-stopped se encarga de reiniciar
});

// ─── Handler Principal ───────────────────────────────────
client.on("message", async (message) => {
    // Filtros básicos
    if (message.from.includes("@g.us")) return;
    if (message.from === "status@broadcast") return;
    if (message.fromMe) return;

    const chatId = message.from;
    const body = message.body?.trim();
    if (!body) return;

    // ── Anti-spam: ignorar mensajes repetidos rápidos ──
    const msgKey = `${chatId}_${message.id?.id || message.timestamp}`;
    if (processedMessages.has(msgKey)) return;
    processedMessages.add(msgKey);
    setTimeout(() => processedMessages.delete(msgKey), 60000);

    const now = Date.now();
    const lastTime = lastMessageTime.get(chatId) || 0;
    if (now - lastTime < MESSAGE_COOLDOWN_MS) {
        return; // Ignorar mensajes muy seguidos del mismo chat
    }
    lastMessageTime.set(chatId, now);

    const phone = extractPhone(chatId);
    console.log(`💬 [${phone}]: ${body.substring(0, 100)}${body.length > 100 ? "..." : ""}`);

    // ── Permitir reiniciar con "nueva consulta" ──
    if (/nueva\s*consulta/i.test(body)) {
        createSession(chatId);
        const msg = MSG.welcome;
        await message.reply(msg);
        console.log(`🔄 [${phone}]: Reinició conversación`);
        return;
    }

    let session = getSession(chatId);

    try {
        // ── Sin sesión → crear una nueva ──
        if (!session) {
            session = createSession(chatId);

            // Si el primer mensaje parece un saludo, responder con bienvenida
            if (isGreeting(body)) {
                session.transcript.push(`[Cliente]: ${body}`);
                const msg = MSG.welcome;
                await message.reply(msg);
                session.transcript.push(`[Bot]: ${msg}`);
                session.state = "WAITING_NAME";
                return;
            }

            // Si no es saludo, tal vez ya dijo su nombre directo
            if (looksLikeName(body)) {
                session.transcript.push(`[Cliente]: ${body}`);
                session.name = body.substring(0, 200);
                session.state = "WAITING_INTEREST";
                const msg = MSG.askInterest(session.name);
                await message.reply(msg);
                session.transcript.push(`[Bot]: ${msg}`);
                return;
            }

            // Caso raro: algo que no es saludo ni nombre
            session.transcript.push(`[Cliente]: ${body}`);
            const msg = MSG.welcome;
            await message.reply(msg);
            session.transcript.push(`[Bot]: ${msg}`);
            session.state = "WAITING_NAME";
            return;
        }

        session.transcript.push(`[Cliente]: ${body}`);

        // ── Estado: esperando nombre ──
        if (session.state === "WAITING_NAME") {
            if (!looksLikeName(body)) {
                // No parece un nombre, re-preguntar amablemente
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

        // ── Estado: esperando destino/interés ──
        if (session.state === "WAITING_INTEREST") {
            session.interest = body.substring(0, 200);
            session.state = "SENDING";

            const result = await sendToWebhook(phone, session);

            if (result === "duplicate") {
                session.state = "DONE";
                const msg = MSG.duplicate;
                await message.reply(msg);
                session.transcript.push(`[Bot]: ${msg}`);
            } else if (result === "ok") {
                session.state = "DONE";
                const msg = MSG.thanks(session.name);
                await message.reply(msg);
                session.transcript.push(`[Bot]: ${msg}`);
            } else {
                // Error → volver a pedir interés
                session.state = "WAITING_INTEREST";
                session.interest = null;
                const msg = MSG.error;
                await message.reply(msg);
                session.transcript.push(`[Bot]: ${msg}`);
            }
            return;
        }

        // ── Estado: ya terminó ──
        if (session.state === "DONE") {
            const msg = MSG.alreadyDone;
            await message.reply(msg);
            return;
        }

    } catch (err) {
        console.error(`❌ Error [${phone}]:`, err.message);
    }
});

// ─── Arranque ────────────────────────────────────────────
console.log("🚀 Iniciando MagnaTravel WhatsApp Bot v2...\n");
client.initialize();

process.on("SIGINT", async () => {
    console.log("\n🛑 Cerrando bot...");
    await client.destroy();
    process.exit(0);
});
process.on("SIGTERM", async () => {
    await client.destroy();
    process.exit(0);
});

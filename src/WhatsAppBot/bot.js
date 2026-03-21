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

let agencyName = "MagnaTravel";
let MSG = {
    welcome: () => `Hola! Bienvenido/a a *${agencyName}*.\n\nSoy tu asistente virtual y estoy para ayudarte con tu viaje.\n\nPara empezar, me decis tu nombre completo?`,
    badName: "No pude captar tu nombre. Me decis tu nombre y apellido?",
    askInterest: (name) => `Un placer, *${name}*.\n\nQue destino o tipo de viaje te gustaria hacer?`,
    askDates: (interest) => `Perfecto, *${interest}*.\n\nTenes alguna fecha aproximada para viajar?`,
    askTravelers: () => "Ultima pregunta: cuantas personas viajan?",
    thanks: (name) => `Genial, *${name}*! Ya registre tu consulta y un asesor se va a comunicar con vos a la brevedad.`,
    agentRequest: (name) => `Entendido, *${name || ""}*. Ya le avise a un asesor para que te contacte personalmente.`,
    duplicate: "Tu consulta ya fue registrada y estamos trabajando en tu propuesta.",
    error: "Hubo un problema al registrar la consulta. Intenta nuevamente o llamanos por telefono."
};

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
        const res = await axios.get(`${API_URL}/api/whatsapp/config/env`);
        const { config, agencyName: loadedAgencyName } = res.data;

        if (loadedAgencyName) {
            agencyName = loadedAgencyName;
        }

        if (config) {
            MSG.welcome = () => (config.welcomeMessage || "Hola! Bienvenido/a a *{agencyName}*.").replace(/{agencyName}/g, agencyName);
            MSG.askInterest = (name) => (config.askInterestMessage || "Un placer, *{name}*.").replace(/{name}/g, name);
            MSG.askDates = (interest) => (config.askDatesMessage || "Perfecto, *{interest}*.").replace(/{interest}/g, interest);
            MSG.askTravelers = () => config.askTravelersMessage || "Ultima pregunta: cuantas personas viajan?";
            MSG.thanks = (name) => (config.thanksMessage || "Gracias, *{name}*.").replace(/{name}/g, name).replace(/{agencyName}/g, agencyName);
            MSG.agentRequest = (name) => (config.agentRequestMessage || "Entendido, *{name}*.").replace(/{name}/g, name || "");
            MSG.duplicate = config.duplicateMessage || MSG.duplicate;
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

function isGreeting(text) {
    return GREETINGS.test(text.trim());
}

function looksLikeName(text) {
    const trimmed = text.trim();
    if (trimmed.length < 2 || trimmed.length > 200) return false;
    if (/^\d+$/.test(trimmed)) return false;
    if (isGreeting(trimmed)) return false;
    if (!/[a-záéíóúñü]/i.test(trimmed)) return false;
    return true;
}

function extractPhone(chatId) {
    const match = chatId.match(/^(\d+)@/);
    return match ? `+${match[1]}` : chatId;
}

function phoneToChatId(phone) {
    return phone.replace(/^\+/, "").replace(/[^0-9]/g, "") + "@c.us";
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
        botLog(`Lead ${res.data?.leadId || "?"} creado para ${phone}`);
        return "ok";
    } catch (error) {
        if (error.response?.status === 409) return "duplicate";
        botLog(`Webhook error: ${error.response?.status || error.message}`);
        return "error";
    }
}

async function sendMessageToWebhook(phone, message, sender) {
    try {
        await axios.post(`${WEBHOOK_URL}/message`, { phone, message, sender }, {
            headers: { "Content-Type": "application/json", "X-Webhook-Secret": WEBHOOK_SECRET },
            timeout: 5000
        });
    } catch {
        // Silent logging failure.
    }
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
    for (let attempt = 0; attempt < retries; attempt += 1) {
        try {
            cleanLockFiles();
            botLog(`Iniciando cliente WhatsApp (${attempt + 1}/${retries})...`);
            await client.initialize();
            return;
        } catch (err) {
            botLog(`Error lanzando Chromium: ${err.message}`);
            if (attempt < retries - 1) {
                await new Promise((resolve) => setTimeout(resolve, 10000));
            }
        }
    }
}

startBot();

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

client.on("disconnected", (reason) => {
    botStatus = "DISCONNECTED";
    isBotReady = false;
    botLog(`Bot desconectado: ${reason}`);
    process.exit(1);
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

    sendMessageToWebhook(phone, body, "Cliente");

    if (/nueva\s*consulta/i.test(body)) {
        const session = createSession(chatId);
        const welcome = typeof MSG.welcome === "function" ? MSG.welcome() : MSG.welcome;
        await message.reply(welcome);
        session.transcript.push(`[Cliente]: ${body}`, `[Bot]: ${welcome}`);
        session.state = "WAITING_NAME";
        return;
    }

    if (AGENT_REQUEST.test(body)) {
        const session = getSession(chatId) || createSession(chatId);
        const agentMessage = typeof MSG.agentRequest === "function" ? MSG.agentRequest(session.name) : MSG.agentRequest;
        await message.reply(agentMessage);
        session.transcript.push(`[Cliente]: ${body}`, `[Bot]: ${agentMessage}`);
        session.state = "DONE";
        if (session.name) await sendToWebhook(phone, session);
        return;
    }

    let session = getSession(chatId);

    try {
        if (!session) {
            session = createSession(chatId);
            const welcome = typeof MSG.welcome === "function" ? MSG.welcome() : MSG.welcome;
            await message.reply(welcome);
            session.transcript.push(`[Cliente]: ${body}`, `[Bot]: ${welcome}`);
            session.state = "WAITING_NAME";
            return;
        }

        session.transcript.push(`[Cliente]: ${body}`);

        switch (session.state) {
            case "WAITING_NAME": {
                if (!looksLikeName(body)) {
                    await message.reply(MSG.badName);
                    session.transcript.push(`[Bot]: ${MSG.badName}`);
                    return;
                }
                session.name = body;
                session.state = "WAITING_INTEREST";
                const nextMessage = MSG.askInterest(session.name);
                await message.reply(nextMessage);
                session.transcript.push(`[Bot]: ${nextMessage}`);
                break;
            }

            case "WAITING_INTEREST": {
                session.interest = body;
                session.state = "WAITING_DATES";
                const nextMessage = MSG.askDates(session.interest);
                await message.reply(nextMessage);
                session.transcript.push(`[Bot]: ${nextMessage}`);
                break;
            }

            case "WAITING_DATES": {
                session.dates = body;
                session.state = "WAITING_TRAVELERS";
                const nextMessage = MSG.askTravelers();
                await message.reply(nextMessage);
                session.transcript.push(`[Bot]: ${nextMessage}`);
                break;
            }

            case "WAITING_TRAVELERS": {
                session.travelers = body;
                session.state = "SENDING";
                const result = await sendToWebhook(phone, session);
                if (result === "duplicate") {
                    await message.reply(MSG.duplicate);
                } else if (result === "ok") {
                    await message.reply(MSG.thanks(session.name));
                } else {
                    await message.reply(MSG.error);
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

app.get("/status", (_req, res) => {
    res.json({ status: botStatus, qr: lastQR, ready: isBotReady });
});

app.get("/logs", (_req, res) => {
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

using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Domain.Entities;

namespace TravelApi.Application.Ai;

/// <summary>
/// Un proveedor de la lista, con todo lo que la pantalla necesita para dibujarlo: el nombre de la
/// calle, la bajada de una linea, y la direccion + modelo recomendados que se precargan solos al
/// elegirlo.
/// </summary>
/// <param name="Provider">Valor interno que se guarda en la base. NO viaja al navegador.</param>
/// <param name="Code">Codigo en texto que SI viaja (contrato con el front).</param>
/// <param name="DisplayName">Como se llama en la calle: "Groq", "OpenAI", "Claude"...</param>
/// <param name="Tagline">La bajada de UNA linea, en criollo, sin palabras tecnicas.</param>
/// <param name="BaseUrl">Direccion recomendada. Vacia en "Otra": la carga el usuario.</param>
/// <param name="Model">Modelo recomendado. Vacio en "Otra": lo carga el usuario.</param>
/// <param name="IsRecommended">El que viene marcado cuando no hay nada configurado.</param>
/// <param name="RequiresManualEndpoint">Si es true, direccion y modelo son obligatorios a mano.</param>
public sealed record AiProviderPreset(
    AiProviderKey Provider,
    string Code,
    string DisplayName,
    string Tagline,
    string BaseUrl,
    string Model,
    bool IsRecommended,
    bool RequiresManualEndpoint);

/// <summary>
/// M-32 de la spec firmada 2026-08-07 (§15.10): <b>la lista de proveedores vive en el motor</b>,
/// no escrita a mano en la pantalla. Asi, agregar un proveedor manana no obliga a tocar el front.
///
/// <para><b>Por que estos y no otros</b>: el motor habla el formato compatible con OpenAI, asi que
/// entra cualquiera que lo hable con tres datos (direccion, clave, modelo). <b>GitHub Copilot y
/// "Codex" NO se pueden conectar asi y por eso NO se ofrecen</b> (§15 de la spec): ofrecerlos seria
/// prometer algo que no funciona.</para>
///
/// <para><b>Los modelos recomendados son VOLATILES</b>: los proveedores los renombran y los dan de
/// baja seguido. Por eso son un default editable (Ajustes avanzados) y no una constante inmutable;
/// si un modelo deja de existir, el probador de conexion lo dice con el codigo "modeloInexistente"
/// y el dueño escribe el nuevo nombre sin esperar un deploy.</para>
/// </summary>
public static class AiProviderPresets
{
    /// <summary>
    /// La lista, en el orden en que se muestra. Groq primero porque es el recomendado.
    /// </summary>
    public static IReadOnlyList<AiProviderPreset> All { get; } = new[]
    {
        new AiProviderPreset(
            AiProviderKey.Groq,
            Code: "groq",
            DisplayName: "Groq",
            Tagline: "Gratis para arrancar. Es la más simple.",
            BaseUrl: "https://api.groq.com/openai/v1",
            // 2026-08-15: Groq discontinuó llama-3.3-70b-versatile (deja de responder el 16/08);
            // su reemplazo recomendado por Groq es este modelo abierto de OpenAI.
            Model: "openai/gpt-oss-120b",
            IsRecommended: true,
            RequiresManualEndpoint: false),

        new AiProviderPreset(
            AiProviderKey.OpenAi,
            Code: "openai",
            DisplayName: "OpenAI",
            Tagline: "La de ChatGPT.",
            BaseUrl: "https://api.openai.com/v1",
            Model: "gpt-4o-mini",
            IsRecommended: false,
            RequiresManualEndpoint: false),

        new AiProviderPreset(
            AiProviderKey.Anthropic,
            Code: "claude",
            DisplayName: "Claude",
            Tagline: "La de Anthropic.",
            BaseUrl: "https://api.anthropic.com/v1",
            Model: "claude-3-5-sonnet-latest",
            IsRecommended: false,
            RequiresManualEndpoint: false),

        new AiProviderPreset(
            AiProviderKey.Gemini,
            Code: "gemini",
            DisplayName: "Gemini",
            Tagline: "La de Google.",
            BaseUrl: "https://generativelanguage.googleapis.com/v1beta/openai",
            Model: "gemini-2.0-flash",
            IsRecommended: false,
            RequiresManualEndpoint: false),

        new AiProviderPreset(
            AiProviderKey.Grok,
            Code: "grok",
            DisplayName: "Grok",
            Tagline: "La de X.",
            BaseUrl: "https://api.x.ai/v1",
            Model: "grok-2-latest",
            IsRecommended: false,
            RequiresManualEndpoint: false),

        new AiProviderPreset(
            AiProviderKey.OpenRouter,
            Code: "openrouter",
            DisplayName: "OpenRouter",
            Tagline: "Una sola clave para usar varias.",
            BaseUrl: "https://openrouter.ai/api/v1",
            Model: "meta-llama/llama-3.3-70b-instruct",
            IsRecommended: false,
            RequiresManualEndpoint: false),

        new AiProviderPreset(
            AiProviderKey.Other,
            Code: "otra",
            DisplayName: "Otra",
            Tagline: "Ponés la dirección y el modelo a mano.",
            BaseUrl: string.Empty,
            Model: string.Empty,
            IsRecommended: false,
            RequiresManualEndpoint: true),
    };

    /// <summary>El que viene marcado cuando la instalacion todavia no configuro nada.</summary>
    public static AiProviderPreset Default =>
        All.First(preset => preset.IsRecommended);

    /// <summary>
    /// Busca por el codigo en texto que manda el front ("groq", "openai"...). Devuelve null si el
    /// codigo no existe, para que el llamador conteste un error de validacion en vez de adivinar.
    /// </summary>
    public static AiProviderPreset? FindByCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return All.FirstOrDefault(preset =>
            string.Equals(preset.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Busca por el valor interno guardado en la base. Si la base tuviera un valor desconocido
    /// (por ejemplo, una fila escrita por una version futura), cae al recomendado en vez de romper.
    /// </summary>
    public static AiProviderPreset FindByProvider(AiProviderKey provider)
    {
        return All.FirstOrDefault(preset => preset.Provider == provider) ?? Default;
    }
}

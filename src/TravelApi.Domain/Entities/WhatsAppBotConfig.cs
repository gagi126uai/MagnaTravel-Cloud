using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

public class WhatsAppBotConfig
{
    public int Id { get; set; }

    [MaxLength(1000)]
    public string WelcomeMessage { get; set; } = "¡Hola! 👋 Bienvenido/a a *MagnaTravel* 🌎✈️\n\nSoy tu asistente virtual y estoy acá para ayudarte a planificar tu próximo viaje soñado. 🏖️🗺️\n\nPara empezar, *¿me decís tu nombre completo?*";

    [MaxLength(1000)]
    public string AskInterestMessage { get; set; } = "¡Un placer, *{name}*! 🤩\n\nContame, *¿qué destino o tipo de viaje te gustaría hacer?*\n\n✈️ _Ej: Cancún, Europa, Crucero, Brasil, Bariloche..._";

    [MaxLength(1000)]
    public string AskDatesMessage { get; set; } = "¡*{interest}*! Excelente elección 😍\n\n¿Tenés alguna *fecha aproximada* en mente para viajar? 📅\n\n_Ej: \"marzo 2026\", \"semana santa\", \"todavía no sé\"_";

    [MaxLength(1000)]
    public string AskTravelersMessage { get; set; } = "Perfecto 📝\n\nÚltima pregunta: *¿cuántas personas viajan?* 👥\n\n_Ej: \"somos 2\", \"familia de 4\", \"soy solo/a\", \"grupo de amigos\"_";

    [MaxLength(1000)]
    public string ThanksMessage { get; set; } = "¡Genial, *{name}*! Ya tengo toda la info 🎉\n\n📋 *Tu consulta fue registrada* y un asesor se va a comunicar con vos a la brevedad.\n\n¡Gracias por confiar en *MagnaTravel*! ✨🛫";

    [MaxLength(1000)]
    public string AgentRequestMessage { get; set; } = "Entendido, *{name}*! 🤝 Ya le avisé a un asesor para que te contacte personalmente.📞";

    [MaxLength(1000)]
    public string DuplicateMessage { get; set; } = "¡Hola de nuevo! 😊 Tu consulta ya fue registrada y estamos trabajando en tu propuesta.\nSi es algo urgente, podés llamarnos directamente. 📞";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

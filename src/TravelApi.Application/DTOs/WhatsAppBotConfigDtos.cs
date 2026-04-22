namespace TravelApi.Application.DTOs;

public class WhatsAppBotConfigDto
{
    public string WelcomeMessage { get; set; } = string.Empty;
    public string AskInterestMessage { get; set; } = string.Empty;
    public string AskDatesMessage { get; set; } = string.Empty;
    public string AskTravelersMessage { get; set; } = string.Empty;
    public string ThanksMessage { get; set; } = string.Empty;
    public string AgentRequestMessage { get; set; } = string.Empty;
    public string DuplicateMessage { get; set; } = string.Empty;
}

public class UpdateWhatsAppBotConfigRequest
{
    public string WelcomeMessage { get; set; } = string.Empty;
    public string AskInterestMessage { get; set; } = string.Empty;
    public string AskDatesMessage { get; set; } = string.Empty;
    public string AskTravelersMessage { get; set; } = string.Empty;
    public string ThanksMessage { get; set; } = string.Empty;
    public string AgentRequestMessage { get; set; } = string.Empty;
    public string DuplicateMessage { get; set; } = string.Empty;
}

public class WhatsAppBotEnvironmentDto
{
    public WhatsAppBotConfigDto Config { get; set; } = new();
    public string AgencyName { get; set; } = string.Empty;
}

namespace PartyApp.Api.Modules.Auth.Requests;

public record RegisterRequest(
    string Username, 
    string DisplayName, 
    string Password);
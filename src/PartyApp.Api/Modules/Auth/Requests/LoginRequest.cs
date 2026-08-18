namespace PartyApp.Api.Modules.Auth.Requests;

public record LoginRequest(
    string Username, 
    string Password);
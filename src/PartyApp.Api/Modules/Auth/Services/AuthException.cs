using PartyApp.Api.Common.Extensions;

namespace PartyApp.Api.Modules.Auth.Services;

public class AuthException : AppException
{
    public AuthException(string message) : base(message)
    {
    }
}
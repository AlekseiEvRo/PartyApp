namespace PartyApp.Api.Common.Extensions;

public class AppException : Exception
{
    public AppException(string message) : base(message)
    {
    }
}
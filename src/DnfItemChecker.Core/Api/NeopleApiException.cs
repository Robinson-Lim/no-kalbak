namespace DnfItemChecker.Core.Api;

/// <summary>Raised when the Neople API returns a non-success status or an error body.</summary>
public sealed class NeopleApiException : Exception
{
    public int StatusCode { get; }
    public string? ErrorCode { get; }

    public NeopleApiException(int statusCode, string? errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}

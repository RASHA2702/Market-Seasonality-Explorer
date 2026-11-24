namespace FiatCryptoGateway.Application;

/// <summary>The requested quote, order or customer does not exist for this API client.</summary>
public class ResourceNotFoundException : Exception
{
    public ResourceNotFoundException()
        : this("NOT_FOUND", "The resource does not exist.")
    {
    }

    public ResourceNotFoundException(string message)
        : this("NOT_FOUND", message)
    {
    }

    public ResourceNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "NOT_FOUND";
    }

    public ResourceNotFoundException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

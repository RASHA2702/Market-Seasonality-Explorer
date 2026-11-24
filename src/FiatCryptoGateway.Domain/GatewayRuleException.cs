namespace FiatCryptoGateway.Domain;

/// <summary>
/// A request broke a business rule. <see cref="Code"/> is a stable, machine-readable reason that is
/// safe to return to API clients; the message is for people.
/// </summary>
public class GatewayRuleException : Exception
{
    public GatewayRuleException()
        : this("RULE_VIOLATION", "A gateway rule rejected the request.")
    {
    }

    public GatewayRuleException(string message)
        : this("RULE_VIOLATION", message)
    {
    }

    public GatewayRuleException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "RULE_VIOLATION";
    }

    public GatewayRuleException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

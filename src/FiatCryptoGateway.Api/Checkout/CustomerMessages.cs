using FiatCryptoGateway.Domain.Payments;

namespace FiatCryptoGateway.Api.Checkout;

/// <summary>
/// What the checkout page tells a customer when a payment does not go through. One place for the wording, so it is easy
/// to review: every message is short, says what to do next, says "not charged" only where that is certain, and never
/// repeats anything the customer typed. Codes are stable; the page only ever picks a message by code, so nothing from
/// the address bar or a form is shown back to the customer.
/// </summary>
public static class CustomerMessages
{
    private const string Fallback = "That did not work. Check the details and try again.";

    private static readonly Dictionary<string, string> Declines = new(StringComparer.Ordinal)
    {
        [DeclineCodes.DoNotHonor] = "Your bank declined this payment. Try another card or a different way to pay.",
        [DeclineCodes.InsufficientFunds] = "There was not enough money available. Try another card or a different way to pay.",
        [DeclineCodes.ExpiredCard] = "This card has expired. Try another card.",
        [DeclineCodes.IncorrectSecurityCode] = "The security code was not accepted. Check it and try again.",
        [DeclineCodes.InvalidUpiAddress] = "That UPI address could not be found. Check it and try again.",
        [DeclineCodes.CustomerDeclined] = "You declined the request, so nothing was charged. You can try again.",
        [DeclineCodes.AuthenticationFailed] = "Your bank could not confirm it was you, so nothing was charged. Try again or use a different way to pay.",
        [DeclineCodes.RailUnavailable] = "We could not reach the payment network, so nothing was charged. Try again in a moment.",
        [DeclineCodes.TimedOut] = "The request timed out, so nothing was charged. Try again.",
        [DeclineCodes.Superseded] = "A newer payment attempt replaced this one.",
        [DeclineCodes.OrderClosed] = "This payment can no longer be made. Ask the merchant for a new payment link.",
        [DeclineCodes.AuthorizationLapsed] = "We could not complete this payment. Check your account, and contact the merchant if money was taken.",
    };

    private static readonly Dictionary<string, string> Refusals = new(StringComparer.Ordinal)
    {
        ["INVALID_CARD_NUMBER"] = "That card number does not look right. Check it and try again.",
        ["TEST_CARDS_ONLY"] = "This is a sandbox, so only the listed test cards work. Your card was not accepted and nothing was kept.",
        ["INVALID_EXPIRY"] = "The expiry date is not valid. Use the month and year on the card, in the future.",
        ["INVALID_SECURITY_CODE"] = "The security code is not valid. It has 3 digits, or 4 on American Express.",
        ["INVALID_INSTRUMENT"] = "Those payment details could not be read. Check them and try again.",
        ["INVALID_UPI_ADDRESS"] = "A UPI address looks like name@bank.",
        ["WRONG_CODE"] = "That code was not right. Check the message from your bank and try again.",
        ["TEST_UPI_ONLY"] = "This is a sandbox, so only the listed test UPI addresses work. Your address was not accepted and nothing was kept.",
        ["PAYMENT_METHOD_UNAVAILABLE"] = "That way of paying is not available.",
        ["ORDER_NOT_PAYABLE"] = "This payment can no longer be made. Ask the merchant for a new payment link.",
        ["ORDER_ALREADY_PAID"] = "This order has already been paid.",
        ["PAYMENT_IN_PROGRESS"] = "Your payment is being completed. This page updates by itself.",
        ["TOO_MANY_ATTEMPTS"] = "There have been too many attempts for this order. Ask the merchant for a new payment link.",
    };

    /// <summary>The decline codes that have a message. Every <see cref="DeclineCodes"/> value must be one of them.</summary>
    public static IReadOnlyCollection<string> DeclineCodesWithMessages => Declines.Keys;

    /// <summary>The refusal codes the payment form can send back. Anything else gets the general message.</summary>
    public static IReadOnlyCollection<string> RefusalCodesWithMessages => Refusals.Keys;

    /// <summary>Why an attempt ended. An unknown code gets the general message and never claims anything about charges.</summary>
    public static string ForDecline(string? code) => code is not null && Declines.TryGetValue(code, out var text) ? text : Fallback;

    /// <summary>Why the form was refused. Takes the code from the address bar, so an unknown code gets the general message.</summary>
    public static string ForRefusal(string? code) => code is not null && Refusals.TryGetValue(code, out var text) ? text : Fallback;

    /// <summary>Whether a code from the address bar is one this page knows how to explain.</summary>
    public static bool IsKnownRefusal(string? code) => code is not null && Refusals.ContainsKey(code);
}

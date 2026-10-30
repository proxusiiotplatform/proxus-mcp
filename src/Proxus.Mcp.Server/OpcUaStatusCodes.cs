using Proxus.Drivers;

namespace Proxus.Mcp;

/// <summary>
///     Names and explains OPC UA StatusCodes. The descriptions come from the specification; the next steps below are
///     what an integrator would try first for the codes people meet most in the field.
/// </summary>
public static partial class OpcUaStatusCodes
{
    private static readonly Dictionary<string, string> NextSteps = new(StringComparer.Ordinal)
    {
        ["BadUserAccessDenied"] = "The user name or password is wrong, or this user may not do this. Check the credentials and the user's role on the server.",
        ["BadIdentityTokenRejected"] = "The server refused this login. Check the user name and password, or whether the server allows this login type on this endpoint.",
        ["BadIdentityTokenInvalid"] = "The server does not accept this login type here. Try anonymous or user-name login, whichever the endpoint lists.",
        ["BadSecurityChecksFailed"] = "The server did not accept this client's certificate. Trust it on the server (run `proxus-mcp certificate` to see it), then retry.",
        ["BadCertificateUntrusted"] = "The server did not accept this client's certificate. Trust it on the server (run `proxus-mcp certificate` to see it), then retry.",
        ["BadCertificateTimeInvalid"] = "A certificate is expired or not yet valid. Check the clocks on both machines and the certificate dates.",
        ["BadCertificateHostNameInvalid"] = "The certificate does not name the host used to connect. Use the host name in the certificate.",
        ["BadCertificateUriInvalid"] = "The ApplicationUri in a certificate does not match the application. Re-create or re-issue the certificate.",
        ["BadSecurityPolicyRejected"] = "The server does not offer this security policy. Pick one it lists, or enable it on the server.",
        ["BadNodeIdUnknown"] = "No node with this id exists. Use browse to find the right node id; namespace indexes can differ between servers.",
        ["BadNodeIdInvalid"] = "The node id is malformed. Use the form ns=<index>;s=<name> or ns=<index>;i=<number>.",
        ["BadNotReadable"] = "The node exists but its value cannot be read, often because the user lacks read access.",
        ["BadAttributeIdInvalid"] = "The node has no Value attribute: it is probably an Object or a folder. Browse into it and read its Variable children.",
        ["BadTooManySessions"] = "The server has no free sessions. Close other clients or raise the session limit on the server.",
        ["BadSessionIdInvalid"] = "The server forgot the session (restart or timeout). The next call opens a new one.",
        ["BadSessionClosed"] = "The server closed the session. The next call opens a new one.",
        ["BadTimeout"] = "The server did not answer in time. Check the network and server load, or raise timeout_ms.",
        ["BadNoCommunication"] = "The server cannot reach the underlying device. The fault is between the OPC UA server and the machine, not here.",
        ["BadWaitingForInitialData"] = "The server has not received a first value from the device yet. Retry in a moment.",
        ["BadDeviceFailure"] = "The device behind the server reports a failure. Check the machine or PLC.",
        ["BadSensorFailure"] = "The sensor behind this value has failed.",
        ["BadOutOfService"] = "The source of this value is switched off or out of service.",
        ["BadConfigurationError"] = "The server reports a configuration error for this value. Check the tag configuration on the server."
    };

    public static (string Name, string Description, string? NextStep)? Describe(uint status)
    {
        // The low 16 bits carry flags such as overflow and historian bits; the code itself is the high 16 bits.
        if (!Known.TryGetValue(status & 0xFFFF0000, out var known))
            return null;
        return (known.Name, known.Description, NextSteps.GetValueOrDefault(known.Name));
    }

    /// <summary>Adds the StatusCode name, the specification's description and a next step to an OPC UA error.</summary>
    public static DriverError Explain(DriverError error)
    {
        if (error.NativeCode is not { } native || error.Protocol?.Contains("OPC UA", StringComparison.Ordinal) != true)
            return error;
        if (Describe(unchecked((uint)native)) is not { } described || error.NativeMeaning == described.Name)
            return error; // unknown code, or already explained

        return error with
        {
            Message = $"{error.Message} {described.Name}: {described.Description}",
            NativeMeaning = described.Name,
            SuggestedAction = described.NextStep ?? error.SuggestedAction
        };
    }
}

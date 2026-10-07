using Expanse.Domain;

namespace Expanse.Clock.Core;

/// <summary>Classifies a verified Host response for the Manager's retry identity.</summary>
public static class SendOnceRequestLifecycle
{
    public static bool IsTerminal(SubmitCommandResult result) => result.Status switch
    {
        "accepted" or "faulted" or "historical" => true,
        "rejected" => result.ConfirmedTerminal,
        _ => false
    };
}

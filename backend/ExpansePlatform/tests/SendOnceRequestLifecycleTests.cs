using Expanse.Clock.Core;
using Expanse.Domain;

namespace Expanse.Clock.Tests;

public sealed class SendOnceRequestLifecycleTests
{
    [Theory]
    [InlineData("accepted", false, true)]
    [InlineData("rejected", true, true)]
    [InlineData("rejected", false, false)]
    [InlineData("faulted", false, true)]
    [InlineData("historical", false, true)]
    [InlineData("pending", false, false)]
    [InlineData("unknown", false, false)]
    public void OnlyConfirmedTerminalOutcomesReleaseTheRetryIdentity(string status, bool confirmedTerminal, bool terminal)
        => Assert.Equal(terminal, SendOnceRequestLifecycle.IsTerminal(new SubmitCommandResult { Status = status, ConfirmedTerminal = confirmedTerminal }));
}

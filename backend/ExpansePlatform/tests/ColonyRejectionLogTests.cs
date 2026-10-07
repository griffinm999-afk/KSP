using Expanse.Clock.Host;

namespace Expanse.Clock.Tests;

[CollectionDefinition("Diagnostic console", DisableParallelization=true)]
public sealed class DiagnosticConsoleCollection { }

[Collection("Diagnostic console")]
public sealed class ColonyRejectionLogTests
{
    [Fact] public void RepeatedAndDistinctRejectionsHaveBoundedLogOutput()
    {
        var previous=Console.Error;using var captured=new StringWriter();
        try
        {
            Console.SetError(captured);
            for(int i=0;i<40;i++) ColonyRejectionLog.Write("synthetic check " + i);
            for(int i=0;i<40;i++) ColonyRejectionLog.Write("synthetic check " + i);
        }
        finally { Console.SetError(previous); }
        var lines=captured.ToString().Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(8,lines.Length);Assert.Contains("other colony validation failure",lines[^1]);
    }
}

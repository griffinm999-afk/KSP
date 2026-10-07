using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Security.Principal;
using Expanse.Clock.Core;

namespace Expanse.Clock.Host;

internal static class Program
{
    const int MaxViewClients = 16;
    static readonly SemaphoreSlim viewClientSlots = new(MaxViewClients, MaxViewClients);
    static string publisherPipe = ClockProtocol.PublisherPipeName, viewPipe = ClockProtocol.ViewPipeName;
    static string effectsPipe = EffectsProtocol.DefaultPipeName, commandPipe = EffectsProtocol.DefaultCommandPipeName;
    static string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExpanseFoundations", "Recovery");
    static bool publisherPipeExplicit, viewPipeExplicit, effectsPipeExplicit, commandPipeExplicit, dataDirectoryExplicit, enableDevCounter;
    static string? backupAndExit;
    static readonly ConcurrentDictionary<long, Task> activeHandlers = new(); static long handlerId;
    static async Task Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("Missing value for " + args[i]);
            switch (args[i])
            {
                case "--publisher-pipe": publisherPipe = Value(); publisherPipeExplicit = true; break;
                case "--view-pipe": viewPipe = Value(); viewPipeExplicit = true; break;
                case "--effects-pipe": effectsPipe = Value(); effectsPipeExplicit = true; break;
                case "--command-pipe": commandPipe = Value(); commandPipeExplicit = true; break;
                case "--data-dir": dataDirectory = Path.GetFullPath(Value()); dataDirectoryExplicit = true; break;
                case "--backup-and-exit": backupAndExit = Path.GetFullPath(Value()); break;
                case "--enable-dev-counter": enableDevCounter = true; break;
                default: throw new ArgumentException("Unknown Host argument: " + args[i]);
            }
        }
        if (enableDevCounter)
        {
            var effectsPrefix = $"ExpanseFoundations.Effects.dev.{Environment.UserName}.";
            var commandPrefix = $"ExpanseFoundations.Commands.dev.{Environment.UserName}.";
            var publisherPrefix = $"ExpanseFoundations.Clock.Publisher.dev.{Environment.UserName}.";
            var viewPrefix = $"ExpanseFoundations.Clock.View.dev.{Environment.UserName}.";
            if (!publisherPipeExplicit || !viewPipeExplicit || !effectsPipeExplicit || !commandPipeExplicit || !dataDirectoryExplicit || !publisherPipe.StartsWith(publisherPrefix, StringComparison.Ordinal) || !viewPipe.StartsWith(viewPrefix, StringComparison.Ordinal) || !effectsPipe.StartsWith(effectsPrefix, StringComparison.Ordinal) || !commandPipe.StartsWith(commandPrefix, StringComparison.Ordinal))
                throw new ArgumentException("--enable-dev-counter requires explicit per-user dev publisher/view/effect/command pipe names and an explicit --data-dir.");
            if (publisherPipe.Length > 200 || viewPipe.Length > 200 || effectsPipe.Length > 200 || commandPipe.Length > 200 ||
                EffectsProtocol.CreateDevPublisherPipeName(publisherPipe.Substring(publisherPrefix.Length)) != publisherPipe ||
                EffectsProtocol.CreateDevViewPipeName(viewPipe.Substring(viewPrefix.Length)) != viewPipe ||
                EffectsProtocol.CreateDevPipeName(effectsPipe.Substring(effectsPrefix.Length)) != effectsPipe ||
                EffectsProtocol.CreateDevCommandPipeName(commandPipe.Substring(commandPrefix.Length)) != commandPipe)
                throw new ArgumentException("A dev pipe endpoint contains an invalid token.");
        }
        if (backupAndExit is not null && !dataDirectoryExplicit) throw new ArgumentException("--backup-and-exit requires an explicit --data-dir.");
        Console.WriteLine("Expanse Foundations Clock and Recovery Host (Ctrl+C to stop)");
        using var recovery = new RecoveryCoordinator(dataDirectory, enableDevCounter: enableDevCounter);
        if (backupAndExit is not null)
        {
            recovery.BackupDatabase(backupAndExit);
            Console.WriteLine("SQLite online backup created: " + backupAndExit);
            return;
        }
        using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var state = new ClockState();
        var publisher = PublisherLoop(state, stop.Token); var view = ViewLoop(state, stop.Token);
        var effects = new EffectPipeServer(effectsPipe, recovery, state).RunAsync(stop.Token);
        var commands = new CommandPipeServer(commandPipe, recovery).RunAsync(stop.Token);
        Console.WriteLine($"Effect pipe: {effectsPipe}; command pipe: {commandPipe}; database: {dataDirectory}; dev counter: {enableDevCounter}");
        try { await Task.WhenAll(publisher, view, effects, commands); } catch (OperationCanceledException) { }
        var remaining = activeHandlers.Values.ToArray(); if (remaining.Length > 0) try { await Task.WhenAll(remaining); } catch (OperationCanceledException) { }
    }

    static void Track(Task task)
    {
        var id = Interlocked.Increment(ref handlerId); activeHandlers[id] = task;
        _ = task.ContinueWith(_ => activeHandlers.TryRemove(id, out var ignored), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    static NamedPipeServerStream Server(string name, int instances) => new(name, PipeDirection.InOut, instances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    static async Task PublisherLoop(ClockState state, CancellationToken stop)
    {
        var owner = 0;
        while (!stop.IsCancellationRequested)
        {
            var pipe = Server(publisherPipe, NamedPipeServerStream.MaxAllowedServerInstances);
            try { await pipe.WaitForConnectionAsync(stop); } catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            var client = pipe; // ownership transfers to the handler
            if (Interlocked.CompareExchange(ref owner, 1, 0) != 0) { Console.Error.WriteLine("Rejected concurrent publisher connection; one publisher is allowed."); await client.DisposeAsync(); continue; }
            state.SetPublisherConnected(true);
            Track(ServePublisher(client, state, stop, () => { state.SetPublisherConnected(false); Interlocked.Exchange(ref owner, 0); }));
        }
    }

    static async Task ServePublisher(NamedPipeServerStream pipe, ClockState state, CancellationToken stop, Action released)
    {
        await using (pipe)
        try
        {
            Console.WriteLine("Publisher connected.");
            while (pipe.IsConnected && !stop.IsCancellationRequested)
            {
                using var frameTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop); frameTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var bytes = await ClockProtocol.ReadFrameAsync(pipe, frameTimeout.Token);
                var sample = ClockProtocol.DecodeClockSample(bytes, ColonyRejectionLog.Write);
                state.Accept(sample);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        { Console.Error.WriteLine($"Publisher disconnected: {ex.Message}"); }
        finally { released(); }
    }

    static async Task ViewLoop(ClockState state, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await viewClientSlots.WaitAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }

            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = Server(viewPipe, MaxViewClients);
                await pipe.WaitForConnectionAsync(stop);
                var client = pipe; pipe = null; // handler owns the stream and the reserved slot
                Track(ServeView(client, state, stop));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                await DisposeQuietly(pipe);
                viewClientSlots.Release();
                break;
            }
            catch (Exception ex)
            {
                await DisposeQuietly(pipe);
                viewClientSlots.Release();
                if (!stop.IsCancellationRequested) Console.Error.WriteLine($"View listener recovered from pipe error: {ex.Message}");
                if (stop.IsCancellationRequested) break;
                try { await Task.Delay(250, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            }
        }
    }

    static async Task ServeView(NamedPipeServerStream pipe, ClockState state, CancellationToken stop)
    {
        try
        {
            await using (pipe)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var doc = await ClockProtocol.ReadJsonAsync<System.Text.Json.JsonDocument>(pipe, timeout.Token) ?? throw new InvalidDataException("Empty view request.");
                var root = doc.RootElement;
                if (!root.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != 1 || !root.TryGetProperty("messageType", out var type) || type.GetString() != "getSnapshot") throw new InvalidDataException("Unsupported view request.");
                var frame = ClockProtocol.EncodeView(state.Snapshot());
                await pipe.WriteAsync(frame, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            }
        }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Console.Error.WriteLine($"View request rejected: {ex.Message}"); }
        finally { viewClientSlots.Release(); }
    }

    static async ValueTask DisposeQuietly(NamedPipeServerStream? pipe)
    { if (pipe is not null) try { await pipe.DisposeAsync(); } catch { } }
}

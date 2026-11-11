using System;

namespace AnsiDotNet;

/// <summary>Receives input from a backend. Implemented by <see cref="Terminal"/>; calls may come from any thread.</summary>
public interface ITerminalInput
{
    void OnInput(ReadOnlySpan<byte> data);
    void OnResize(TerminalSize size);
    void OnClosed();
}

/// <summary>
/// A byte transport to a terminal: the local console, a socket, an SSH channel, a pipe. Backends push input to
/// an <see cref="ITerminalInput"/> and accept output through <see cref="IOutputSink.Write"/>.
/// </summary>
public interface ITerminalBackend : IOutputSink, IDisposable
{
    TerminalSize Size { get; }

    /// <summary>Begins delivering input. Called once.</summary>
    void Start(ITerminalInput input);

    /// <summary>Temporarily give the terminal back (e.g. to run a child process). Input is not read while suspended.</summary>
    void Suspend() { }

    void Resume() { }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Lokad.Jq;

/// <summary> An exported environment variable for an jq command invocation. </summary>
public readonly record struct JqEnvironmentVariable
{
    private readonly string? _name;
    private readonly string? _value;

    /// <summary>Initializes an exported environment variable.</summary>
    public JqEnvironmentVariable(string name, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        _name = name;
        _value = value;
    }

    /// <summary>Gets the variable name.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Gets the variable value.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Deconstructs the variable into its name and value.</summary>
    public void Deconstruct(out string name, out string value)
    {
        name = Name;
        value = Value;
    }
}

/// <summary>
/// Represents a single jq command invocation with resolved arguments and descriptors.
/// </summary>
public sealed class JqCommandInvocation : IDisposable
{
    private IMemoryScope? _memoryScope;
    private bool _disposed;
    private IReadOnlyList<string> _arguments;
    private IReadOnlyList<JqEnvironmentVariable> _environment;
    private JqPath _currentDirectory;

    internal IMemoryBudget? MemoryBudget { get; private set; }

    /// <summary>Creates a governed invocation snapshot; refusal returns false and null before allocation.</summary>
    /// <remarks>Keep the owned invocation and borrowed budget alive through all command executions; serialize disposal with users.</remarks>
    public static bool TryCreateWithStandardDescriptors(string commandName,
        IReadOnlyList<string> arguments, IReadOnlyList<JqEnvironmentVariable> environment,
        IMemoryBudget memoryBudget, [NotNullWhen(true)] out JqCommandInvocation? invocation) =>
        TryCreateCore(commandName, arguments, environment, null, JqFileDescriptor.StdIn,
            JqFileDescriptor.StdOut, JqFileDescriptor.StdErr, null, memoryBudget, out invocation);

    /// <summary>Creates a governed invocation snapshot with an explicit clock; refusal returns false and null.</summary>
    public static bool TryCreateWithStandardDescriptors(string commandName,
        IReadOnlyList<string> arguments, IReadOnlyList<JqEnvironmentVariable> environment,
        JqClock clock, IMemoryBudget memoryBudget,
        [NotNullWhen(true)] out JqCommandInvocation? invocation)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return TryCreateCore(commandName, arguments, environment, null, JqFileDescriptor.StdIn,
            JqFileDescriptor.StdOut, JqFileDescriptor.StdErr, clock, memoryBudget, out invocation);
    }

    /// <summary>Creates a governed snapshot with explicit directory and descriptors; refusal returns false and null.</summary>
    public static bool TryCreate(string commandName, IReadOnlyList<string> arguments,
        IReadOnlyList<JqEnvironmentVariable> environment, JqPath currentDirectory,
        JqFileDescriptor stdIn, JqFileDescriptor stdOut, JqFileDescriptor stdErr,
        IMemoryBudget memoryBudget, [NotNullWhen(true)] out JqCommandInvocation? invocation)
    {
        ArgumentNullException.ThrowIfNull(currentDirectory);
        return TryCreateCore(commandName, arguments, environment, currentDirectory, stdIn,
            stdOut, stdErr, null, memoryBudget, out invocation);
    }

    /// <summary>Creates a governed snapshot with explicit directory, descriptors and clock; refusal returns false and null.</summary>
    public static bool TryCreate(string commandName, IReadOnlyList<string> arguments,
        IReadOnlyList<JqEnvironmentVariable> environment, JqPath currentDirectory,
        JqFileDescriptor stdIn, JqFileDescriptor stdOut, JqFileDescriptor stdErr,
        JqClock clock, IMemoryBudget memoryBudget,
        [NotNullWhen(true)] out JqCommandInvocation? invocation)
    {
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(clock);
        return TryCreateCore(commandName, arguments, environment, currentDirectory, stdIn,
            stdOut, stdErr, clock, memoryBudget, out invocation);
    }

    private static bool TryCreateCore(string commandName, IReadOnlyList<string> arguments,
        IReadOnlyList<JqEnvironmentVariable> environment, JqPath? currentDirectory,
        JqFileDescriptor stdIn, JqFileDescriptor stdOut, JqFileDescriptor stdErr,
        JqClock? clock, IMemoryBudget memoryBudget,
        [NotNullWhen(true)] out JqCommandInvocation? invocation)
    {
        ArgumentNullException.ThrowIfNull(commandName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(memoryBudget);
        long estimate = 256 + (long)IntPtr.Size * arguments.Count
            + 2L * IntPtr.Size * environment.Count;
        if (currentDirectory is null)
        {
            foreach (JqEnvironmentVariable entry in environment)
            {
                if (entry.Name != "PWD") continue;
                estimate += 128 + 64L * entry.Value.Length;
                break;
            }
        }
        IMemoryScope scope = memoryBudget.CreateScope();
        bool published = false;
        try
        {
            var storage = new JqMemoryReservation(scope);
            storage.Resize(estimate);
            JqPath directory = currentDirectory ?? JqPathResolution.ResolveCurrentDirectory(environment);
            invocation = clock is null
                ? new JqCommandInvocation(commandName, arguments, environment, directory, stdIn, stdOut, stdErr)
                : new JqCommandInvocation(commandName, arguments, environment, directory, stdIn, stdOut, stdErr, clock);
            invocation._memoryScope = scope;
            invocation.MemoryBudget = memoryBudget;
            published = true;
            return true;
        }
        catch (JqQuotaException exception) when (exception.IsMemoryAdmission)
        {
            invocation = null;
            return false;
        }
        finally
        {
            if (!published) scope.Dispose();
        }
    }

    internal void CheckLive() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Releases this snapshot's owned admission; idempotent. Does not dispose the caller's budget or cancel work.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _arguments = Array.Empty<string>();
        _environment = Array.Empty<JqEnvironmentVariable>();
        _currentDirectory = JqPath.Root;
        _memoryScope?.Dispose();
    }

    /// <summary>
    /// Creates an invocation with standard descriptors and a current directory derived from <c>PWD</c>.
    /// </summary>
    public static JqCommandInvocation CreateWithStandardDescriptors(
        string commandName,
        IReadOnlyList<string> arguments,
        IReadOnlyList<JqEnvironmentVariable> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new JqCommandInvocation(
            commandName,
            arguments,
            environment,
            JqPathResolution.ResolveCurrentDirectory(environment),
            JqFileDescriptor.StdIn,
            JqFileDescriptor.StdOut,
            JqFileDescriptor.StdErr);
    }

    /// <summary>
    /// Creates an invocation with standard descriptors, a current directory derived from <c>PWD</c>,
    /// and an explicit clock for time builtins.
    /// </summary>
    public static JqCommandInvocation CreateWithStandardDescriptors(
        string commandName,
        IReadOnlyList<string> arguments,
        IReadOnlyList<JqEnvironmentVariable> environment,
        JqClock clock)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);
        return new JqCommandInvocation(
            commandName,
            arguments,
            environment,
            JqPathResolution.ResolveCurrentDirectory(environment),
            JqFileDescriptor.StdIn,
            JqFileDescriptor.StdOut,
            JqFileDescriptor.StdErr,
            clock);
    }

    /// <summary>Initializes an invocation with an explicit current directory and descriptors.</summary>
    public JqCommandInvocation(
        string commandName,
        IReadOnlyList<string> arguments,
        IReadOnlyList<JqEnvironmentVariable> environment,
        JqPath currentDirectory,
        JqFileDescriptor stdIn,
        JqFileDescriptor stdOut,
        JqFileDescriptor stdErr)
    {
        static ReadOnlyCollection<string> CopyArguments(IReadOnlyList<string> arguments)
        {
            var copy = new string[arguments.Count];
            for (var index = 0; index < copy.Length; index++)
            {
                var argument = arguments[index];
                ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
                copy[index] = argument;
            }

            return Array.AsReadOnly(copy);
        }

        static ReadOnlyCollection<JqEnvironmentVariable> CopyEnvironment(
            IReadOnlyList<JqEnvironmentVariable> environment)
        {
            var copy = new JqEnvironmentVariable[environment.Count];
            for (var index = 0; index < copy.Length; index++)
            {
                var variable = environment[index];
                ArgumentException.ThrowIfNullOrEmpty(variable.Name, nameof(environment));
                copy[index] = variable;
            }

            return Array.AsReadOnly(copy);
        }

        ArgumentNullException.ThrowIfNull(commandName);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(currentDirectory);
        CommandName = commandName;
        _arguments = CopyArguments(arguments);
        _environment = CopyEnvironment(environment);
        _currentDirectory = currentDirectory;
        StdIn = stdIn;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    /// <summary>Gets the executable name supplied by the caller.</summary>
    public string CommandName { get; }

    /// <summary>Gets an immutable snapshot of command arguments, excluding the executable name.</summary>
    public IReadOnlyList<string> Arguments
    {
        get { CheckLive(); return _arguments; }
    }

    /// <summary>Gets an immutable snapshot of exported environment variables.</summary>
    public IReadOnlyList<JqEnvironmentVariable> Environment
    {
        get { CheckLive(); return _environment; }
    }

    /// <summary>Initializes an invocation with an explicit current directory, descriptors, and clock.</summary>
    public JqCommandInvocation(
        string commandName,
        IReadOnlyList<string> arguments,
        IReadOnlyList<JqEnvironmentVariable> environment,
        JqPath currentDirectory,
        JqFileDescriptor stdIn,
        JqFileDescriptor stdOut,
        JqFileDescriptor stdErr,
        JqClock clock)
        : this(commandName, arguments, environment, currentDirectory, stdIn, stdOut, stdErr)
    {
        ArgumentNullException.ThrowIfNull(clock);
        Clock = clock;
    }

    /// <summary>Gets the explicit clock for time builtins, or null when the host supplies none.</summary>
    public JqClock? Clock { get; }

    /// <summary>Gets the canonical absolute current directory for the command.</summary>
    public JqPath CurrentDirectory
    {
        get { CheckLive(); return _currentDirectory; }
    }

    /// <summary>Gets the descriptor used for standard input.</summary>
    public JqFileDescriptor StdIn { get; }

    /// <summary>Gets the descriptor used for standard output.</summary>
    public JqFileDescriptor StdOut { get; }

    /// <summary>Gets the descriptor used for standard error.</summary>
    public JqFileDescriptor StdErr { get; }

}

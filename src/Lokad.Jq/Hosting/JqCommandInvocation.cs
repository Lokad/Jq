using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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
public sealed class JqCommandInvocation
{
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
        Arguments = CopyArguments(arguments);
        Environment = CopyEnvironment(environment);
        CurrentDirectory = currentDirectory;
        StdIn = stdIn;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    /// <summary>Gets the executable name supplied by the caller.</summary>
    public string CommandName { get; }

    /// <summary>Gets an immutable snapshot of command arguments, excluding the executable name.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets an immutable snapshot of exported environment variables.</summary>
    public IReadOnlyList<JqEnvironmentVariable> Environment { get; }

    /// <summary>Gets the canonical absolute current directory for the command.</summary>
    public JqPath CurrentDirectory { get; }

    /// <summary>Gets the descriptor used for standard input.</summary>
    public JqFileDescriptor StdIn { get; }

    /// <summary>Gets the descriptor used for standard output.</summary>
    public JqFileDescriptor StdOut { get; }

    /// <summary>Gets the descriptor used for standard error.</summary>
    public JqFileDescriptor StdErr { get; }

}

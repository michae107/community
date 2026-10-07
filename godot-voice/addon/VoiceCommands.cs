using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TalonVoice;

public enum VoiceArgument { None, Text, Coordinate }

/// <summary>Opt-in commands owned by the current scene. No game-specific names or behavior.</summary>
public sealed class VoiceCommands : IDisposable
{
    private readonly Dictionary<string, Registration> _commands = new(StringComparer.Ordinal);

    public IDisposable Register(Node owner, string name, string phrase, VoiceArgument argument, Func<string, object?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree())
            throw new ArgumentException("Voice commands require an owner in the scene tree.", nameof(owner));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("Use a short, unique command identifier.", nameof(name));
        phrase = string.Join(' ', phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (phrase.Length is 0 or > 64 || phrase.Any(c => !char.IsAsciiLetter(c) && c != ' '))
            throw new ArgumentException("A spoken command phrase must contain words only.", nameof(phrase));
        if (!Enum.IsDefined(argument)) throw new ArgumentOutOfRangeException(nameof(argument));
        if (_commands.ContainsKey(name) || _commands.Values.Any(c => c.Phrase == phrase))
            throw new InvalidOperationException("A voice command with this name or phrase is already registered.");
        var registration = new Registration(this, owner, name, phrase, argument, handler);
        _commands.Add(name, registration);
        owner.TreeExiting += registration.Dispose;
        return registration;
    }

    public object[] Describe(Node? scene) => _commands.Values.Where(c => c.IsAvailable(scene))
        .OrderBy(c => c.Name, StringComparer.Ordinal)
        .Select(c => (object)new { name = c.Name, phrase = c.Phrase, argument = c.Argument.ToString().ToLowerInvariant() })
        .ToArray();

    public object? Invoke(Node? scene, string name, string argument)
    {
        if (!_commands.TryGetValue(name, out Registration? command) || !command.IsAvailable(scene))
            throw new InvalidOperationException("This voice command is not available in the current scene.");
        if (argument.Length > 256 || argument.Any(char.IsControl))
            throw new ArgumentException("The command argument is too long or contains control characters.");
        if (command.Argument == VoiceArgument.None && argument.Length != 0)
            throw new ArgumentException("This command does not take an argument.");
        return command.Handler(argument);
    }

    public void Dispose()
    {
        foreach (Registration registration in _commands.Values.ToArray()) registration.Dispose();
    }

    private sealed class Registration(VoiceCommands registry, Node owner, string name, string phrase,
        VoiceArgument argument, Func<string, object?> handler) : IDisposable
    {
        public string Name { get; } = name;
        public string Phrase { get; } = phrase;
        public VoiceArgument Argument { get; } = argument;
        public Func<string, object?> Handler { get; } = handler;
        private bool _disposed;

        public bool IsAvailable(Node? scene) => !_disposed && scene is not null &&
            GodotObject.IsInstanceValid(owner) && owner.IsInsideTree() && owner.CanProcess() &&
            (scene == owner || scene.IsAncestorOf(owner)) &&
            (owner is not CanvasItem control || control.IsVisibleInTree());

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            registry._commands.Remove(Name);
            if (GodotObject.IsInstanceValid(owner)) owner.TreeExiting -= Dispose;
        }
    }
}

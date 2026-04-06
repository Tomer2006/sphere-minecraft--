using System;
using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public enum RuntimeLogChannel
{
    Session,
    Save,
    World,
    Chunk,
    Player,
    Physics
}

public static class RuntimeLog
{
    private static readonly Dictionary<string, double> LastLogTimesByKey = [];

    public static bool Enabled { get; set; } = true;
    public static bool SessionEnabled { get; set; } = true;
    public static bool SaveEnabled { get; set; } = true;
    public static bool WorldEnabled { get; set; } = true;
    public static bool ChunkEnabled { get; set; } = true;
    public static bool PlayerEnabled { get; set; } = true;
    public static bool PhysicsEnabled { get; set; } = true;

    public static void Info(RuntimeLogChannel channel, string message)
    {
        if (!IsEnabled(channel))
        {
            return;
        }

        GD.Print(FormatPrefix(channel) + message);
    }

    public static void Warning(RuntimeLogChannel channel, string message)
    {
        if (!IsEnabled(channel))
        {
            return;
        }

        GD.PushWarning(FormatPrefix(channel) + message);
    }

    public static void Error(RuntimeLogChannel channel, string message)
    {
        if (!IsEnabled(channel))
        {
            return;
        }

        GD.PushError(FormatPrefix(channel) + message);
    }

    public static void InfoEverySeconds(RuntimeLogChannel channel, string key, double seconds, Func<string> messageFactory)
    {
        if (!IsEnabled(channel) || !ShouldLog(key, seconds))
        {
            return;
        }

        GD.Print(FormatPrefix(channel) + messageFactory());
    }

    public static string FormatVector(Vector3 value)
    {
        return $"({value.X:0.00}, {value.Y:0.00}, {value.Z:0.00})";
    }

    private static bool ShouldLog(string key, double seconds)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        if (LastLogTimesByKey.TryGetValue(key, out double lastLogTime) && now - lastLogTime < seconds)
        {
            return false;
        }

        LastLogTimesByKey[key] = now;
        return true;
    }

    private static bool IsEnabled(RuntimeLogChannel channel)
    {
        if (!Enabled)
        {
            return false;
        }

        return channel switch
        {
            RuntimeLogChannel.Session => SessionEnabled,
            RuntimeLogChannel.Save => SaveEnabled,
            RuntimeLogChannel.World => WorldEnabled,
            RuntimeLogChannel.Chunk => ChunkEnabled,
            RuntimeLogChannel.Player => PlayerEnabled,
            RuntimeLogChannel.Physics => PhysicsEnabled,
            _ => true
        };
    }

    private static string FormatPrefix(RuntimeLogChannel channel)
    {
        return $"[SphereMinecraft][{channel}][frame:{Engine.GetProcessFrames()}][t:{Time.GetTicksMsec()}ms] ";
    }
}

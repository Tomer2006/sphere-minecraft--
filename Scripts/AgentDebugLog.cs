using System;
using System.IO;
using System.Text.Json;
using Godot;

namespace SphereMinecraft;

#region agent log
internal static class AgentDebugLog
{
	internal static void Write(string hypothesisId, string location, string message, object? data = null)
	{
		try
		{
			string path = ProjectSettings.GlobalizePath("res://debug-915ab0.log");
			string line = JsonSerializer.Serialize(new
			{
				sessionId = "915ab0",
				hypothesisId,
				location,
				message,
				data,
				timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
			});
			File.AppendAllText(path, line + "\n");
		}
		catch
		{
			// ignore debug I/O errors
		}
	}
}
#endregion

using Godot;

namespace SphereMinecraft;

public static class PlanetPlayerPersistence
{
	public static PlayerSaveData CreateSaveData(PlanetPlayer player)
	{
		return new PlayerSaveData
		{
			Position = Vector3Save.FromVector3(player.GlobalPosition),
			Velocity = Vector3Save.FromVector3(player.Velocity),
			DesiredForward = Vector3Save.FromVector3(player.DesiredForwardState),
			Pitch = player.PitchDegrees,
			SelectedBlockType = (int)player.SelectedHotbarBlockState,
			SelectedHotbarSlot = player.SelectedHotbarSlotState,
			InventorySlots = player.CreateInventorySlotSaveData()
		};
	}

	public static void ApplySaveData(PlanetPlayer player, PlayerSaveData data)
	{
		player.PrepareForLoadedState();

		player.MoveToPositionForPersistence(data.Position.ToVector3());
		player.Velocity = data.Velocity.ToVector3();
		player.DesiredForwardState = data.DesiredForward.ToVector3();
		player.PitchDegrees = data.Pitch;
		player.ApplyInventorySaveData(data.InventorySlots, data.SelectedHotbarSlot);

		Vector3 upAxis = player.GetUpAxisForPersistence();
		player.SmoothedUpState = upAxis;
		player.NormalizeDesiredForward(upAxis);
		player.ApplyCameraPitch();
		player.AlignToSurfaceImmediately(upAxis);
		player.RefreshStreamingAfterLoad();

		RuntimeLog.Info(RuntimeLogChannel.Player,
			$"Applied player save data. Position={RuntimeLog.FormatVector(player.GlobalPosition)}, Velocity={RuntimeLog.FormatVector(player.Velocity)}, Pitch={player.PitchDegrees:0.00}, SelectedBlock={player.SelectedHotbarBlockState}, SelectedHotbarSlot={player.SelectedHotbarSlotState}");
	}
}

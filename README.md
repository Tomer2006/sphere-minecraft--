# Sphere Minecraft Godot Port

This repository is a manual Godot 4 + C# port of the Unity voxel-planet prototype in `C:\Users\tomer\new game\My project`.

Current status:
- The core world-generation logic has been ported to Godot C#.
- The first-person movement and block break/place loop are playable in Godot.
- The main scene now uses values aligned with the Unity sample scene.

Important limits:
- Unity prefabs and `.unity` scenes are not automatically convertible into native Godot scenes.
- The large Unity prefab in `Assets\prefeds\GameObject.prefab` has not been imported as a real Godot asset.
- This project reproduces behavior in code; it is not a one-click asset migration.

Main files:
- `Scenes/main.tscn`
- `Scripts/PlanetVoxelWorld.cs`
- `Scripts/PlanetPlayer.cs`

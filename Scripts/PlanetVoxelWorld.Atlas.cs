using Godot;

namespace SphereMinecraft;

public partial class PlanetVoxelWorld
{
	private const int AtlasTileSize = 48;
	private const int AtlasGridSize = 2;
	private const int AtlasPaddingPixels = 2;
	private const string BlockTexturesPath = "res://Assets/Textures/Blocks/";

	private Texture2D BuildTextureAtlas()
	{
		atlasUvRects.Clear();
		RuntimeLog.Info(RuntimeLogChannel.World, "Building block texture atlas from res://Assets/Textures/Blocks/.");

		int atlasSize = AtlasTileSize * AtlasGridSize;
		Image image = Image.CreateEmpty(atlasSize, atlasSize, false, Image.Format.Rgba8);
		image.Fill(new Color(0f, 0f, 0f, 0f));

		AddBlockTextureToAtlas(image, VoxelBlockType.Grass, 0, "grass.png", new Color(0.30f, 0.58f, 0.27f), new Color(0.43f, 0.76f, 0.31f));
		AddBlockTextureToAtlas(image, VoxelBlockType.Dirt, 1, "dirt.png", new Color(0.42f, 0.27f, 0.18f), new Color(0.54f, 0.35f, 0.22f));
		AddBlockTextureToAtlas(image, VoxelBlockType.Stone, 2, "stone.png", new Color(0.42f, 0.45f, 0.48f), new Color(0.62f, 0.64f, 0.68f));
		atlasUvRects[VoxelBlockType.Air] = atlasUvRects[VoxelBlockType.Stone];

		return ImageTexture.CreateFromImage(image);
	}

	private void AddBlockTextureToAtlas(
		Image image,
		VoxelBlockType blockType,
		int tileIndex,
		string fileName,
		Color fallbackBase,
		Color fallbackAccent)
	{
		if (TryBlitBlockTextureTile(image, tileIndex, fileName))
		{
			RegisterAtlasTileUvRect(blockType, tileIndex);
			return;
		}

		RuntimeLog.Warning(RuntimeLogChannel.World, $"Block texture missing or unreadable: {BlockTexturesPath}{fileName}. Using procedural tile.");
		PaintAtlasTile(image, blockType, tileIndex, fallbackBase, fallbackAccent);
	}

	private static bool TryBlitBlockTextureTile(Image atlas, int tileIndex, string fileName)
	{
		string path = BlockTexturesPath + fileName;
		if (!ResourceLoader.Exists(path))
		{
			return false;
		}

		if (ResourceLoader.Load(path) is not Texture2D texture)
		{
			return false;
		}

		Image? source = texture.GetImage();
		if (source == null)
		{
			return false;
		}

		source.Convert(Image.Format.Rgba8);
		if (source.GetWidth() != AtlasTileSize || source.GetHeight() != AtlasTileSize)
		{
			source.Resize(AtlasTileSize, AtlasTileSize, Image.Interpolation.Nearest);
		}

		int tileX = tileIndex % AtlasGridSize;
		int tileY = tileIndex / AtlasGridSize;
		int pixelStartX = tileX * AtlasTileSize;
		int pixelStartY = tileY * AtlasTileSize;
		atlas.BlitRect(source, new Rect2I(0, 0, AtlasTileSize, AtlasTileSize), new Vector2I(pixelStartX, pixelStartY));
		return true;
	}

	private void RegisterAtlasTileUvRect(VoxelBlockType blockType, int tileIndex)
	{
		int tileX = tileIndex % AtlasGridSize;
		int tileY = tileIndex / AtlasGridSize;
		float atlasSizeF = AtlasTileSize * AtlasGridSize;
		float tileSpan = AtlasTileSize / atlasSizeF;
		float uvPadding = AtlasPaddingPixels / atlasSizeF;
		atlasUvRects[blockType] = new Rect2(
			new Vector2(tileX * tileSpan + uvPadding, tileY * tileSpan + uvPadding),
			new Vector2(tileSpan - uvPadding * 2f, tileSpan - uvPadding * 2f));
	}

	private void PaintAtlasTile(Image image, VoxelBlockType blockType, int tileIndex, Color baseColor, Color accentColor)
	{
		int tileX = tileIndex % AtlasGridSize;
		int tileY = tileIndex / AtlasGridSize;
		int pixelStartX = tileX * AtlasTileSize;
		int pixelStartY = tileY * AtlasTileSize;

		for (int localY = 0; localY < AtlasTileSize; localY++)
		{
			for (int localX = 0; localX < AtlasTileSize; localX++)
			{
				float stripe = Mathf.Abs(Mathf.Sin((localX + 1) * 0.41f + (localY + 1) * 0.23f + tileIndex * 1.71f));
				float grain = Mathf.Abs(Mathf.Sin((localX + 3) * 1.13f + tileIndex * 0.7f) * Mathf.Cos((localY + 5) * 1.37f));
				float mask = Mathf.Clamp(stripe * 0.65f + grain * 0.35f, 0f, 1f);
				Color color = baseColor.Lerp(accentColor, mask);

				int edgeDistance = Mathf.Min(Mathf.Min(localX, localY), Mathf.Min(AtlasTileSize - 1 - localX, AtlasTileSize - 1 - localY));
				if (edgeDistance < AtlasPaddingPixels)
				{
					color = color.Lerp(baseColor, 0.45f);
				}

				image.SetPixel(pixelStartX + localX, pixelStartY + localY, color);
			}
		}

		RegisterAtlasTileUvRect(blockType, tileIndex);
	}
}

using Godot;

namespace SphereMinecraft;

public partial class SceneLighting : Node3D
{
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("WorldEnvironment");
    [Export] public NodePath SunPath { get; set; } = new("Sun");
    [Export] public Vector3 SunRotationDegrees { get; set; } = new(50f, -30f, 0f);
    [Export] public float SunEnergy { get; set; } = 1.15f;
    [Export] public Color SunColor { get; set; } = new(1.0f, 0.95f, 0.86f);
    [Export] public float SkyExposure { get; set; } = 1.0f;
    [Export] public float FogDensity { get; set; } = 0.0018f;

    public override void _Ready()
    {
        WorldEnvironment? worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        DirectionalLight3D? sun = GetNodeOrNull<DirectionalLight3D>(SunPath);

        if (worldEnvironment is null)
        {
            return;
        }

        PhysicalSkyMaterial skyMaterial = new()
        {
            RayleighCoefficient = 2.0f,
            RayleighColor = new Color(0.38f, 0.55f, 1.0f),
            MieCoefficient = 0.006f,
            MieEccentricity = 0.82f,
            MieColor = new Color(1.0f, 0.93f, 0.84f),
            Turbidity = 3.8f,
            SunDiskScale = 1.0f,
            GroundColor = new Color(0.18f, 0.20f, 0.17f),
            EnergyMultiplier = 1.0f,
            UseDebanding = true
        };

        Sky sky = new()
        {
            SkyMaterial = skyMaterial,
            RadianceSize = Sky.RadianceSizeEnum.Size256
        };

        Environment environment = new()
        {
            BackgroundMode = Environment.BGMode.Sky,
            Sky = sky,
            SkyCustomFov = 50.0f,
            AmbientLightSource = Environment.AmbientSource.Sky,
            AmbientLightSkyContribution = 1.0f,
            ReflectedLightSource = Environment.ReflectionSource.Sky,
            TonemapMode = Environment.ToneMapper.Aces,
            TonemapExposure = SkyExposure,
            TonemapWhite = 6.0f,
            FogEnabled = true,
            FogMode = Environment.FogModeEnum.Depth,
            FogLightColor = new Color(0.78f, 0.84f, 0.95f),
            FogLightEnergy = 0.6f,
            FogSunScatter = 0.25f,
            FogDensity = FogDensity,
            FogAerialPerspective = 0.35f,
            FogDepthBegin = 80.0f,
            FogDepthEnd = 450.0f,
            SdfgiEnabled = false,
            SsaoEnabled = true,
            SsaoRadius = 1.2f,
            SsaoIntensity = 1.1f
        };

        worldEnvironment.Environment = environment;

        if (sun is not null)
        {
            sun.RotationDegrees = SunRotationDegrees;
            sun.LightColor = SunColor;
            sun.LightEnergy = SunEnergy;
            sun.LightIndirectEnergy = 1.0f;
            sun.ShadowEnabled = true;
            sun.ShadowBias = 0.05f;
            sun.ShadowNormalBias = 1.0f;
        }
    }
}

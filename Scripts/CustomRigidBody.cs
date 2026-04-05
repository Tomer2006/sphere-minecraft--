using Godot;

namespace SphereMinecraft;

public partial class CustomRigidBody : CharacterBody3D
{
    private const float RotationSharpness = 18f;
    private const float RotationDeadzoneRadians = 0.0015f;
    private const float GroundSnapDeadzone = 0.01f;

    private float skinWidth = 0.04f;
    private int maxSlideIterations = 4;
    private int maxDepenetrationIterations = 3;
    private float minGroundDot = 0.35f;
    private uint collisionLayer = 2;
    private uint collisionMask = uint.MaxValue;
    private Vector3 lastGroundNormal = Vector3.Up;

    private CollisionShape3D? capsule;
    private CapsuleShape3D? capsuleShape;
    private float groundingSuppressionTimer;

    [ExportGroup("Collision")]
    [Export(PropertyHint.Range, "0.001,0.2,0.001")]
    public float SkinWidth
    {
        get => skinWidth;
        set => skinWidth = value;
    }

    [Export(PropertyHint.Range, "1,16,1")]
    public int MaxSlideIterations
    {
        get => maxSlideIterations;
        set => maxSlideIterations = value;
    }

    [Export(PropertyHint.Range, "1,16,1")]
    public int MaxDepenetrationIterations
    {
        get => maxDepenetrationIterations;
        set => maxDepenetrationIterations = value;
    }

    [Export(PropertyHint.Range, "0,1,0.01")]
    public float MinGroundDot
    {
        get => minGroundDot;
        set => minGroundDot = value;
    }

    [Export]
    public uint CollisionLayerBits
    {
        get => collisionLayer;
        set
        {
            collisionLayer = value;
            CollisionLayer = value;
        }
    }

    [Export]
    public uint CollisionMaskBits
    {
        get => collisionMask;
        set
        {
            collisionMask = value;
            CollisionMask = value;
        }
    }

    public bool IsGrounded { get; private set; }

    public Aabb Bounds => GetBounds();

    public override void _Ready()
    {
        EnsureCapsule();
        CollisionLayer = collisionLayer;
        CollisionMask = collisionMask;
        MotionMode = MotionModeEnum.Floating;
    }

    public void ConfigureCapsule(float radius, float height, Vector3 center)
    {
        EnsureCapsule();

        capsuleShape!.Radius = radius;
        capsuleShape.Height = height;
        capsule!.Position = center;
    }

    public void MoveRotation(Quaternion targetRotation, Vector3 upAxis)
    {
        Quaternion currentRotation = GlobalBasis.GetRotationQuaternion();
        float angleDelta = currentRotation.AngleTo(targetRotation);

        if (angleDelta <= RotationDeadzoneRadians)
        {
            return;
        }

        float deltaTime = Mathf.Max(0.0001f, (float)GetPhysicsProcessDeltaTime());
        float blend = 1f - Mathf.Exp(-RotationSharpness * deltaTime);
        Quaternion nextRotation = currentRotation.Slerp(targetRotation, blend).Normalized();
        GlobalBasis = new Basis(nextRotation).Orthonormalized();
    }

    public void SuppressGrounding(float duration)
    {
        groundingSuppressionTimer = Mathf.Max(groundingSuppressionTimer, duration);
        IsGrounded = false;
    }

    public void BeginPhysicsStep(float deltaTime)
    {
        if (groundingSuppressionTimer <= 0f)
        {
            return;
        }

        groundingSuppressionTimer = Mathf.Max(0f, groundingSuppressionTimer - deltaTime);
    }

    public void RefreshGrounded(Vector3 upAxis, float groundProbeDistance)
    {
        if (IsGroundingSuppressed)
        {
            IsGrounded = false;
            return;
        }

        if (Velocity.Dot(upAxis) > 0.05f)
        {
            IsGrounded = false;
            return;
        }

        IsGrounded = ProbeGround(upAxis, groundProbeDistance);
    }

    public void Simulate(Vector3 upAxis, float deltaTime, float groundProbeDistance)
    {
        MoveWithCollisions(Velocity * deltaTime, upAxis);

        if (IsGroundingSuppressed)
        {
            IsGrounded = false;
            return;
        }

        if (Velocity.Dot(upAxis) > 0.05f)
        {
            IsGrounded = false;
            return;
        }

        IsGrounded = TryProbeGround(upAxis, groundProbeDistance, out float groundGap, out Vector3 groundNormal);

        if (!IsGrounded || Velocity.Dot(upAxis) > 0f)
        {
            return;
        }

        if (groundGap > GroundSnapDeadzone)
        {
            GlobalPosition -= upAxis.Normalized() * Mathf.Min(groundGap, groundProbeDistance);
        }

        Velocity = Velocity.Slide(groundNormal);
    }

    private void MoveWithCollisions(Vector3 motion, Vector3 upAxis)
    {
        Transform3D bodyTransform = GlobalTransform;
        Vector3 remainingMotion = motion;
        Vector3 velocity = Velocity;
        float currentSkinWidth = GetLocalSkinWidth(upAxis);

        for (int iteration = 0; iteration < maxSlideIterations; iteration++)
        {
            float distance = remainingMotion.Length();

            if (distance <= 0.0001f)
            {
                break;
            }

            PhysicsTestMotionResult3D result = new();
            if (!TryBodyMotion(bodyTransform, remainingMotion, currentSkinWidth, false, 1, result))
            {
                bodyTransform.Origin += remainingMotion;
                break;
            }

            bodyTransform.Origin += result.GetTravel();

            Vector3 normal = result.GetCollisionNormal(0);
            Vector3 remainder = result.GetRemainder();
            remainingMotion = remainder.Slide(normal);
            velocity = velocity.Slide(normal);

            if (!IsGroundingSuppressed &&
                normal.Dot(upAxis) >= minGroundDot &&
                Velocity.Dot(upAxis) <= 0f)
            {
                IsGrounded = true;
            }
        }

        GlobalTransform = bodyTransform;
        Velocity = velocity;
    }

    private bool ProbeGround(Vector3 upAxis, float groundProbeDistance)
    {
        return TryProbeGround(upAxis, groundProbeDistance, out _, out _);
    }

    private bool TryProbeGround(Vector3 upAxis, float groundProbeDistance, out float groundGap, out Vector3 groundNormal)
    {
        Vector3 normalizedUp = upAxis.Normalized();
        float currentSkinWidth = GetLocalSkinWidth(normalizedUp);
        float radius = GetScaledRadius();
        Vector3 bottomSphereCenter = GetBottomSphereCenter(GlobalTransform, normalizedUp);
        Vector3 bottomPoint = bottomSphereCenter - normalizedUp * radius;
        float expectedContactDistance = groundProbeDistance + currentSkinWidth;
        Vector3 rayOrigin = bottomPoint + normalizedUp * expectedContactDistance;
        Vector3 rayDelta = -normalizedUp * (groundProbeDistance + currentSkinWidth * 2f);
        BuildGroundProbeAxes(normalizedUp, out Vector3 tangentA, out Vector3 tangentB);

        Vector3[] probeOffsets =
        {
            Vector3.Zero,
            tangentA * (radius * 0.55f),
            -tangentA * (radius * 0.55f),
            tangentB * (radius * 0.55f),
            -tangentB * (radius * 0.55f)
        };

        bool grounded = false;
        groundGap = 0f;
        groundNormal = lastGroundNormal;
        float weightedDistanceSum = 0f;
        float weightSum = 0f;
        Vector3 weightedNormalSum = Vector3.Zero;

        for (int probeIndex = 0; probeIndex < probeOffsets.Length; probeIndex++)
        {
            Vector3 probeOffset = probeOffsets[probeIndex];
            PhysicsRayQueryParameters3D query = new()
            {
                From = rayOrigin + probeOffset,
                To = rayOrigin + probeOffset + rayDelta,
                CollideWithAreas = false,
                HitFromInside = false
            };
            query.Exclude = [GetRid()];

            Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (result.Count == 0)
            {
                continue;
            }

            Vector3 normal = (Vector3)result["normal"];
            if (normal.Dot(upAxis) < minGroundDot)
            {
                continue;
            }

            float distance = ((Vector3)result["position"]).DistanceTo(rayOrigin + probeOffset);
            float centerBias = probeIndex == 0 ? 1.5f : 1f;
            float distanceWeight = 1f / Mathf.Max(0.02f, distance);
            float weight = centerBias * distanceWeight * Mathf.Lerp(0.35f, 1f, normal.Dot(normalizedUp));

            weightedDistanceSum += distance * weight;
            weightedNormalSum += normal * weight;
            weightSum += weight;
            grounded = true;
        }

        if (grounded && weightSum > 0f)
        {
            groundGap = weightedDistanceSum / weightSum - expectedContactDistance;
            groundNormal = weightedNormalSum.Normalized();
            if (groundNormal.LengthSquared() < 0.0001f)
            {
                groundNormal = normalizedUp;
            }

            lastGroundNormal = groundNormal;
        }
        else
        {
            lastGroundNormal = normalizedUp;
        }

        return grounded;
    }

    private void BuildGroundProbeAxes(Vector3 upAxis, out Vector3 tangentA, out Vector3 tangentB)
    {
        tangentA = GlobalBasis.X.Slide(upAxis);
        if (tangentA.LengthSquared() < 0.0001f)
        {
            tangentA = (-GlobalBasis.Z).Slide(upAxis);
        }

        if (tangentA.LengthSquared() < 0.0001f)
        {
            tangentA = upAxis.Cross(Mathf.Abs(upAxis.Dot(Vector3.Right)) < 0.99f ? Vector3.Right : Vector3.Forward);
        }

        tangentA = tangentA.Normalized();
        tangentB = upAxis.Cross(tangentA).Normalized();
    }

    private void ResolvePenetration(Vector3 upAxis)
    {
        float currentSkinWidth = GetLocalSkinWidth(upAxis);

        for (int iteration = 0; iteration < maxDepenetrationIterations; iteration++)
        {
            PhysicsTestMotionResult3D result = new();
            if (!TryBodyMotion(GlobalTransform, Vector3.Zero, currentSkinWidth, true, 4, result))
            {
                break;
            }

            float deepestDepth = 0f;
            Vector3 deepestNormal = Vector3.Zero;
            for (int i = 0; i < result.GetCollisionCount(); i++)
            {
                float depth = result.GetCollisionDepth(i);
                if (depth <= deepestDepth)
                {
                    continue;
                }

                deepestDepth = depth;
                deepestNormal = result.GetCollisionNormal(i);
            }

            if (deepestDepth <= 0.000001f || deepestNormal.LengthSquared() <= 0.000001f)
            {
                break;
            }

            GlobalPosition += deepestNormal.Normalized() * (deepestDepth + currentSkinWidth);
        }
    }

    private bool TryBodyMotion(
        Transform3D from,
        Vector3 motion,
        float margin,
        bool recoveryAsCollision,
        int maxCollisions,
        PhysicsTestMotionResult3D result)
    {
        PhysicsTestMotionParameters3D parameters = new()
        {
            From = from,
            Motion = motion,
            Margin = margin,
            MaxCollisions = maxCollisions,
            RecoveryAsCollision = recoveryAsCollision
        };

        return PhysicsServer3D.BodyTestMotion(GetRid(), parameters, result);
    }

    private void GetCapsulePoints(Vector3 position, Vector3 upAxis, out Vector3 top, out Vector3 bottom)
    {
        Vector3 center = position + GlobalBasis * GetCapsuleCenter();
        float radius = GetScaledRadius();
        float halfHeight = Mathf.Max(GetScaledHeight() * 0.5f - radius, 0f);

        top = center + upAxis * halfHeight;
        bottom = center - upAxis * halfHeight;
    }

    private Vector3 GetBottomSphereCenter(Transform3D bodyTransform, Vector3 upAxis)
    {
        Vector3 center = bodyTransform.Origin + bodyTransform.Basis * GetCapsuleCenter();
        float radius = GetScaledRadius();
        float halfHeight = Mathf.Max(GetScaledHeight() * 0.5f - radius, 0f);
        return center - upAxis * halfHeight;
    }

    private Aabb GetBounds()
    {
        GetCapsulePoints(GlobalPosition, Vector3.Up, out Vector3 top, out Vector3 bottom);
        float radius = GetScaledRadius();
        Vector3 min = new(
            Mathf.Min(top.X, bottom.X) - radius,
            Mathf.Min(top.Y, bottom.Y) - radius,
            Mathf.Min(top.Z, bottom.Z) - radius);
        Vector3 max = new(
            Mathf.Max(top.X, bottom.X) + radius,
            Mathf.Max(top.Y, bottom.Y) + radius,
            Mathf.Max(top.Z, bottom.Z) + radius);
        return new Aabb(min, max - min);
    }

    private float GetScaledRadius()
    {
        Vector3 scale = GlobalTransform.Basis.Scale.Abs();
        return GetCapsuleRadius() * Mathf.Max(scale.X, scale.Z);
    }

    private float GetScaledHeight()
    {
        return GetCapsuleHeight() * Mathf.Abs(GlobalTransform.Basis.Scale.Y);
    }

    private float GetLocalSkinWidth(Vector3 upAxis)
    {
        float radius = GetScaledRadius();
        return Mathf.Clamp(skinWidth, 0.001f, Mathf.Max(0.02f, radius * 0.12f));
    }

    private bool IsGroundingSuppressed => groundingSuppressionTimer > 0f;

    private void EnsureCapsule()
    {
        if (capsule is not null && capsuleShape is not null)
        {
            return;
        }

        capsule = GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
        if (capsule is null)
        {
            capsule = new CollisionShape3D
            {
                Name = "CollisionShape3D"
            };
            AddChild(capsule);
        }

        capsuleShape = capsule.Shape as CapsuleShape3D;
        if (capsuleShape is null)
        {
            capsuleShape = new CapsuleShape3D();
            capsule.Shape = capsuleShape;
        }
    }

    private float GetCapsuleRadius()
    {
        EnsureCapsule();
        return capsuleShape!.Radius;
    }

    private float GetCapsuleHeight()
    {
        EnsureCapsule();
        return capsuleShape!.Height;
    }

    private Vector3 GetCapsuleCenter()
    {
        EnsureCapsule();
        return capsule!.Position;
    }
}

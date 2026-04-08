using System.Collections.Generic;
using System.Text;
using Godot;

namespace SphereMinecraft;

public partial class PlanetPlayer : CharacterBody3D
{
	private const float RotationSharpness = 18f;
	private const float RotationDeadzoneRadians = 0.0015f;
	private const float DefaultSafeMargin = 0.04f;
	private const int HotbarSlotCount = 9;
	private const int InventoryRowCount = 4;
	private const int InventorySlotCount = HotbarSlotCount * InventoryRowCount;
	private const int TotalInventorySlotCount = HotbarSlotCount + InventorySlotCount;
	private const int MaxInventoryStackSize = 256;

	private NodePath worldPath = new("../World");
	private PlanetVoxelWorld? world;
	private Camera3D? playerCamera;

	private float spawnHeightOffset = 3f;
	private float capsuleRadius = 0.45f;
	private float capsuleHeight = 1.85f;
	private Vector3 capsuleCenter = Vector3.Zero;
	private Vector3 cameraPivotLocalPosition = new(0f, 0.65f, 0f);

	private float moveSpeed = 8f;
	private float moveAcceleration = 40f;
	private float airControl = 0.35f;
	private float gravityStrength = 30f;
	private float jumpSpeed = 11f;
	private float groundProbeDistance = 0.08f;
	private float groundMinDot = 0.35f;
	private float coyoteTime = 0.12f;
	private float jumpBufferTime = 0.15f;
	private float jumpGroundingLockTime = 0.18f;

	private float mouseSensitivity = 0.14f;
	private float minPitch = -89f;
	private float maxPitch = 89f;
	private float lookDeadzone = 0.01f;

	private float upSmoothingTime = 0.05f;
	private float interactDistance = 8f;

	private Node3D? cameraPivot;
	private CollisionShape3D? capsule;
	private CapsuleShape3D? capsuleShape;
	private Vector2 moveInput;
	private readonly VoxelBlockType[] inventoryBlockTypes = new VoxelBlockType[TotalInventorySlotCount];
	private readonly int[] inventoryBlockCounts = new int[TotalInventorySlotCount];
	private int selectedHotbarSlot;
	private Vector3 desiredForward = Vector3.Forward;
	private float pitch;
	private float jumpBufferTimer;
	private float jumpGroundingLockTimer;
	private float coyoteTimer;
	private Vector3 smoothedUp = Vector3.Up;

	private PanelContainer? inventoryPanel;
	private GridContainer? inventoryGrid;
	private HBoxContainer? hotbarContainer;
	private PanelContainer? carriedItemPanel;
	private Label? carriedItemLabel;
	private readonly PanelContainer?[] hotbarSlotPanels = new PanelContainer[HotbarSlotCount];
	private readonly Label?[] hotbarSlotLabels = new Label[HotbarSlotCount];
	private readonly PanelContainer?[] inventorySlotPanels = new PanelContainer[InventorySlotCount];
	private readonly Label?[] inventorySlotLabels = new Label[InventorySlotCount];
	private ColorRect? crosshairHorizontal;
	private ColorRect? crosshairVertical;

	private Vector2 lookInput;
	private bool jumpQueued;
	private bool escapePressedThisFrame;
	private bool inventoryTogglePressedThisFrame;
	private bool primaryPointerPressedThisFrame;
	private bool secondaryPointerPressedThisFrame;
	private int pendingHotbarSelection = -1;
	private bool? lastGroundedState;
	private bool gameplayEnabled = true;
	private bool inventoryOpen;
	private VoxelBlockType carriedBlockType = VoxelBlockType.Air;
	private int carriedBlockCount;

	private bool flyMode;
	private bool noClipMode;
	private float flySpeed = 20f;
	private float flyFastMultiplier = 3f;

	private bool chatOpen;
	private LineEdit? chatInput;
	private VBoxContainer? chatMessageContainer;
	private PanelContainer? chatPanel;
	private readonly List<string> chatHistory = [];

	[ExportGroup("References")]
	[Export]
	public NodePath WorldPath
	{
		get => worldPath;
		set => worldPath = value;
	}

	[ExportGroup("Body")]
	[Export]
	public float SpawnHeightOffset
	{
		get => spawnHeightOffset;
		set => spawnHeightOffset = value;
	}

	[Export]
	public float CapsuleRadius
	{
		get => capsuleRadius;
		set => capsuleRadius = value;
	}

	[Export]
	public float CapsuleHeight
	{
		get => capsuleHeight;
		set => capsuleHeight = value;
	}

	[Export]
	public Vector3 CapsuleCenter
	{
		get => capsuleCenter;
		set => capsuleCenter = value;
	}

	[Export]
	public Vector3 CameraPivotLocalPosition
	{
		get => cameraPivotLocalPosition;
		set => cameraPivotLocalPosition = value;
	}

	[ExportGroup("Movement")]
	[Export]
	public float MoveSpeed
	{
		get => moveSpeed;
		set => moveSpeed = value;
	}

	[Export]
	public float MoveAcceleration
	{
		get => moveAcceleration;
		set => moveAcceleration = value;
	}

	[Export]
	public float AirControl
	{
		get => airControl;
		set => airControl = value;
	}

	[Export]
	public float GravityStrength
	{
		get => gravityStrength;
		set => gravityStrength = value;
	}

	[Export]
	public float JumpSpeed
	{
		get => jumpSpeed;
		set => jumpSpeed = value;
	}

	[Export]
	public float GroundProbeDistance
	{
		get => groundProbeDistance;
		set => groundProbeDistance = value;
	}

	[Export]
	public float GroundMinDot
	{
		get => groundMinDot;
		set => groundMinDot = value;
	}

	[Export]
	public float CoyoteTime
	{
		get => coyoteTime;
		set => coyoteTime = value;
	}

	[Export]
	public float JumpBufferTime
	{
		get => jumpBufferTime;
		set => jumpBufferTime = value;
	}

	[Export]
	public float JumpGroundingLockTime
	{
		get => jumpGroundingLockTime;
		set => jumpGroundingLockTime = value;
	}

	[ExportGroup("Look")]
	[Export]
	public float MouseSensitivity
	{
		get => mouseSensitivity;
		set => mouseSensitivity = value;
	}

	[Export]
	public float MinPitchDegrees
	{
		get => minPitch;
		set => minPitch = value;
	}

	[Export]
	public float MaxPitchDegrees
	{
		get => maxPitch;
		set => maxPitch = value;
	}

	[Export]
	public float LookDeadzone
	{
		get => lookDeadzone;
		set => lookDeadzone = value;
	}

	[ExportGroup("Spherical Smoothing")]
	[Export]
	public float UpSmoothingTime
	{
		get => upSmoothingTime;
		set => upSmoothingTime = value;
	}

	[ExportGroup("Interaction")]
	[Export]
	public float InteractDistance
	{
		get => interactDistance;
		set => interactDistance = value;
	}

	public void SetGameplayEnabled(bool enabled)
	{
		gameplayEnabled = enabled;
		if (!enabled)
		{
			SetInventoryOpen(false);
			ClearFrameInput();
		}
	}

	public bool IsInventoryOpen => inventoryOpen;

	public void PlaceOnPlanetSurfaceTop()
	{
		world ??= ResolveWorld();
		if (world == null)
		{
			return;
		}

		PlacePlayerAt(world.PlanetCenter + Vector3.Up * (world.ApproximateSurfaceRadius + spawnHeightOffset));
		Velocity = Vector3.Zero;

		Vector3 upAxis = GetUpAxis();
		smoothedUp = upAxis;
		NormalizeDesiredForward(upAxis);

		AlignToSurface(upAxis, true);
		RuntimeLog.Info(RuntimeLogChannel.Player,
			$"Placed player on top of planet. Position={RuntimeLog.FormatVector(GlobalPosition)}, ApproxSurfaceRadius={world.ApproximateSurfaceRadius:0.00}, UpAxis={RuntimeLog.FormatVector(upAxis)}");
	}

	public override void _Ready()
	{
		InitializeCharacterBody();
		EnsureInputActions();
		ApplyBodySettings();
		AttachOrCreateCamera();
		EnsureHud();
		SetInventoryOpen(false);
		RuntimeLog.Info(RuntimeLogChannel.Player,
			$"PlanetPlayer ready. SpawnHeightOffset={spawnHeightOffset:0.00}, CapsuleRadius={capsuleRadius:0.00}, CapsuleHeight={capsuleHeight:0.00}, MoveSpeed={moveSpeed:0.00}, Gravity={gravityStrength:0.00}");
		CallDeferred(nameof(Start));
	}

	public override void _Process(double delta)
	{
		Update();
		UpdateHud();
		ClearFrameInput();
	}

	public override void _PhysicsProcess(double delta)
	{
		FixedUpdate((float)delta);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (!gameplayEnabled)
		{
			return;
		}

		if (chatOpen)
		{
			if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
			{
				CloseChat();
				GetViewport().SetInputAsHandled();
			}
			return;
		}

		if (@event is InputEventMouseMotion mouseMotion && Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			lookInput += mouseMotion.Relative;
			return;
		}

		if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed)
		{
			if (mouseButton.ButtonIndex == MouseButton.Left)
			{
				primaryPointerPressedThisFrame = true;
			}
			else if (mouseButton.ButtonIndex == MouseButton.Right)
			{
				secondaryPointerPressedThisFrame = true;
			}

			return;
		}

		if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
		{
			return;
		}

		switch (keyEvent.Keycode)
		{
			case Key.Space:
				jumpQueued = true;
				break;
			case Key.Escape:
				escapePressedThisFrame = true;
				break;
		case Key.E:
			inventoryTogglePressedThisFrame = true;
			break;
		case Key.T:
			if (!chatOpen)
			{
				OpenChat();
			}
			break;
		case Key.Slash:
			if (!chatOpen)
			{
				OpenChat("/");
			}
			break;
			case Key.Key1:
				pendingHotbarSelection = 0;
				break;
			case Key.Key2:
				pendingHotbarSelection = 1;
				break;
			case Key.Key3:
				pendingHotbarSelection = 2;
				break;
			case Key.Key4:
				pendingHotbarSelection = 3;
				break;
			case Key.Key5:
				pendingHotbarSelection = 4;
				break;
			case Key.Key6:
				pendingHotbarSelection = 5;
				break;
			case Key.Key7:
				pendingHotbarSelection = 6;
				break;
			case Key.Key8:
				pendingHotbarSelection = 7;
				break;
			case Key.Key9:
				pendingHotbarSelection = 8;
				break;
		}
	}

	private void Start()
	{
		if (world == null)
		{
			world = ResolveWorld();
		}

		if (world == null)
		{
			RuntimeLog.Error(RuntimeLogChannel.Player, "PlanetPlayer could not find a PlanetVoxelWorld.");
			return;
		}

		AttachOrCreateCamera();

		PlacePlayerAt(world.PlanetCenter + Vector3.Up * (world.ApproximateSurfaceRadius + spawnHeightOffset));
		Velocity = Vector3.Zero;

		Vector3 upAxis = GetUpAxis();
		smoothedUp = upAxis;
		desiredForward = Vector3.Forward.Slide(upAxis).Normalized();

		if (desiredForward.LengthSquared() < 0.001f)
		{
			desiredForward = Vector3.Right.Slide(upAxis).Normalized();
		}

		AlignToSurface(upAxis, true);
		world.RefreshStreamingAroundPlayer();
		SetCursorLock(true);
		RuntimeLog.Info(RuntimeLogChannel.Player,
			$"Player start complete. SpawnPosition={RuntimeLog.FormatVector(GlobalPosition)}, ApproxSurfaceRadius={world.ApproximateSurfaceRadius:0.00}, UpAxis={RuntimeLog.FormatVector(upAxis)}");
	}

	internal Vector3 DesiredForwardState
	{
		get => desiredForward;
		set => desiredForward = value;
	}

	internal float PitchDegrees
	{
		get => pitch;
		set => pitch = value;
	}

	internal Vector3 SmoothedUpState
	{
		get => smoothedUp;
		set => smoothedUp = value;
	}

	internal VoxelBlockType SelectedBlockState
	{
		get => SelectedHotbarBlockState;
		set => SelectOrCreateHotbarBlock(value);
	}

	internal VoxelBlockType SelectedHotbarBlockState => GetSelectedHotbarBlockType();

	internal int SelectedHotbarSlotState
	{
		get => selectedHotbarSlot;
		set => SelectHotbarSlot(value);
	}

	internal void PrepareForLoadedState()
	{
		EnsureCapsule();
		world ??= ResolveWorld();
		AttachOrCreateCamera();
	}

	internal Vector3 GetUpAxisForPersistence()
	{
		return GetUpAxis();
	}

	internal void NormalizeDesiredForward(Vector3 upAxis)
	{
		desiredForward = desiredForward.Slide(upAxis).Normalized();

		if (desiredForward.LengthSquared() < 0.001f)
		{
			desiredForward = Vector3.Forward.Slide(upAxis).Normalized();
		}

		if (desiredForward.LengthSquared() < 0.001f)
		{
			desiredForward = Vector3.Right.Slide(upAxis).Normalized();
		}
	}

	internal void ApplyCameraPitch()
	{
		if (cameraPivot != null)
		{
			cameraPivot.RotationDegrees = new Vector3(pitch, 0f, 0f);
		}
	}

	internal void AlignToSurfaceImmediately(Vector3 upAxis)
	{
		AlignToSurface(upAxis, true);
	}

	internal void RefreshStreamingAfterLoad()
	{
		world?.RefreshStreamingAroundPlayer();
	}

	internal void MoveToPositionForPersistence(Vector3 targetPosition)
	{
		PlacePlayerAt(targetPosition);
	}

	internal List<PlayerInventorySlotSave> CreateInventorySlotSaveData()
	{
		List<PlayerInventorySlotSave> slots = new(TotalInventorySlotCount);
		for (int slotIndex = 0; slotIndex < TotalInventorySlotCount; slotIndex++)
		{
			slots.Add(new PlayerInventorySlotSave
			{
				BlockType = (int)inventoryBlockTypes[slotIndex],
				Count = inventoryBlockCounts[slotIndex]
			});
		}

		return slots;
	}

	internal void ApplyInventorySaveData(IReadOnlyList<PlayerInventorySlotSave>? slots, int hotbarSlotIndex)
	{
		ClearInventory();

		if (slots != null)
		{
			int slotCount = Mathf.Min(slots.Count, TotalInventorySlotCount);
			for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
			{
				PlayerInventorySlotSave slot = slots[slotIndex];
				SetInventorySlot(slotIndex, (VoxelBlockType)slot.BlockType, slot.Count);
			}
		}

		SelectHotbarSlot(hotbarSlotIndex);
	}

	private void Update()
	{
		if (!gameplayEnabled || world == null)
		{
			return;
		}

		HandleCursorLock();
		HandleHotbarSelection();
		moveInput = (inventoryOpen || chatOpen) ? Vector2.Zero : ReadMoveInput();

		if (inventoryOpen || chatOpen)
		{
			return;
		}

		if (Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			HandleLook();
			HandleBlockInput();
		}
	}

	private void FixedUpdate(float deltaTime)
	{
		if (!gameplayEnabled || world == null)
		{
			return;
		}

		if (jumpGroundingLockTimer > 0f)
		{
			jumpGroundingLockTimer = Mathf.Max(0f, jumpGroundingLockTimer - deltaTime);
		}

		if (ConsumeJumpPress())
		{
			jumpBufferTimer = Mathf.Max(jumpBufferTimer, jumpBufferTime);
			RuntimeLog.Info(RuntimeLogChannel.Player, $"Jump input buffered for {jumpBufferTime:0.00}s.");
		}

		Vector3 rawUp = GetUpAxis();
		float smoothingTime = Mathf.Max(upSmoothingTime, 0.0001f);
		float t = 1f - Mathf.Pow(0.001f, deltaTime / smoothingTime);
		smoothedUp = smoothedUp.Slerp(rawUp, t).Normalized();

		UpdateCharacterBodyState(smoothedUp);
		AlignToSurface(smoothedUp, false);
		bool wasGrounded = IsPlayerGrounded();

		if (jumpBufferTimer > 0f)
		{
			jumpBufferTimer = Mathf.Max(0f, jumpBufferTimer - deltaTime);
		}

		if (!wasGrounded && coyoteTimer > 0f)
		{
			coyoteTimer = Mathf.Max(0f, coyoteTimer - deltaTime);
		}

		SimulateBody(smoothedUp, deltaTime, wasGrounded);
		bool isGrounded = IsPlayerGrounded();
		if (isGrounded)
		{
			coyoteTimer = coyoteTime;
		}

		if (lastGroundedState != isGrounded)
		{
			RuntimeLog.Info(RuntimeLogChannel.Player,
				$"Grounded state changed to {isGrounded}. Position={RuntimeLog.FormatVector(GlobalPosition)}, Velocity={RuntimeLog.FormatVector(Velocity)}, CoyoteTimer={coyoteTimer:0.000}");
			lastGroundedState = isGrounded;
		}

		RuntimeLog.InfoEverySeconds(RuntimeLogChannel.Player, $"player-state-{GetInstanceId()}", 0.5,
			() => $"Player state snapshot. Position={RuntimeLog.FormatVector(GlobalPosition)}, Velocity={RuntimeLog.FormatVector(Velocity)}, Grounded={isGrounded}, MoveInput={moveInput}, JumpBuffer={jumpBufferTimer:0.000}, Coyote={coyoteTimer:0.000}");
	}

	private void AttachOrCreateCamera()
	{
		if (cameraPivot == null)
		{
			cameraPivot = GetNodeOrNull<Node3D>("Camera Pivot");
		}

		if (cameraPivot == null)
		{
			cameraPivot = new Node3D
			{
				Name = "Camera Pivot"
			};
			AddChild(cameraPivot);
		}

		cameraPivot.Position = cameraPivotLocalPosition;

		if (playerCamera == null)
		{
			playerCamera = cameraPivot.GetNodeOrNull<Camera3D>("Player Camera") ??
						   cameraPivot.GetNodeOrNull<Camera3D>("Camera3D");
		}

		if (playerCamera == null)
		{
			playerCamera = new Camera3D
			{
				Name = "Player Camera",
				Current = true
			};
			cameraPivot.AddChild(playerCamera);
		}

		playerCamera.Position = Vector3.Zero;
		playerCamera.Rotation = Vector3.Zero;
		playerCamera.Current = true;
	}

	private void ApplyBodySettings()
	{
		ConfigureCapsule(capsuleRadius, capsuleHeight, capsuleCenter);
		UpdateCharacterBodyState(smoothedUp);
		RuntimeLog.Info(RuntimeLogChannel.Player,
			$"Applied body settings. CapsuleCenter={RuntimeLog.FormatVector(capsuleCenter)}, GroundMinDot={groundMinDot:0.00}, GroundProbeDistance={groundProbeDistance:0.00}");
	}

	private void InitializeCharacterBody()
	{
		EnsureCapsule();
		MotionMode = MotionModeEnum.Grounded;
		SlideOnCeiling = false;
		SafeMargin = DefaultSafeMargin;
		UpDirection = Vector3.Up;
		UpdateCharacterBodyState(Vector3.Up);
		RuntimeLog.Info(RuntimeLogChannel.Physics,
			$"PlanetPlayer body initialized. CollisionLayer={CollisionLayer}, CollisionMask={CollisionMask}, SafeMargin={SafeMargin:0.000}, FloorSnapLength={FloorSnapLength:0.000}");
	}

	private void UpdateCharacterBodyState(Vector3 upAxis)
	{
		Vector3 normalizedUp = upAxis.LengthSquared() > 0.0001f ? upAxis.Normalized() : Vector3.Up;
		UpDirection = normalizedUp;
		FloorMaxAngle = Mathf.Acos(Mathf.Clamp(groundMinDot, 0f, 1f));
		FloorSnapLength = jumpGroundingLockTimer > 0f ? 0f : Mathf.Max(0f, groundProbeDistance);
	}

	private bool IsPlayerGrounded()
	{
		return jumpGroundingLockTimer <= 0f && IsOnFloor();
	}

	private void PlacePlayerAt(Vector3 targetPosition)
	{
		GlobalPosition = targetPosition;
		jumpGroundingLockTimer = 0f;
		UpdateCharacterBodyState(GetUpAxis());
	}

	private void ConfigureCapsule(float radius, float height, Vector3 center)
	{
		EnsureCapsule();
		capsuleShape!.Radius = radius;
		capsuleShape.Height = height;
		capsule!.Position = center;
	}

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

	private void HandleCursorLock()
	{
		if (inventoryTogglePressedThisFrame)
		{
			SetInventoryOpen(!inventoryOpen);
			primaryPointerPressedThisFrame = false;
			secondaryPointerPressedThisFrame = false;
		}

		if (WasEscapePressedThisFrame())
		{
			RuntimeLog.Info(RuntimeLogChannel.Player, "Unlocking cursor because escape was pressed.");
			SetCursorLock(false);
		}

		if (inventoryOpen)
		{
			return;
		}

		if (WasPrimaryPointerPressedThisFrame() && Input.MouseMode != Input.MouseModeEnum.Captured)
		{
			RuntimeLog.Info(RuntimeLogChannel.Player, "Recapturing cursor because primary mouse button was pressed.");
			SetCursorLock(true);
			primaryPointerPressedThisFrame = false;
		}
	}

	private void HandleLook()
	{
		Vector2 mouseDelta = ReadLookInput() * mouseSensitivity;

		if (mouseDelta.LengthSquared() <= lookDeadzone * lookDeadzone)
		{
			return;
		}

		Vector3 upAxis = smoothedUp;

		Basis yawRotation = new(upAxis, -Mathf.DegToRad(mouseDelta.X));
		Vector3 nextForward = (yawRotation * desiredForward).Slide(upAxis).Normalized();

		if (nextForward.LengthSquared() > 0.001f)
		{
			desiredForward = nextForward;
		}

		pitch = Mathf.Clamp(pitch - mouseDelta.Y, minPitch, maxPitch);

		if (cameraPivot != null)
		{
			cameraPivot.RotationDegrees = new Vector3(pitch, 0f, 0f);
		}
	}

	private void SimulateBody(Vector3 upAxis, float deltaTime, bool wasGrounded)
	{
		if (flyMode)
		{
			SimulateFly(upAxis, deltaTime);
			return;
		}

		Vector3 velocity = Velocity;
		Vector3 groundNormal = wasGrounded ? GetFloorNormal() : upAxis;
		if (groundNormal.LengthSquared() < 0.0001f || groundNormal.Dot(upAxis) < groundMinDot)
		{
			groundNormal = upAxis;
		}

		float verticalSpeed = velocity.Dot(upAxis);
		Vector3 lateralVelocity = velocity.Slide(upAxis).Slide(groundNormal);

		Vector3 forward = desiredForward.Slide(upAxis).Normalized();

		if (forward.LengthSquared() < 0.001f)
		{
			forward = (-GlobalTransform.Basis.Z).Slide(upAxis).Normalized();
		}

		Vector3 right = forward.Cross(upAxis).Normalized();
		Vector3 desiredVelocity = forward * moveInput.Y + right * moveInput.X;

		if (desiredVelocity.LengthSquared() > 1f)
		{
			desiredVelocity = desiredVelocity.Normalized();
		}

		desiredVelocity = desiredVelocity.Slide(groundNormal) * moveSpeed;

		float acceleration = moveAcceleration * (wasGrounded ? 1f : airControl);
		lateralVelocity = lateralVelocity.MoveToward(desiredVelocity, acceleration * deltaTime);

		bool canJump = jumpBufferTimer > 0f && (wasGrounded || coyoteTimer > 0f);

		if (wasGrounded && verticalSpeed < 0f)
		{
			verticalSpeed = 0f;
		}

		if (canJump)
		{
			ExecuteJump(upAxis, ref verticalSpeed);
		}
		else if (!wasGrounded)
		{
			verticalSpeed -= gravityStrength * deltaTime;
		}

		Velocity = lateralVelocity + upAxis * verticalSpeed;
		MoveAndSlide();

		if (IsPlayerGrounded())
		{
			Vector3 resolvedFloorNormal = GetFloorNormal();
			if (resolvedFloorNormal.LengthSquared() > 0.0001f)
			{
				Velocity = Velocity.Slide(resolvedFloorNormal);
			}
		}
	}

	private void SimulateFly(Vector3 upAxis, float deltaTime)
	{
		Vector3 forward = desiredForward.Slide(upAxis).Normalized();
		if (forward.LengthSquared() < 0.001f)
		{
			forward = (-GlobalTransform.Basis.Z).Slide(upAxis).Normalized();
		}

		Vector3 right = forward.Cross(upAxis).Normalized();

		Vector3 cameraForward = cameraPivot != null
			? -cameraPivot.GlobalTransform.Basis.Z
			: forward;

		Vector3 wishDir = cameraForward * moveInput.Y + right * moveInput.X;

		float verticalInput = 0f;
		if (Input.IsActionPressed("jump"))
		{
			verticalInput += 1f;
		}
		if (Input.IsActionPressed("move_backward") && Input.IsKeyPressed(Key.Shift))
		{
			verticalInput -= 1f;
		}

		wishDir += upAxis * verticalInput;

		if (wishDir.LengthSquared() > 1f)
		{
			wishDir = wishDir.Normalized();
		}

		float speed = flySpeed * (Input.IsKeyPressed(Key.Ctrl) ? flyFastMultiplier : 1f);

		if (noClipMode)
		{
			GlobalPosition += wishDir * speed * deltaTime;
			Velocity = Vector3.Zero;
		}
		else
		{
			Velocity = wishDir * speed;
			MoveAndSlide();
		}
	}

	private void AlignToSurface(Vector3 upAxis, bool immediate)
	{
		Vector3 forward = desiredForward.Slide(upAxis).Normalized();

		if (forward.LengthSquared() < 0.001f)
		{
			return;
		}

		Basis targetBasis = new Transform3D(Basis.Identity, GlobalPosition)
			.LookingAt(GlobalPosition + forward, upAxis)
			.Basis;

		if (immediate)
		{
			GlobalBasis = targetBasis;
		}
		else
		{
			MoveRotation(targetBasis.GetRotationQuaternion());
		}
	}

	private void HandleBlockInput()
	{
		if (playerCamera == null)
		{
			return;
		}

		if (WasPrimaryPointerPressedThisFrame())
		{
			TryBreakBlock();
		}

		if (WasSecondaryPointerPressedThisFrame())
		{
			TryPlaceBlock();
		}
	}

	private void HandleHotbarSelection()
	{
		if (pendingHotbarSelection < 0)
		{
			return;
		}

		int previousSlot = selectedHotbarSlot;
		SelectHotbarSlot(pendingHotbarSelection);
		if (previousSlot != selectedHotbarSlot)
		{
			RuntimeLog.Info(RuntimeLogChannel.Player,
				$"Selected hotbar slot changed from {previousSlot + 1} to {selectedHotbarSlot + 1}. Block={GetSelectedHotbarBlockType()}, Count={GetSelectedHotbarCount()}");
		}
	}

	private void TryBreakBlock()
	{
		if (world == null || playerCamera == null)
		{
			return;
		}

		if (!TryRaycastFromCamera(out CollisionObject3D? collider, out int faceIndex, out Vector3 position, out Vector3 normal))
		{
			RuntimeLog.Info(RuntimeLogChannel.Player, "Break block skipped because the camera raycast hit nothing.");
			return;
		}

		if (world.TryGetBreakCell(collider, faceIndex, position, normal, out PlanetCellId targetCell))
		{
			if (!world.TryGetBlockType(targetCell, out VoxelBlockType brokenBlockType))
			{
				RuntimeLog.Info(RuntimeLogChannel.Player, $"Break block skipped because target cell {targetCell} had no collectable block.");
				return;
			}

			if (!TryAddBlockToInventory(brokenBlockType))
			{
				RuntimeLog.Info(RuntimeLogChannel.Player, $"Break block skipped because the inventory is full. Block={brokenBlockType}, Cell={targetCell}");
				return;
			}

			RuntimeLog.Info(RuntimeLogChannel.Player,
				$"Breaking block {brokenBlockType} at {targetCell}. FaceIndex={faceIndex}, HitPosition={RuntimeLog.FormatVector(position)}, HitNormal={RuntimeLog.FormatVector(normal)}");
			world.RemoveBlock(targetCell);
		}
		else
		{
			RuntimeLog.Info(RuntimeLogChannel.Player,
				$"Break block raycast hit collider {collider?.Name ?? "<unknown>"} but no breakable cell was resolved. FaceIndex={faceIndex}");
		}
	}

	private void TryPlaceBlock()
	{
		if (world == null || playerCamera == null)
		{
			return;
		}

		VoxelBlockType selectedBlock = GetSelectedHotbarBlockType();
		if (selectedBlock == VoxelBlockType.Air || GetSelectedHotbarCount() <= 0)
		{
			RuntimeLog.Info(RuntimeLogChannel.Player, $"Place block skipped because hotbar slot {selectedHotbarSlot + 1} is empty.");
			return;
		}

		if (!TryRaycastFromCamera(out CollisionObject3D? collider, out int faceIndex, out Vector3 position, out Vector3 normal))
		{
			RuntimeLog.Info(RuntimeLogChannel.Player, "Place block skipped because the camera raycast hit nothing.");
			return;
		}

		if (!world.TryGetPlaceCell(collider, faceIndex, position, normal, out PlanetCellId targetCell))
		{
			RuntimeLog.Info(RuntimeLogChannel.Player,
				$"Place block raycast hit collider {collider?.Name ?? "<unknown>"} but no placement cell was resolved. FaceIndex={faceIndex}");
			return;
		}

		if (world.HasBlock(targetCell))
		{
			RuntimeLog.Info(RuntimeLogChannel.Player, $"Place block skipped because target cell {targetCell} is already occupied.");
			return;
		}

		if (GetPlayerBounds().Intersects(world.GetCellAabb(targetCell)))
		{
			RuntimeLog.Info(RuntimeLogChannel.Player, $"Place block skipped because target cell {targetCell} intersects the player bounds.");
			return;
		}

		RuntimeLog.Info(RuntimeLogChannel.Player,
			$"Placing block {selectedBlock} at {targetCell}. FaceIndex={faceIndex}, HitPosition={RuntimeLog.FormatVector(position)}, HitNormal={RuntimeLog.FormatVector(normal)}");
		world.PlaceBlock(targetCell, selectedBlock);
		ConsumeSelectedHotbarBlock();
	}

	private bool TryRaycastFromCamera(out CollisionObject3D? collider, out int faceIndex, out Vector3 position, out Vector3 normal)
	{
		collider = null;
		faceIndex = -1;
		position = Vector3.Zero;
		normal = Vector3.Zero;

		if (playerCamera == null)
		{
			return false;
		}

		Vector3 from = playerCamera.GlobalPosition;
		Vector3 to = from + -playerCamera.GlobalTransform.Basis.Z * interactDistance;

		PhysicsRayQueryParameters3D query = new()
		{
			From = from,
			To = to,
			CollideWithAreas = false,
			HitFromInside = false
		};
		query.Exclude = [GetRid()];

		Godot.Collections.Dictionary result = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (result.Count == 0)
		{
			return false;
		}

		collider = result["collider"].AsGodotObject() as CollisionObject3D;
		if (collider == null)
		{
			return false;
		}

		faceIndex = (int)result["face_index"];
		position = (Vector3)result["position"];
		normal = (Vector3)result["normal"];
		return true;
	}

	private Vector2 ReadMoveInput()
	{
		float horizontal = 0f;
		float vertical = 0f;

		if (IsMoveLeftPressed())
		{
			horizontal -= 1f;
		}

		if (IsMoveRightPressed())
		{
			horizontal += 1f;
		}

		if (IsMoveBackwardPressed())
		{
			vertical -= 1f;
		}

		if (IsMoveForwardPressed())
		{
			vertical += 1f;
		}

		return new Vector2(horizontal, vertical);
	}

	private Vector3 GetUpAxis()
	{
		if (world == null)
		{
			return Vector3.Up;
		}

		Vector3 offset = GlobalPosition - world.PlanetCenter;

		if (offset.LengthSquared() < 0.001f)
		{
			return Vector3.Up;
		}

		return offset.Normalized();
	}

	private static void SetCursorLock(bool isLocked)
	{
		Input.MouseMode = isLocked ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
	}

	private void SetInventoryOpen(bool isOpen)
	{
		inventoryOpen = isOpen;
		if (inventoryOpen)
		{
			jumpQueued = false;
			moveInput = Vector2.Zero;
		}
		else
		{
			ReturnCarriedItemToInventory();
		}

		if (inventoryOpen)
		{
			SetCursorLock(false);
		}
		else if (gameplayEnabled)
		{
			SetCursorLock(true);
		}

		UpdateHudVisibility();
	}

	private void ExecuteJump(Vector3 upAxis, ref float verticalSpeed)
	{
		verticalSpeed = jumpSpeed;
		coyoteTimer = 0f;
		jumpBufferTimer = 0f;
		jumpGroundingLockTimer = jumpGroundingLockTime;
		FloorSnapLength = 0f;
		RuntimeLog.Info(RuntimeLogChannel.Player,
			$"Jump executed. VerticalSpeed={verticalSpeed:0.00}, Position={RuntimeLog.FormatVector(GlobalPosition)}, UpAxis={RuntimeLog.FormatVector(upAxis)}");
	}

	private Aabb GetPlayerBounds()
	{
		EnsureCapsule();

		Vector3 upAxis = UpDirection.LengthSquared() > 0.0001f ? UpDirection.Normalized() : GetUpAxis();
		Vector3 center = GlobalTransform.Origin + GlobalBasis * capsule!.Position;
		float radius = GetScaledCapsuleRadius();
		float halfHeight = Mathf.Max(GetScaledCapsuleHeight() * 0.5f - radius, 0f);
		Vector3 top = center + upAxis * halfHeight;
		Vector3 bottom = center - upAxis * halfHeight;
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

	private float GetScaledCapsuleRadius()
	{
		EnsureCapsule();
		Vector3 scale = GlobalTransform.Basis.Scale.Abs();
		return capsuleShape!.Radius * Mathf.Max(scale.X, scale.Z);
	}

	private float GetScaledCapsuleHeight()
	{
		EnsureCapsule();
		return capsuleShape!.Height * Mathf.Abs(GlobalTransform.Basis.Scale.Y);
	}

	private void MoveRotation(Quaternion targetRotation)
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

	private Vector2 ReadLookInput()
	{
		Vector2 value = lookInput;
		lookInput = Vector2.Zero;
		return value;
	}

	private bool WasEscapePressedThisFrame()
	{
		return escapePressedThisFrame || Input.IsActionJustPressed("ui_cancel");
	}

	private bool WasPrimaryPointerPressedThisFrame()
	{
		return primaryPointerPressedThisFrame;
	}

	private bool WasSecondaryPointerPressedThisFrame()
	{
		return secondaryPointerPressedThisFrame;
	}

	private static bool IsMoveForwardPressed()
	{
		return Input.IsActionPressed("move_forward");
	}

	private static bool IsMoveBackwardPressed()
	{
		return Input.IsActionPressed("move_backward");
	}

	private static bool IsMoveLeftPressed()
	{
		return Input.IsActionPressed("move_left");
	}

	private static bool IsMoveRightPressed()
	{
		return Input.IsActionPressed("move_right");
	}

	private PlanetVoxelWorld? ResolveWorld()
	{
		if (!worldPath.IsEmpty)
		{
			return GetNodeOrNull<PlanetVoxelWorld>(worldPath);
		}

		return GetNodeOrNull<PlanetVoxelWorld>("../World");
	}

	private void EnsureHud()
	{
		CanvasLayer hud = GetNodeOrNull<CanvasLayer>("HUD") ?? new CanvasLayer { Name = "HUD" };
		if (hud.GetParent() is null)
		{
			AddChild(hud);
		}

		Control root = hud.GetNodeOrNull<Control>("HudRoot") ?? new Control
		{
			Name = "HudRoot",
			AnchorRight = 1f,
			AnchorBottom = 1f,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};

		if (root.GetParent() is null)
		{
			hud.AddChild(root);
		}
		root.GetNodeOrNull<Control>("HelpLabel")?.QueueFree();
		root.GetNodeOrNull<Control>("SelectedBlockLabel")?.QueueFree();
		root.GetNodeOrNull<Control>("InventoryLabel")?.QueueFree();
		root.GetNodeOrNull<Control>("HotbarLabel")?.QueueFree();

		inventoryPanel = root.GetNodeOrNull<PanelContainer>("InventoryPanel");
		if (inventoryPanel == null)
		{
			inventoryPanel = new PanelContainer
			{
				Name = "InventoryPanel",
				Visible = false,
				CustomMinimumSize = new Vector2(860f, 360f),
				MouseFilter = Control.MouseFilterEnum.Pass
			};
			inventoryPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(new Color(0.05f, 0.07f, 0.09f, 0.94f), new Color(0.42f, 0.48f, 0.54f, 0.95f), 2));
			root.AddChild(inventoryPanel);

			MarginContainer inventoryMargin = new()
			{
				MouseFilter = Control.MouseFilterEnum.Pass
			};
			inventoryMargin.AddThemeConstantOverride("margin_left", 18);
			inventoryMargin.AddThemeConstantOverride("margin_top", 18);
			inventoryMargin.AddThemeConstantOverride("margin_right", 18);
			inventoryMargin.AddThemeConstantOverride("margin_bottom", 18);
			inventoryPanel.AddChild(inventoryMargin);

			VBoxContainer inventoryLayout = new()
			{
				MouseFilter = Control.MouseFilterEnum.Pass
			};
			inventoryLayout.AddThemeConstantOverride("separation", 14);
			inventoryMargin.AddChild(inventoryLayout);

			Label inventoryTitle = new()
			{
				Name = "InventoryTitle",
				Text = "Inventory",
				HorizontalAlignment = HorizontalAlignment.Center,
				MouseFilter = Control.MouseFilterEnum.Pass
			};
			inventoryTitle.AddThemeFontSizeOverride("font_size", 26);
			inventoryLayout.AddChild(inventoryTitle);

			inventoryGrid = new GridContainer
			{
				Name = "InventoryGrid",
				Columns = HotbarSlotCount,
				MouseFilter = Control.MouseFilterEnum.Pass
			};
			inventoryGrid.AddThemeConstantOverride("h_separation", 8);
			inventoryGrid.AddThemeConstantOverride("v_separation", 8);
			inventoryLayout.AddChild(inventoryGrid);
		}
		else
		{
			inventoryGrid = inventoryPanel.GetNodeOrNull<GridContainer>("InventoryGrid");
		}

		hotbarContainer = root.GetNodeOrNull<HBoxContainer>("HotbarContainer");
		if (hotbarContainer == null)
		{
			hotbarContainer = new HBoxContainer
			{
				Name = "HotbarContainer",
				MouseFilter = Control.MouseFilterEnum.Pass
			};
			hotbarContainer.AddThemeConstantOverride("separation", 8);
			root.AddChild(hotbarContainer);
		}

		for (int slotIndex = 0; slotIndex < HotbarSlotCount; slotIndex++)
		{
			if (hotbarSlotPanels[slotIndex] != null)
			{
				continue;
			}

			hotbarSlotPanels[slotIndex] = CreateSlotPanel($"HotbarSlot{slotIndex}", new Vector2(78f, 78f), out Label slotLabel);
			hotbarSlotLabels[slotIndex] = slotLabel;
			int capturedSlotIndex = slotIndex;
			hotbarSlotPanels[slotIndex]!.GuiInput += @event => HandleInventorySlotGuiInput(capturedSlotIndex, true, @event);
			hotbarContainer.AddChild(hotbarSlotPanels[slotIndex]);
		}

		for (int slotIndex = 0; slotIndex < InventorySlotCount; slotIndex++)
		{
			if (inventorySlotPanels[slotIndex] != null)
			{
				continue;
			}

			inventorySlotPanels[slotIndex] = CreateSlotPanel($"InventorySlot{slotIndex}", new Vector2(78f, 78f), out Label slotLabel);
			inventorySlotLabels[slotIndex] = slotLabel;
			int capturedSlotIndex = slotIndex;
			inventorySlotPanels[slotIndex]!.GuiInput += @event => HandleInventorySlotGuiInput(capturedSlotIndex, false, @event);
			inventoryGrid?.AddChild(inventorySlotPanels[slotIndex]);
		}

		carriedItemPanel = root.GetNodeOrNull<PanelContainer>("CarriedItemPanel");
		if (carriedItemPanel == null)
		{
			carriedItemPanel = CreateSlotPanel("CarriedItemPanel", new Vector2(78f, 78f), out Label slotLabel);
			carriedItemLabel = slotLabel;
			carriedItemPanel.ZIndex = 100;
			carriedItemPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
			carriedItemPanel.Visible = false;
			root.AddChild(carriedItemPanel);
		}
		else
		{
			carriedItemLabel ??= carriedItemPanel.FindChild("SlotLabel", true, false) as Label;
		}

		crosshairHorizontal = root.GetNodeOrNull<ColorRect>("CrosshairHorizontal");
		if (crosshairHorizontal == null)
		{
			crosshairHorizontal = new ColorRect
			{
				Name = "CrosshairHorizontal",
				Color = Colors.White,
				Size = new Vector2(16f, 2f),
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			root.AddChild(crosshairHorizontal);
		}

		crosshairVertical = root.GetNodeOrNull<ColorRect>("CrosshairVertical");
		if (crosshairVertical == null)
		{
			crosshairVertical = new ColorRect
			{
				Name = "CrosshairVertical",
				Color = Colors.White,
				Size = new Vector2(2f, 16f),
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			root.AddChild(crosshairVertical);
		}

		BuildChatUi(root);
		UpdateHudVisibility();
	}

	private void BuildChatUi(Control root)
	{
		chatPanel = root.GetNodeOrNull<PanelContainer>("ChatPanel");
		if (chatPanel != null)
		{
			chatInput = chatPanel.FindChild("ChatInput", true, false) as LineEdit;
			chatMessageContainer = chatPanel.FindChild("ChatMessages", true, false) as VBoxContainer;
			return;
		}

		chatPanel = new PanelContainer
		{
			Name = "ChatPanel",
			Visible = false,
			CustomMinimumSize = new Vector2(480f, 220f),
			MouseFilter = Control.MouseFilterEnum.Pass
		};
		chatPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(new Color(0.02f, 0.02f, 0.04f, 0.88f), new Color(0.35f, 0.40f, 0.48f, 0.90f), 2));
		root.AddChild(chatPanel);

		MarginContainer chatMargin = new() { MouseFilter = Control.MouseFilterEnum.Pass };
		chatMargin.AddThemeConstantOverride("margin_left", 12);
		chatMargin.AddThemeConstantOverride("margin_top", 10);
		chatMargin.AddThemeConstantOverride("margin_right", 12);
		chatMargin.AddThemeConstantOverride("margin_bottom", 10);
		chatPanel.AddChild(chatMargin);

		VBoxContainer chatLayout = new() { MouseFilter = Control.MouseFilterEnum.Pass };
		chatLayout.AddThemeConstantOverride("separation", 6);
		chatMargin.AddChild(chatLayout);

		ScrollContainer scroll = new()
		{
			Name = "ChatScroll",
			SizeFlagsVertical = Control.SizeFlags.ExpandFill,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		chatLayout.AddChild(scroll);

		chatMessageContainer = new VBoxContainer
		{
			Name = "ChatMessages",
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		chatMessageContainer.AddThemeConstantOverride("separation", 2);
		scroll.AddChild(chatMessageContainer);

		chatInput = new LineEdit
		{
			Name = "ChatInput",
			PlaceholderText = "Type a command...",
			CustomMinimumSize = new Vector2(0f, 32f),
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		chatInput.AddThemeFontSizeOverride("font_size", 16);
		chatInput.TextSubmitted += OnChatSubmitted;
		chatLayout.AddChild(chatInput);
	}

	private void UpdateHud()
	{
		Vector2 center = GetViewport().GetVisibleRect().Size * 0.5f;
		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		Vector2 mousePosition = GetViewport().GetMousePosition();

		if (inventoryPanel != null)
		{
			inventoryPanel.Position = new Vector2(
				Mathf.Max(24f, center.X - inventoryPanel.CustomMinimumSize.X * 0.5f),
				Mathf.Max(36f, center.Y - inventoryPanel.CustomMinimumSize.Y * 0.5f));
		}

		if (hotbarContainer != null)
		{
			float hotbarWidth = HotbarSlotCount * 78f + (HotbarSlotCount - 1) * 8f;
			hotbarContainer.Position = new Vector2(
				Mathf.Max(16f, center.X - hotbarWidth * 0.5f),
				viewportSize.Y - 102f);
		}

		if (chatPanel != null)
		{
			chatPanel.Position = new Vector2(16f, viewportSize.Y - 220f - 120f);
		}

		UpdateHotbarUi();
		UpdateInventoryUi();
		UpdateCarriedItemUi(mousePosition);
		UpdateHudVisibility();

		if (crosshairHorizontal != null)
		{
			crosshairHorizontal.Visible = !inventoryOpen;
			crosshairHorizontal.Position = center - crosshairHorizontal.Size * 0.5f;
		}

		if (crosshairVertical != null)
		{
			crosshairVertical.Visible = !inventoryOpen;
			crosshairVertical.Position = center - crosshairVertical.Size * 0.5f;
		}
	}

	private void UpdateHudVisibility()
	{
		if (inventoryPanel != null)
		{
			inventoryPanel.Visible = inventoryOpen;
		}

		if (carriedItemPanel != null)
		{
			carriedItemPanel.Visible = inventoryOpen && HasCarriedItem;
		}
	}

	private void UpdateHotbarUi()
	{
		for (int slotIndex = 0; slotIndex < HotbarSlotCount; slotIndex++)
		{
			ApplySlotVisualState(
				hotbarSlotPanels[slotIndex],
				hotbarSlotLabels[slotIndex],
				slotIndex,
				showSlotNumber: false,
				isSelected: slotIndex == selectedHotbarSlot);
		}
	}

	private void UpdateInventoryUi()
	{
		for (int slotIndex = 0; slotIndex < InventorySlotCount; slotIndex++)
		{
			ApplySlotVisualState(
				inventorySlotPanels[slotIndex],
				inventorySlotLabels[slotIndex],
				HotbarSlotCount + slotIndex,
				showSlotNumber: false,
				isSelected: false);
		}
	}

	private void UpdateCarriedItemUi(Vector2 mousePosition)
	{
		if (carriedItemPanel == null || carriedItemLabel == null)
		{
			return;
		}

		bool visible = inventoryOpen && HasCarriedItem;
		carriedItemPanel.Visible = visible;
		if (!visible)
		{
			return;
		}

		carriedItemPanel.Position = mousePosition - carriedItemPanel.CustomMinimumSize * 0.5f;
		carriedItemPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(GetBlockUiColor(carriedBlockType), new Color(0.95f, 0.95f, 0.95f, 0.95f), 2));
		carriedItemLabel.Text = GetBlockShortCode(carriedBlockType) + "\n" + "x" + carriedBlockCount;
		carriedItemLabel.Modulate = Colors.White;
	}

	private void ApplySlotVisualState(PanelContainer? slotPanel, Label? slotLabel, int inventorySlotIndex, bool showSlotNumber, bool isSelected)
	{
		if (slotPanel == null || slotLabel == null)
		{
			return;
		}

		VoxelBlockType blockType = inventoryBlockTypes[inventorySlotIndex];
		int blockCount = inventoryBlockCounts[inventorySlotIndex];
		bool hasItem = blockType != VoxelBlockType.Air && blockCount > 0;

		Color fillColor = hasItem ? GetBlockUiColor(blockType) : new Color(0.11f, 0.13f, 0.16f, 0.94f);
		Color borderColor = isSelected ? new Color(0.98f, 0.86f, 0.32f, 1f) : new Color(0.39f, 0.46f, 0.55f, 0.92f);
		int borderWidth = isSelected ? 4 : 2;

		slotPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(fillColor, borderColor, borderWidth));
		slotLabel.Text = GetVisualSlotText(inventorySlotIndex, showSlotNumber);
		slotLabel.Modulate = hasItem ? Colors.White : new Color(0.74f, 0.79f, 0.85f, 0.78f);
	}

	private void HandleInventorySlotGuiInput(int slotIndex, bool isHotbar, InputEvent @event)
	{
		if (!inventoryOpen || @event is not InputEventMouseButton mouseButton || !mouseButton.Pressed)
		{
			return;
		}

		int inventorySlotIndex = isHotbar ? slotIndex : HotbarSlotCount + slotIndex;
		switch (mouseButton.ButtonIndex)
		{
			case MouseButton.Left:
				HandleInventoryLeftClick(inventorySlotIndex);
				break;
			case MouseButton.Right:
				HandleInventoryRightClick(inventorySlotIndex);
				break;
		}
	}

	private void HandleInventoryLeftClick(int inventorySlotIndex)
	{
		VoxelBlockType slotBlockType = inventoryBlockTypes[inventorySlotIndex];
		int slotBlockCount = inventoryBlockCounts[inventorySlotIndex];

		if (!HasCarriedItem)
		{
			if (slotBlockType == VoxelBlockType.Air || slotBlockCount <= 0)
			{
				return;
			}

			carriedBlockType = slotBlockType;
			carriedBlockCount = slotBlockCount;
			SetInventorySlot(inventorySlotIndex, VoxelBlockType.Air, 0);
			return;
		}

		if (slotBlockType == VoxelBlockType.Air || slotBlockCount <= 0)
		{
			SetInventorySlot(inventorySlotIndex, carriedBlockType, carriedBlockCount);
			ClearCarriedItem();
			return;
		}

		if (slotBlockType == carriedBlockType && slotBlockCount < MaxInventoryStackSize)
		{
			int transferCount = Mathf.Min(MaxInventoryStackSize - slotBlockCount, carriedBlockCount);
			SetInventorySlot(inventorySlotIndex, slotBlockType, slotBlockCount + transferCount);
			carriedBlockCount -= transferCount;
			if (carriedBlockCount <= 0)
			{
				ClearCarriedItem();
			}

			return;
		}

		SwapCarriedItemWithSlot(inventorySlotIndex);
	}

	private void HandleInventoryRightClick(int inventorySlotIndex)
	{
		VoxelBlockType slotBlockType = inventoryBlockTypes[inventorySlotIndex];
		int slotBlockCount = inventoryBlockCounts[inventorySlotIndex];

		if (!HasCarriedItem)
		{
			if (slotBlockType == VoxelBlockType.Air || slotBlockCount <= 0)
			{
				return;
			}

			int pickupCount = Mathf.CeilToInt(slotBlockCount * 0.5f);
			carriedBlockType = slotBlockType;
			carriedBlockCount = pickupCount;
			SetInventorySlot(inventorySlotIndex, slotBlockType, slotBlockCount - pickupCount);
			return;
		}

		if (slotBlockType != VoxelBlockType.Air && slotBlockType != carriedBlockType)
		{
			return;
		}

		if (slotBlockType == carriedBlockType && slotBlockCount >= MaxInventoryStackSize)
		{
			return;
		}

		VoxelBlockType placedType = slotBlockType == VoxelBlockType.Air ? carriedBlockType : slotBlockType;
		SetInventorySlot(inventorySlotIndex, placedType, slotBlockCount + 1);
		carriedBlockCount--;
		if (carriedBlockCount <= 0)
		{
			ClearCarriedItem();
		}
	}

	private void SwapCarriedItemWithSlot(int inventorySlotIndex)
	{
		VoxelBlockType slotBlockType = inventoryBlockTypes[inventorySlotIndex];
		int slotBlockCount = inventoryBlockCounts[inventorySlotIndex];
		SetInventorySlot(inventorySlotIndex, carriedBlockType, carriedBlockCount);
		carriedBlockType = slotBlockType;
		carriedBlockCount = slotBlockCount;
	}

	private void ClearCarriedItem()
	{
		carriedBlockType = VoxelBlockType.Air;
		carriedBlockCount = 0;
	}

	private void ReturnCarriedItemToInventory()
	{
		if (!HasCarriedItem)
		{
			return;
		}

		int remainingCount = carriedBlockCount;
		while (remainingCount > 0)
		{
			if (!TryAddBlockToInventory(carriedBlockType))
			{
				break;
			}

			remainingCount--;
		}

		if (remainingCount <= 0)
		{
			ClearCarriedItem();
		}
		else
		{
			carriedBlockCount = remainingCount;
		}
	}

	private string GetVisualSlotText(int inventorySlotIndex, bool showSlotNumber)
	{
		VoxelBlockType blockType = inventoryBlockTypes[inventorySlotIndex];
		int blockCount = inventoryBlockCounts[inventorySlotIndex];
		string topLine = showSlotNumber ? (inventorySlotIndex + 1).ToString() : string.Empty;
		string itemLine = blockType == VoxelBlockType.Air || blockCount <= 0
			? (showSlotNumber ? "-" : string.Empty)
			: GetBlockShortCode(blockType);
		string countLine = blockType == VoxelBlockType.Air || blockCount <= 0
			? string.Empty
			: "x" + blockCount;

		StringBuilder builder = new();
		if (!string.IsNullOrEmpty(topLine))
		{
			builder.Append(topLine);
			builder.Append('\n');
		}

		builder.Append(itemLine);
		if (!string.IsNullOrEmpty(countLine))
		{
			builder.Append('\n');
			builder.Append(countLine);
		}

		return builder.ToString().TrimEnd();
	}

	private PanelContainer CreateSlotPanel(string name, Vector2 size, out Label slotLabel)
	{
		PanelContainer slotPanel = new()
		{
			Name = name,
			CustomMinimumSize = size,
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		slotPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(new Color(0.11f, 0.13f, 0.16f, 0.94f), new Color(0.39f, 0.46f, 0.55f, 0.92f), 2));

		MarginContainer margin = new()
		{
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		margin.AddThemeConstantOverride("margin_left", 4);
		margin.AddThemeConstantOverride("margin_top", 4);
		margin.AddThemeConstantOverride("margin_right", 4);
		margin.AddThemeConstantOverride("margin_bottom", 4);
		slotPanel.AddChild(margin);

		slotLabel = new Label
		{
			Name = "SlotLabel",
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		slotLabel.AddThemeFontSizeOverride("font_size", 16);
		margin.AddChild(slotLabel);
		return slotPanel;
	}

	private static StyleBoxFlat CreatePanelStyle(Color backgroundColor, Color borderColor, int borderWidth)
	{
		return new StyleBoxFlat
		{
			BgColor = backgroundColor,
			BorderColor = borderColor,
			BorderWidthBottom = borderWidth,
			BorderWidthTop = borderWidth,
			BorderWidthLeft = borderWidth,
			BorderWidthRight = borderWidth,
			CornerRadiusBottomLeft = 8,
			CornerRadiusBottomRight = 8,
			CornerRadiusTopLeft = 8,
			CornerRadiusTopRight = 8
		};
	}

	private static Color GetBlockUiColor(VoxelBlockType blockType)
	{
		return blockType switch
		{
			VoxelBlockType.Grass => new Color(0.23f, 0.43f, 0.20f, 0.96f),
			VoxelBlockType.Dirt => new Color(0.36f, 0.23f, 0.15f, 0.96f),
			VoxelBlockType.Stone => new Color(0.33f, 0.36f, 0.40f, 0.96f),
			_ => new Color(0.11f, 0.13f, 0.16f, 0.94f)
		};
	}

	private void OpenChat(string prefill = "")
	{
		if (chatOpen || inventoryOpen)
		{
			return;
		}

		chatOpen = true;
		if (chatPanel != null)
		{
			chatPanel.Visible = true;
		}

		if (chatInput != null)
		{
			chatInput.Text = prefill;
			chatInput.GrabFocus();
			chatInput.CaretColumn = prefill.Length;
		}

		SetCursorLock(false);
	}

	private void CloseChat()
	{
		chatOpen = false;
		if (chatPanel != null)
		{
			chatPanel.Visible = false;
		}

		if (chatInput != null)
		{
			chatInput.ReleaseFocus();
			chatInput.Text = string.Empty;
		}

		if (gameplayEnabled)
		{
			SetCursorLock(true);
		}
	}

	private void OnChatSubmitted(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			CloseChat();
			return;
		}

		AddChatMessage($"> {text}");

		if (text.StartsWith('/'))
		{
			ExecuteChatCommand(text.TrimStart('/').Trim().ToLowerInvariant());
		}
		else
		{
			AddChatMessage("Unknown input. Use /fly or /noclip.");
		}

		CloseChat();
	}

	private void ExecuteChatCommand(string command)
	{
		switch (command)
		{
			case "fly":
				flyMode = !flyMode;
				if (!flyMode)
				{
					noClipMode = false;
					SetCollisionEnabled(true);
				}
				AddChatMessage(flyMode ? "Fly mode enabled." : "Fly mode disabled.");
				RuntimeLog.Info(RuntimeLogChannel.Player, $"Fly mode toggled to {flyMode}.");
				break;
			case "noclip":
				noClipMode = !noClipMode;
				if (noClipMode)
				{
					flyMode = true;
				}
				SetCollisionEnabled(!noClipMode);
				AddChatMessage(noClipMode ? "Noclip enabled. (fly + no collision)" : "Noclip disabled.");
				RuntimeLog.Info(RuntimeLogChannel.Player, $"Noclip mode toggled to {noClipMode}.");
				break;
			default:
				AddChatMessage($"Unknown command: /{command}");
				AddChatMessage("Available: /fly, /noclip");
				break;
		}
	}

	private void SetCollisionEnabled(bool enabled)
	{
		EnsureCapsule();
		if (capsule != null)
		{
			capsule.Disabled = !enabled;
		}
	}

	private void AddChatMessage(string message)
	{
		chatHistory.Add(message);
		if (chatMessageContainer == null)
		{
			return;
		}

		Label label = new()
		{
			Text = message,
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		label.AddThemeFontSizeOverride("font_size", 14);
		label.AddThemeColorOverride("font_color", new Color(0.85f, 0.90f, 0.95f));
		chatMessageContainer.AddChild(label);

		while (chatMessageContainer.GetChildCount() > 50)
		{
			chatMessageContainer.GetChild(0).QueueFree();
		}
	}

	private void ClearFrameInput()
	{
		escapePressedThisFrame = false;
		inventoryTogglePressedThisFrame = false;
		primaryPointerPressedThisFrame = false;
		secondaryPointerPressedThisFrame = false;
		pendingHotbarSelection = -1;
	}

	private bool ConsumeJumpPress()
	{
		bool pressed = jumpQueued || Input.IsActionJustPressed("jump");
		jumpQueued = false;
		return pressed;
	}

	private static void EnsureInputActions()
	{
		EnsureAction("move_forward", Key.W, Key.Up);
		EnsureAction("move_backward", Key.S, Key.Down);
		EnsureAction("move_left", Key.A, Key.Left);
		EnsureAction("move_right", Key.D, Key.Right);
		EnsureAction("jump", Key.Space);
		EnsureAction("ui_cancel", Key.Escape);
	}

	private static void EnsureAction(string actionName, params Key[] keys)
	{
		if (!InputMap.HasAction(actionName))
		{
			InputMap.AddAction(actionName);
		}

		foreach (Key key in keys)
		{
			InputEventKey keyEvent = new()
			{
				PhysicalKeycode = key
			};

			if (!InputMap.ActionHasEvent(actionName, keyEvent))
			{
				InputMap.ActionAddEvent(actionName, keyEvent);
			}
		}
	}

	private void ClearInventory()
	{
		for (int slotIndex = 0; slotIndex < TotalInventorySlotCount; slotIndex++)
		{
			inventoryBlockTypes[slotIndex] = VoxelBlockType.Air;
			inventoryBlockCounts[slotIndex] = 0;
		}
	}

	private void SetInventorySlot(int slotIndex, VoxelBlockType blockType, int count)
	{
		if (slotIndex < 0 || slotIndex >= TotalInventorySlotCount)
		{
			return;
		}

		if (blockType == VoxelBlockType.Air || count <= 0)
		{
			inventoryBlockTypes[slotIndex] = VoxelBlockType.Air;
			inventoryBlockCounts[slotIndex] = 0;
			return;
		}

		inventoryBlockTypes[slotIndex] = blockType;
		inventoryBlockCounts[slotIndex] = Mathf.Clamp(count, 1, MaxInventoryStackSize);
	}

	private void SelectHotbarSlot(int slotIndex)
	{
		selectedHotbarSlot = Mathf.Clamp(slotIndex, 0, HotbarSlotCount - 1);
	}

	private void SelectOrCreateHotbarBlock(VoxelBlockType blockType)
	{
		int matchingHotbarSlot = FindMatchingSlot(blockType, onlyHotbar: true);
		if (matchingHotbarSlot >= 0)
		{
			SelectHotbarSlot(matchingHotbarSlot);
			return;
		}

		SetInventorySlot(selectedHotbarSlot, blockType, blockType == VoxelBlockType.Air ? 0 : 1);
	}

	private VoxelBlockType GetSelectedHotbarBlockType()
	{
		return inventoryBlockCounts[selectedHotbarSlot] > 0 ? inventoryBlockTypes[selectedHotbarSlot] : VoxelBlockType.Air;
	}

	private int GetSelectedHotbarCount()
	{
		return inventoryBlockCounts[selectedHotbarSlot];
	}

	private bool TryAddBlockToInventory(VoxelBlockType blockType)
	{
		if (blockType == VoxelBlockType.Air)
		{
			return false;
		}

		int targetSlot = FindSlotWithSpace(blockType);
		if (targetSlot < 0)
		{
			targetSlot = FindFirstEmptySlot();
		}

		if (targetSlot < 0)
		{
			return false;
		}

		if (inventoryBlockTypes[targetSlot] == VoxelBlockType.Air)
		{
			inventoryBlockTypes[targetSlot] = blockType;
		}

		inventoryBlockCounts[targetSlot] = Mathf.Min(MaxInventoryStackSize, inventoryBlockCounts[targetSlot] + 1);

		if (GetSelectedHotbarBlockType() == VoxelBlockType.Air && targetSlot < HotbarSlotCount)
		{
			SelectHotbarSlot(targetSlot);
		}

		return true;
	}

	private void ConsumeSelectedHotbarBlock()
	{
		if (inventoryBlockCounts[selectedHotbarSlot] <= 0)
		{
			return;
		}

		inventoryBlockCounts[selectedHotbarSlot]--;
		if (inventoryBlockCounts[selectedHotbarSlot] <= 0)
		{
			inventoryBlockCounts[selectedHotbarSlot] = 0;
			inventoryBlockTypes[selectedHotbarSlot] = VoxelBlockType.Air;
		}
	}

	private int FindSlotWithSpace(VoxelBlockType blockType)
	{
		for (int slotIndex = 0; slotIndex < TotalInventorySlotCount; slotIndex++)
		{
			if (inventoryBlockTypes[slotIndex] == blockType && inventoryBlockCounts[slotIndex] > 0 && inventoryBlockCounts[slotIndex] < MaxInventoryStackSize)
			{
				return slotIndex;
			}
		}

		return -1;
	}

	private int FindFirstEmptySlot()
	{
		for (int slotIndex = 0; slotIndex < TotalInventorySlotCount; slotIndex++)
		{
			if (inventoryBlockTypes[slotIndex] == VoxelBlockType.Air || inventoryBlockCounts[slotIndex] <= 0)
			{
				return slotIndex;
			}
		}

		return -1;
	}

	private int FindMatchingSlot(VoxelBlockType blockType, bool onlyHotbar)
	{
		int endSlot = onlyHotbar ? HotbarSlotCount : TotalInventorySlotCount;
		for (int slotIndex = 0; slotIndex < endSlot; slotIndex++)
		{
			if (inventoryBlockTypes[slotIndex] == blockType && inventoryBlockCounts[slotIndex] > 0)
			{
				return slotIndex;
			}
		}

		return -1;
	}

	private static string GetBlockShortCode(VoxelBlockType blockType)
	{
		return blockType switch
		{
			VoxelBlockType.Grass => "G",
			VoxelBlockType.Dirt => "D",
			VoxelBlockType.Stone => "S",
			_ => "-"
		};
	}

	private bool HasCarriedItem => carriedBlockType != VoxelBlockType.Air && carriedBlockCount > 0;
}

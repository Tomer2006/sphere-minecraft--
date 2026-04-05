using Godot;

namespace SphereMinecraft;

public partial class PlanetPlayer : CustomRigidBody
{
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
	private float groundedStickSpeed = 0.2f;
	private float groundProbeDistance = 0.08f;
	private float groundMinDot = 0.35f;
	private float coyoteTime = 0.12f;
	private float jumpBufferTime = 0.15f;
	private float jumpGroundingLockTime = 0.18f;
	private float jumpTakeoffDistance = 0.16f;

	private float mouseSensitivity = 0.14f;
	private float minPitch = -89f;
	private float maxPitch = 89f;
	private float lookDeadzone = 0.01f;

	private float upSmoothingTime = 0.05f;
	private float interactDistance = 8f;
	private VoxelBlockType selectedBlock = VoxelBlockType.Grass;

	private CustomRigidBody? body;
	private Node3D? cameraPivot;
	private Vector2 moveInput;
	private Vector3 desiredForward = Vector3.Forward;
	private float pitch;
	private float jumpBufferTimer;
	private float coyoteTimer;
	private Vector3 smoothedUp = Vector3.Up;

	private Label? selectedBlockLabel;
	private ColorRect? crosshairHorizontal;
	private ColorRect? crosshairVertical;

	private Vector2 lookInput;
	private bool jumpPressedThisFrame;
	private bool escapePressedThisFrame;
	private bool primaryPointerPressedThisFrame;
	private bool secondaryPointerPressedThisFrame;
	private bool digit1PressedThisFrame;
	private bool digit2PressedThisFrame;
	private bool digit3PressedThisFrame;

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
	public float GroundedStickSpeed
	{
		get => groundedStickSpeed;
		set => groundedStickSpeed = value;
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

	[Export]
	public float JumpTakeoffDistance
	{
		get => jumpTakeoffDistance;
		set => jumpTakeoffDistance = value;
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

	[Export]
	public VoxelBlockType SelectedBlockType
	{
		get => selectedBlock;
		set => selectedBlock = value;
	}

	public VoxelBlockType SelectedBlock => selectedBlock;

	public PlayerSaveData CreateSaveData()
	{
		return new PlayerSaveData
		{
			Position = Vector3Save.FromVector3(GlobalPosition),
			Velocity = Vector3Save.FromVector3(Velocity),
			DesiredForward = Vector3Save.FromVector3(desiredForward),
			Pitch = pitch,
			SelectedBlockType = (int)selectedBlock
		};
	}

	public void ApplySaveData(PlayerSaveData data)
	{
		body = this;

		if (world == null)
		{
			world = ResolveWorld();
		}

		AttachOrCreateCamera();

		GlobalPosition = data.Position.ToVector3();
		Velocity = data.Velocity.ToVector3();
		desiredForward = data.DesiredForward.ToVector3();
		pitch = data.Pitch;
		selectedBlock = (VoxelBlockType)data.SelectedBlockType;

		Vector3 upAxis = GetUpAxis();
		smoothedUp = upAxis;
		desiredForward = desiredForward.Slide(upAxis).Normalized();

		if (desiredForward.LengthSquared() < 0.001f)
		{
			desiredForward = Vector3.Forward.Slide(upAxis).Normalized();
		}

		if (desiredForward.LengthSquared() < 0.001f)
		{
			desiredForward = Vector3.Right.Slide(upAxis).Normalized();
		}

		if (cameraPivot != null)
		{
			cameraPivot.RotationDegrees = new Vector3(pitch, 0f, 0f);
		}

		AlignToSurface(upAxis, true);
	}

	public override void _Ready()
	{
		EnsureInputActions();
		body = this;
		ApplyBodySettings();
		AttachOrCreateCamera();
		EnsureHud();
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
				jumpPressedThisFrame = true;
				break;
			case Key.Escape:
				escapePressedThisFrame = true;
				break;
			case Key.Key1:
				digit1PressedThisFrame = true;
				break;
			case Key.Key2:
				digit2PressedThisFrame = true;
				break;
			case Key.Key3:
				digit3PressedThisFrame = true;
				break;
		}
	}

	private void Start()
	{
		body = this;

		if (world == null)
		{
			world = ResolveWorld();
		}

		if (world == null)
		{
			GD.PushError("PlanetPlayer could not find a PlanetVoxelWorld.");
			return;
		}

		AttachOrCreateCamera();

		GlobalPosition = world.PlanetCenter + Vector3.Up * (world.ApproximateSurfaceRadius + spawnHeightOffset);
		Velocity = Vector3.Zero;

		Vector3 upAxis = GetUpAxis();
		smoothedUp = upAxis;
		desiredForward = Vector3.Forward.Slide(upAxis).Normalized();

		if (desiredForward.LengthSquared() < 0.001f)
		{
			desiredForward = Vector3.Right.Slide(upAxis).Normalized();
		}

		AlignToSurface(upAxis, true);
		SetCursorLock(true);
	}

	private void Update()
	{
		if (world == null || body == null)
		{
			return;
		}

		moveInput = ReadMoveInput();
		HandleCursorLock();

		if (Input.MouseMode == Input.MouseModeEnum.Captured)
		{
			HandleLook();
			HandleBlockInput();
		}

		if (WasJumpPressedThisFrame())
		{
			jumpBufferTimer = jumpBufferTime;
		}
	}

	private void FixedUpdate(float deltaTime)
	{
		if (world == null || body == null)
		{
			return;
		}

		body.BeginPhysicsStep(deltaTime);

		Vector3 rawUp = GetUpAxis();
		float smoothingTime = Mathf.Max(upSmoothingTime, 0.0001f);
		float t = 1f - Mathf.Pow(0.001f, deltaTime / smoothingTime);
		smoothedUp = smoothedUp.Slerp(rawUp, t).Normalized();

		AlignToSurface(smoothedUp, false);
		body.RefreshGrounded(smoothedUp, groundProbeDistance);

		if (jumpBufferTimer > 0f)
		{
			jumpBufferTimer = Mathf.Max(0f, jumpBufferTimer - deltaTime);
		}

		if (body.IsGrounded)
		{
			coyoteTimer = coyoteTime;
		}
		else if (coyoteTimer > 0f)
		{
			coyoteTimer = Mathf.Max(0f, coyoteTimer - deltaTime);
		}

		SimulateBody(smoothedUp, deltaTime);
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
		if (body == null)
		{
			return;
		}

		body.ConfigureCapsule(capsuleRadius, capsuleHeight, capsuleCenter);
		body.MinGroundDot = groundMinDot;
	}

	private void HandleCursorLock()
	{
		if (WasEscapePressedThisFrame())
		{
			SetCursorLock(false);
		}

		if (WasPrimaryPointerPressedThisFrame() && Input.MouseMode != Input.MouseModeEnum.Captured)
		{
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

	private void SimulateBody(Vector3 upAxis, float deltaTime)
	{
		if (body == null)
		{
			return;
		}

		Vector3 velocity = body.Velocity;
		float verticalSpeed = velocity.Dot(upAxis);
		Vector3 lateralVelocity = velocity.Slide(upAxis);

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

		desiredVelocity *= moveSpeed;

		float acceleration = moveAcceleration * (body.IsGrounded ? 1f : airControl);
		lateralVelocity = lateralVelocity.MoveToward(desiredVelocity, acceleration * deltaTime);

		bool canJump = jumpBufferTimer > 0f && (body.IsGrounded || coyoteTimer > 0f);
		bool didJump = false;

		if (body.IsGrounded)
		{
			if (verticalSpeed < 0f)
			{
				verticalSpeed = 0f;
			}

			if (canJump)
			{
				ExecuteJump(upAxis, ref verticalSpeed);
				didJump = true;
			}
		}
		else
		{
			if (canJump)
			{
				ExecuteJump(upAxis, ref verticalSpeed);
				didJump = true;
			}

			if (!didJump)
			{
				verticalSpeed -= gravityStrength * deltaTime;
			}
		}

		body.Velocity = lateralVelocity + upAxis * verticalSpeed;
		body.Simulate(upAxis, deltaTime, groundProbeDistance);
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
			body?.MoveRotation(targetBasis.GetRotationQuaternion(), upAxis);
		}
	}

	private void HandleBlockInput()
	{
		if (WasDigitPressedThisFrame(1))
		{
			selectedBlock = VoxelBlockType.Grass;
		}
		else if (WasDigitPressedThisFrame(2))
		{
			selectedBlock = VoxelBlockType.Dirt;
		}
		else if (WasDigitPressedThisFrame(3))
		{
			selectedBlock = VoxelBlockType.Stone;
		}

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

	private void TryBreakBlock()
	{
		if (world == null || playerCamera == null)
		{
			return;
		}

		if (!TryRaycastFromCamera(out CollisionObject3D? collider, out int faceIndex, out Vector3 position, out Vector3 normal))
		{
			return;
		}

		if (world.TryGetBreakCell(collider, faceIndex, position, normal, out PlanetCellId targetCell))
		{
			world.RemoveBlock(targetCell);
		}
	}

	private void TryPlaceBlock()
	{
		if (world == null || playerCamera == null)
		{
			return;
		}

		if (!TryRaycastFromCamera(out CollisionObject3D? collider, out int faceIndex, out Vector3 position, out Vector3 normal))
		{
			return;
		}

		if (!world.TryGetPlaceCell(collider, faceIndex, position, normal, out PlanetCellId targetCell))
		{
			return;
		}

		if (world.HasBlock(targetCell))
		{
			return;
		}

		if (Bounds.Intersects(world.GetCellAabb(targetCell)))
		{
			return;
		}

		world.PlaceBlock(targetCell, selectedBlock);
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

	private void ExecuteJump(Vector3 upAxis, ref float verticalSpeed)
	{
		verticalSpeed = jumpSpeed;
		coyoteTimer = 0f;
		jumpBufferTimer = 0f;
		GlobalPosition += upAxis * jumpTakeoffDistance;
		body?.SuppressGrounding(jumpGroundingLockTime);
	}

	private Vector2 ReadLookInput()
	{
		Vector2 value = lookInput;
		lookInput = Vector2.Zero;
		return value;
	}

	private bool WasJumpPressedThisFrame()
	{
		return jumpPressedThisFrame || Input.IsActionJustPressed("jump");
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

	private bool WasDigitPressedThisFrame(int digit)
	{
		return digit switch
		{
			1 => digit1PressedThisFrame,
			2 => digit2PressedThisFrame,
			3 => digit3PressedThisFrame,
			_ => false
		};
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

		if (root.GetNodeOrNull<Label>("HelpLabel") is null)
		{
			Label helpLabel = new()
			{
				Name = "HelpLabel",
				Position = new Vector2(16f, 16f),
				Size = new Vector2(420f, 120f),
				Text =
					"WASD move\n" +
					"Space jump\n" +
					"Left click break block\n" +
					"Right click place block\n" +
					"1 Grass  2 Dirt  3 Stone\n" +
					"Esc menu  F5 save",
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			root.AddChild(helpLabel);
		}

		selectedBlockLabel = root.GetNodeOrNull<Label>("SelectedBlockLabel");
		if (selectedBlockLabel == null)
		{
			selectedBlockLabel = new Label
			{
				Name = "SelectedBlockLabel",
				Position = new Vector2(16f, 128f),
				Size = new Vector2(320f, 24f),
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			root.AddChild(selectedBlockLabel);
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
	}

	private void UpdateHud()
	{
		if (selectedBlockLabel != null)
		{
			selectedBlockLabel.Text = "Selected block: " + selectedBlock;
		}

		Vector2 center = GetViewport().GetVisibleRect().Size * 0.5f;

		if (crosshairHorizontal != null)
		{
			crosshairHorizontal.Position = center - crosshairHorizontal.Size * 0.5f;
		}

		if (crosshairVertical != null)
		{
			crosshairVertical.Position = center - crosshairVertical.Size * 0.5f;
		}
	}

	private void ClearFrameInput()
	{
		jumpPressedThisFrame = false;
		escapePressedThisFrame = false;
		primaryPointerPressedThisFrame = false;
		secondaryPointerPressedThisFrame = false;
		digit1PressedThisFrame = false;
		digit2PressedThisFrame = false;
		digit3PressedThisFrame = false;
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
}

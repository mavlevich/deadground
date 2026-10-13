using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

public partial class Main : Node3D
{
	private HeightMap _map = null!;
	private IGameSession _session = null!;
	private Camera3D _camera = null!;
	private CameraRig _cameraRig = null!;
	private SettingsPanel _settingsPanel = null!;
	private FramePacingMetrics? _benchmarkMetrics;
	private int _benchmarkTargetFrames;
	private long _lastFrameTimestamp;
	private int _benchmarkWarmupFrames = 5;
	private Label _stats = null!, _status = null!, _subtitle = null!, _title = null!;
	private UnitViewSet _unitViews = null!;
	private ContactViewSet _contactViews = null!;
	private MeshInstance3D _destination = null!;
	private double _hudTimer, _smokeTimer;
	private bool _smokeTest;
	private bool _terrainWindingValid;
	private long _lastBytes;
	private uint _lastCommandResult;
	private string _scenario = "prototype";
	private CombatEffects _combatEffects = null!;
	private MissionPanel? _missionPanel;
	private StaticGeometry? _scenarioGeometry;
	private UnitCatalog _catalog = null!;
	private SelectionCard _selectionCard = null!;
	private OrderNotification _orderNotification = null!;
	private RoleBadgeOverlay _roleBadgeOverlay = null!;
	private PanelContainer _topHud = null!, _bottomHud = null!;
	private readonly Rect2[] _occludingPanels = new Rect2[6];
	private string? _screenshotPath;
	private double _screenshotTimer = 0.5;

	internal CameraRig CameraRig => _cameraRig;
	internal SettingsPanel SettingsPanel => _settingsPanel;
	internal SelectionCard SelectionCard => _selectionCard;
	internal OrderNotification OrderNotification => _orderNotification;
	internal RoleBadgeOverlay RoleBadgeOverlay => _roleBadgeOverlay;
	internal UnitViewSet UnitViews => _unitViews;
	internal ContactViewSet ContactViews => _contactViews;
	internal UnitCatalog Catalog => _catalog;

	public override void _Ready()
	{
		string[] userArgs = OS.GetCmdlineUserArgs();
		string[] allArgs = OS.GetCmdlineArgs();
		bool isLosSandbox = userArgs.Contains("--los-sandbox") || allArgs.Contains("--los-sandbox");
		bool hasConnect = userArgs.Contains("--connect") || allArgs.Contains("--connect");

		if (isLosSandbox && hasConnect)
		{
			GD.PushError("Invalid arguments: --los-sandbox cannot be combined with --connect.");
			SetProcess(false);
			GetTree().Quit(1);
			return;
		}

		if (isLosSandbox)
		{
			bool isSmoke = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");
			if (isSmoke)
			{
				bool ok = LosSandboxRegressionTests.Run();
				SetProcess(false);
				GetTree().Quit(ok ? 0 : 1);
				return;
			}

			SetProcess(false);
			SetProcessUnhandledInput(false);
			AddChild(new LosSandboxView());
			return;
		}

		_smokeTest = userArgs.Contains("--smoke-test") || allArgs.Contains("--smoke-test");
		if (_smokeTest && !ClientRegressionTests.Run(this))
		{
			SetProcess(false);
			GetTree().Quit(1);
			return;
		}

		string? connectTarget = null;
		int port = 7777;

		void ParseArgs(string[] args)
		{
			for (int i = 0; i < args.Length; i++)
			{
				if (args[i] == "--connect" && i + 1 < args.Length)
				{
					connectTarget = args[i + 1];
					i++;
				}
				else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
				{
					port = p;
					i++;
				}
				else if (args[i] == "--scenario" && i + 1 < args.Length)
				{
					_scenario = args[i + 1].ToLowerInvariant();
					i++;
				}
				else if (args[i] == "--screenshot" && i + 1 < args.Length)
				{
					_screenshotPath = args[i + 1];
					i++;
				}
			}
		}

		ParseArgs(allArgs);
		ParseArgs(userArgs);

		StaticGeometry? scenarioGeometry = null;
		MatchSetup setup;

		if (_scenario == "visibility")
		{
			scenarioGeometry = VisibilityScenario.CreateStaticGeometry();
			_map = scenarioGeometry.HeightMap;
			setup = VisibilityScenario.CreateMatchSetup(scenarioGeometry);
		}
		else if (_scenario == "navigation")
		{
			scenarioGeometry = NavigationScenario.CreateStaticGeometry();
			_map = scenarioGeometry.HeightMap;
			setup = NavigationScenario.CreateMatchSetup(scenarioGeometry);
		}
		else if (_scenario == "navigation-large")
		{
			scenarioGeometry = LargeNavigationScenario.CreateStaticGeometry();
			_map = scenarioGeometry.HeightMap;
			setup = LargeNavigationScenario.CreateMatchSetup(scenarioGeometry);
		}
		else if (_scenario == "combat")
		{
			scenarioGeometry = CombatScenario.CreateStaticGeometry();
			_map = scenarioGeometry.HeightMap;
			setup = CombatScenario.CreateMatchSetup(scenarioGeometry);
		}
		else if (_scenario == "recon-raid")
		{
			scenarioGeometry = ReconRaidScenario.CreateStaticGeometry();
			_map = scenarioGeometry.HeightMap;
			setup = ReconRaidScenario.CreateMatchSetup(scenarioGeometry);
		}
		else
		{
			_map = HeightMap.CreatePrototype();
			setup = PrototypeScenario.CreateMatchSetup(_map);
		}
		_scenarioGeometry = scenarioGeometry;

		if (connectTarget is not null)
		{
			string host = connectTarget;
			if (connectTarget.Contains(':'))
			{
				string[] parts = connectTarget.Split(':');
				host = parts[0];
				if (int.TryParse(parts[1], out int p)) port = p;
			}
			_session = ClientSession.Connect(host, port, setup);
		}
		else
		{
			_session = _scenario switch
			{
				"visibility" => new LocalSession(VisibilityScenario.CreateSimulation(scenarioGeometry!)),
				"navigation" => new LocalSession(NavigationScenario.CreateSimulation(scenarioGeometry!)),
				"navigation-large" => new LocalSession(LargeNavigationScenario.CreateSimulation(scenarioGeometry!)),
				"combat" => new LocalSession(CombatScenario.CreateSimulation(scenarioGeometry!)),
				"recon-raid" => new LocalSession(ReconRaidScenario.CreateSimulation(scenarioGeometry!)),
				_ => new LocalSession(PrototypeScenario.Create(_map))
			};
		}


		MeshInstance3D terrain = TerrainView.Create(_map);
		AddChild(terrain);
		_terrainWindingValid = !_smokeTest || TerrainView.HasUpwardFrontFaces(terrain);
		AddChild(TerrainView.CreateGrid(_map));

		if (scenarioGeometry is not null)
		{
			SetupStructures(scenarioGeometry);
		}

		AddChild(new DirectionalLight3D
		{
			RotationDegrees = new Vector3(-55, -35, 0),
			LightColor = new Color("fff0d2"),
			LightEnergy = 1.2f,
			ShadowEnabled = true
		});
		AddChild(new WorldEnvironment
		{
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Color,
				BackgroundColor = new Color("17211c"),
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = new Color("b9c6b2"),
				AmbientLightEnergy = 0.55f,
				TonemapMode = Godot.Environment.ToneMapper.Filmic
			}
		});
		DisplaySettings loadedConfig = SettingsPanel.LoadConfig();
		string[] combinedArgs = allArgs.Concat(userArgs).ToArray();
		bool cliOverridesVsync = DisplaySettings.HasVsyncOverride(combinedArgs);
		bool cliOverridesMaxFps = DisplaySettings.HasMaxFpsOverride(combinedArgs);
		DisplaySettings activeSettings = DisplaySettings.ParseCommandLine(combinedArgs, loadedConfig);

		_settingsPanel = new SettingsPanel();
		AddChild(_settingsPanel);
		_settingsPanel.Initialize(loadedConfig, activeSettings, cliOverridesVsync, cliOverridesMaxFps);

		if (activeSettings.BenchmarkFrames > 0)
		{
			_benchmarkMetrics = new FramePacingMetrics(activeSettings.BenchmarkFrames + 16);
			_benchmarkTargetFrames = activeSettings.BenchmarkFrames;
		}

		float initialCameraSize = _scenario == "combat" ? 75 : (_scenario == "navigation-large" ? 450 : (_scenario == "recon-raid" ? 160 : 112));
		_camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = initialCameraSize, Far = 3500, Current = true };
		AddChild(_camera);
		Vector3 initialFocus = _scenario == "combat"
			? new Vector3(45, 0, 45)
			: (_scenario == "navigation-large" ? new Vector3(300, 0, 300) : (_scenario == "recon-raid" ? new Vector3(100, 0, 100) : new Vector3(64, 0, 64)));
		_cameraRig = new CameraRig(_camera, initialFocus, initialCameraSize, _map.Width, _map.Depth);
		_cameraRig.SetDefaultView(initialFocus, initialCameraSize);

		_catalog = PrototypeScenario.LoadCatalog();
		_unitViews = new UnitViewSet(this, _map, _catalog);
		_contactViews = new ContactViewSet(this, _map);
		_combatEffects = new CombatEffects(_map);
		AddChild(_combatEffects);

		if (_session is LocalSession && _scenario != "visibility" && _scenario != "combat" && _scenario != "navigation-large" && _scenario != "recon-raid")
		{
			foreach (OwnUnitObservation unit in _session.Replica.OwnUnits)
			{
				// Demo movement is sent as normal client commands through the byte transport.
				_session.Move(unit.Id, unit.Position + new NumericsVector2(0, 60));
			}
			if (_session.Replica.OwnUnits.Length > 0) _unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
		}
		else if (_scenario == "combat" && _session.Replica.OwnUnits.Length > 1)
		{
			// Auto-select first tank for immediate playability
			_unitViews.Selected.Add(_session.Replica.OwnUnits[1].Id);
		}
		else if (_scenario == "navigation-large" && _session.Replica.OwnUnits.Length > 0)
		{
			// Auto-select first recon unit for immediate playability
			_unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
		}
		else if (_scenario == "recon-raid" && _session.Replica.OwnUnits.Length > 0)
		{
			// Auto-select player recon unit for immediate playability
			_unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
		}


		_destination = new MeshInstance3D
		{
			Mesh = new TorusMesh { InnerRadius = 1.5f, OuterRadius = 1.8f, Rings = 24, RingSegments = 8 },
			Scale = new Vector3(1, 0.15f, 1),
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color("edc76b"),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
			},
			Visible = false
		};
		AddChild(_destination);
		BuildHud();
		UpdateViews();
		GD.Print($"M0_CLIENT_READY runtime={System.Environment.Version} own_units={_session.Replica.OwnUnits.Length}");
	}

	private void SetupStructures(StaticGeometry geometry)
	{
		StandardMaterial3D buildingMat = new() { AlbedoColor = new Color("5c5346"), Roughness = 0.85f };
		StandardMaterial3D wallMat = new() { AlbedoColor = new Color("6b756c"), Roughness = 0.9f };

		foreach (OpaqueStructure structure in geometry.Structures)
		{
			float sizeX = structure.Max.X - structure.Min.X;
			float sizeY = structure.Max.Y - structure.Min.Y;
			float sizeZ = structure.Max.Z - structure.Min.Z;
			float posX = (structure.Min.X + structure.Max.X) * 0.5f;
			float posY = (structure.Min.Y + structure.Max.Y) * 0.5f;
			float posZ = (structure.Min.Z + structure.Max.Z) * 0.5f;

			AddChild(new MeshInstance3D
			{
				Mesh = new BoxMesh { Size = new Vector3(sizeX, sizeY, sizeZ) },
				Position = new Vector3(posX, posY, posZ),
				MaterialOverride = structure.Id.Value == 1 ? buildingMat : wallMat
			});
		}
	}

	public override void _Process(double delta)
	{
		long frameStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		float wallClockIntervalMs = 0f;
		if (_lastFrameTimestamp > 0)
		{
			wallClockIntervalMs = (float)((frameStartTimestamp - _lastFrameTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
		}
		_lastFrameTimestamp = frameStartTimestamp;

		_session.Advance(delta);
		UpdateViews();
		_unitViews.UpdateRecoil(delta);
		_combatEffects.Update(delta);
		_combatEffects.ProcessEvents(_session.Replica.Events, _unitViews);
		_missionPanel?.UpdateMission(_session.Mission, _session.Tick);

		_subtitle.Text = _session.StatusDescription;
		if (_title is not null)
		{
			_title.Text = FormatTitle(_session.AssignedPlayerId, _scenario);
		}

		_orderNotification?.Update(delta);

		if (_unitViews.Selected.Count == 1)
		{
			EntityId selId = _unitViews.Selected.First();
			if (_session.Replica.TryGetOwnUnit(selId, out OwnUnitObservation ownUnit))
			{
				_selectionCard?.UpdateSingleOwnUnit(ownUnit);
				_status.Text = $"Unit #{ownUnit.Id.Value} selected / RMB terrain to move, RMB enemy to attack";
			}
		}
		else if (_unitViews.Selected.Count > 1)
		{
			int tanks = 0, recons = 0, others = 0;
			foreach (EntityId id in _unitViews.Selected)
			{
				if (_session.Replica.TryGetOwnUnit(id, out OwnUnitObservation u))
				{
					if (u.Definition < _catalog.Units.Length)
					{
						string cls = _catalog.Units[u.Definition].Classification ?? string.Empty;
						if (cls.Equals("tank", StringComparison.OrdinalIgnoreCase)) tanks++;
						else if (cls.Equals("recon", StringComparison.OrdinalIgnoreCase)) recons++;
						else others++;
					}
					else others++;
				}
			}
			_selectionCard?.UpdateMultipleOwnUnits(_unitViews.Selected.Count, tanks, recons, others);
			_status.Text = $"{_unitViews.Selected.Count} units selected / RMB terrain to move, RMB enemy to attack";
		}
		else if (_contactViews.Selected.Count > 0)
		{
			ContactId firstSelected = _contactViews.Selected.First();
			if (_contactViews.Views.TryGetValue(firstSelected, out ContactView? cv))
			{
				_selectionCard?.UpdateContact(cv.Observation, _session.Tick);
				_status.Text = $"Contact #{cv.Observation.Id.Value} selected / Right-click with armed tank to attack";
			}
		}
		else
		{
			_selectionCard?.UpdateEmpty();
			_status.Text = "No units selected / Left-click unit or contact to inspect";
		}

		if (_session.LastCommandResult is { } result && result.Sequence != _lastCommandResult)
		{
			_lastCommandResult = result.Sequence;
			string msg = result.Queued
				? $"Server queued order #{result.Sequence}"
				: $"Order #{result.Sequence} rejected by server";
			_orderNotification?.ShowNotification(msg);
			if (!result.Queued) _destination.Visible = false;
		}

		_cameraRig.Update(delta, allowInput: !_settingsPanel.IsOpen);

		if (_roleBadgeOverlay is not null && _topHud is not null && _bottomHud is not null && _selectionCard is not null)
		{
			_occludingPanels[0] = _topHud.GetGlobalRect();
			_occludingPanels[1] = _bottomHud.GetGlobalRect();
			_occludingPanels[2] = _selectionCard.GetGlobalRect();
			_occludingPanels[3] = _orderNotification is { Visible: true } ? _orderNotification.GetGlobalRect() : default;
			_occludingPanels[4] = _settingsPanel.IsOpen ? _settingsPanel.GetContainerRect() : default;
			_occludingPanels[5] = _missionPanel is not null ? (_missionPanel.IsMissionFinished ? _missionPanel.GetResultRect() : _missionPanel.GetBriefingRect()) : default;
			_roleBadgeOverlay.UpdateBadges(_camera, _camera.Size, _unitViews, _contactViews, _session.Replica, _session.Tick, _occludingPanels);
		}

		long frameEndTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		float cpuWorkMs = (float)((frameEndTimestamp - frameStartTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

		if (_benchmarkMetrics is not null)
		{
			if (_benchmarkWarmupFrames > 0)
			{
				_benchmarkWarmupFrames--;
			}
			else if (_benchmarkMetrics.Count < _benchmarkTargetFrames && wallClockIntervalMs > 0f)
			{
				_benchmarkMetrics.Record(wallClockIntervalMs, cpuWorkMs);

				if (_benchmarkMetrics.Count >= _benchmarkTargetFrames)
				{
					string hardware = $"{OS.GetName()} / {OS.GetProcessorName()} / Cores: {OS.GetProcessorCount()}";
					Vector2 viewSize = GetViewport().GetVisibleRect().Size;
					string resolution = $"{viewSize.X:F0}x{viewSize.Y:F0}";
					string report = _benchmarkMetrics.FormatReport(
						_scenario, hardware, resolution, _settingsPanel.CurrentSettings.VSyncEnabled, _settingsPanel.CurrentSettings.MaxFps, warmupFrames: 5);
					GD.Print(report);
				}
			}
		}

		_hudTimer += delta;
		if (_hudTimer >= 0.5)
		{
			double rate = (_session.BytesReceived - _lastBytes) / _hudTimer / 1024;
			_lastBytes = _session.BytesReceived;
			string simInfo = _session is LocalSession local
				? $"{local.LastTickMilliseconds:F3} ms per tick / {local.LastTickAllocatedBytes} B tick allocations"
				: _session.StatusDescription;
			_stats.Text = $"20 Hz SIM / 10 Hz STATE / TICK {_session.Tick:00000} / OWN: {_session.Replica.OwnUnits.Length} / CONTACTS: {_session.Replica.ContactCount}\n{simInfo} / {rate:F1} KiB/s";
			_hudTimer = 0;
		}

		if (_screenshotPath is not null)
		{
			_screenshotTimer -= delta;
			if (_screenshotTimer <= 0)
			{
				Image? img = GetViewport().GetTexture().GetImage();
				if (img is not null)
				{
					img.SavePng(_screenshotPath);
					GD.Print($"SCREENSHOT_SAVED {_screenshotPath}");
				}
				_screenshotPath = null;
			}
		}

		if (_smokeTest)
		{
			_smokeTimer += delta;
			if (_smokeTimer >= 1)
			{
				bool ok = _scenario switch
				{
					"visibility" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 2,
					"navigation" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
					"navigation-large" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
					"combat" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
					"recon-raid" => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 3,
					_ => _terrainWindingValid && _session.Tick >= 10 && _session.Replica.OwnUnits.Length == 12 &&
						_session.Replica.OwnUnits[0].Position.Y > 24
				};
				string token = _scenario == "combat"
					? (ok ? "M1_CLIENT_COMBAT_SMOKE_OK" : "M1_CLIENT_COMBAT_SMOKE_FAILED")
					: (_scenario == "navigation-large"
						? (ok ? "M1_CLIENT_NAV_LARGE_SMOKE_OK" : "M1_CLIENT_NAV_LARGE_SMOKE_FAILED")
						: (_scenario == "recon-raid"
							? (ok ? "M1_CLIENT_RECON_RAID_SMOKE_OK" : "M1_CLIENT_RECON_RAID_SMOKE_FAILED")
							: (ok ? "M0_CLIENT_SMOKE_OK" : "M0_CLIENT_SMOKE_FAILED")));
				GD.Print(token);
				GetTree().Quit(ok ? 0 : 1);
			}
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (_settingsPanel.IsOpen)
		{
			if (@event is InputEventKey { Pressed: true, Echo: false } openKey)
			{
				if (openKey.Keycode == Key.Escape)
				{
					_settingsPanel.Close();
					GetViewport().SetInputAsHandled();
					return;
				}

				if (openKey.Keycode is Key.F10 or Key.O)
				{
					_settingsPanel.Toggle();
					GetViewport().SetInputAsHandled();
					return;
				}
			}

			// Centrally block all gameplay input while settings overlay is open
			GetViewport().SetInputAsHandled();
			return;
		}

		if (_missionPanel?.IsMissionFinished == true)
		{
			if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R } && _session is LocalSession)
			{
				OnRetryMission();
				GetViewport().SetInputAsHandled();
				return;
			}

			if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right })
			{
				_status.Text = "Mission ended. Orders are disabled.";
				GetViewport().SetInputAsHandled();
				return;
			}

			if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F })
			{
				_status.Text = "Mission ended. Orders are disabled.";
				GetViewport().SetInputAsHandled();
				return;
			}
		}

		if (@event is InputEventKey { Pressed: true, Echo: false } key)
		{
			if (key.Keycode is Key.F10 or Key.O)
			{
				_settingsPanel.Toggle();
				GetViewport().SetInputAsHandled();
				return;
			}

			if (key.Keycode == Key.Escape)
			{
				_unitViews.Selected.Clear();
				_contactViews.Selected.Clear();
				_status.Text = "No units selected";
				return;
			}

			if (key.Keycode == Key.Space)
			{
				_cameraRig.ResetToDefault();
				return;
			}

			if (_scenario == "navigation-large" && _unitViews.Selected.Count > 0)
			{
				NumericsVector2? testTarget = key.Keycode switch
				{
					Key.Key1 => new NumericsVector2(350f, 350f),
					Key.Key2 => new NumericsVector2(1050f, 550f),
					Key.Key3 => new NumericsVector2(1201.1f, 580f),
					Key.Key4 => new NumericsVector2(1440f, 1440f),
					Key.Key5 => new NumericsVector2(1650f, 750f),
					Key.Key6 => new NumericsVector2(500f, 200f),
					_ => null
				};

				if (testTarget is not null)
				{
					foreach (EntityId id in _unitViews.Selected)
					{
						_session.Move(id, testTarget.Value);
					}
					_status.Text = $"Route target set to {testTarget.Value}";
				}
			}

			if (key.Keycode == Key.F && _unitViews.Selected.Count > 0 && _contactViews.Selected.Count > 0)
			{
				ContactId target = _contactViews.Selected.First();
				int totalUnits = _unitViews.Selected.Count;
				int sent = 0;
				foreach (EntityId id in _unitViews.Selected)
				{
					if (_session.Attack(id, target)) sent++;
				}
				string attackMsg = sent == 0
					? $"Failed to submit attack order / 0 of {totalUnits} units -> Contact #{target.Value}"
					: sent < totalUnits
						? $"Attack order submitted for {sent} of {totalUnits} units -> Contact #{target.Value}"
						: $"Attack order submitted / {sent} units -> Contact #{target.Value}";
				_orderNotification?.ShowNotification(attackMsg);
				_status.Text = attackMsg;
			}
		}

		if (@event is not InputEventMouseButton { Pressed: true } mouse) return;
		if (mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
		{
			if (mouse.ButtonIndex == MouseButton.WheelUp) _cameraRig.ZoomIn();
			else _cameraRig.ZoomOut();
			return;
		}

		if (mouse.ButtonIndex == MouseButton.Left)
		{
			EntityId? nearestUnit = null;
			ContactId? nearestContact = null;

			if (_roleBadgeOverlay.TryHitBadge(mouse.Position, out EntityId? badgeUnit, out ContactId? badgeContact))
			{
				if (badgeContact is not null) nearestContact = badgeContact;
				else if (badgeUnit is not null) nearestUnit = badgeUnit;
			}

			if (nearestUnit is null && nearestContact is null)
			{
				float nearestUnitDistance = 24 * 24;
				foreach ((EntityId id, UnitView view) in _unitViews.Views)
				{
					Vector3 center = view.Root.Position + Vector3.Up;
					if (_camera.IsPositionBehind(center)) continue;
					float distance = _camera.UnprojectPosition(center).DistanceSquaredTo(mouse.Position);
					if (distance < nearestUnitDistance) { nearestUnitDistance = distance; nearestUnit = id; }
				}

				float nearestContactDistance = 24 * 24;
				foreach ((ContactId id, ContactView view) in _contactViews.Views)
				{
					Vector3 center = view.Root.Position + Vector3.Up * 1.5f;
					if (_camera.IsPositionBehind(center)) continue;
					float distance = _camera.UnprojectPosition(center).DistanceSquaredTo(mouse.Position);
					if (distance < nearestContactDistance) { nearestContactDistance = distance; nearestContact = id; }
				}

				if (nearestContact is not null && nearestUnit is not null && nearestContactDistance > nearestUnitDistance)
				{
					nearestContact = null;
				}
			}

			if (nearestContact is not null)
			{
				_unitViews.Selected.Clear();
				if (!mouse.ShiftPressed) _contactViews.Selected.Clear();
				_contactViews.Selected.Add(nearestContact.Value);

				if (_contactViews.Views.TryGetValue(nearestContact.Value, out ContactView? cv))
				{
					string kind = cv.Observation.State == ContactState.Current ? "Current observation" : "Last-known marker";
					int ageTicks = Math.Max(0, _session.Tick - cv.Observation.LastSeenTick);
					float ageSeconds = ageTicks / (float)Simulation.TickRate;
					_status.Text = $"Enemy Contact #{cv.Observation.Id.Value}: {kind} (age: {ageSeconds:F1}s) / right-click with tank selected to attack";
				}
			}
			else if (nearestUnit is not null)
			{
				_contactViews.Selected.Clear();
				if (!mouse.ShiftPressed) _unitViews.Selected.Clear();
				_unitViews.Selected.Add(nearestUnit.Value);
				_status.Text = $"{_unitViews.Selected.Count} selected / RMB contact to attack, RMB terrain to move";
			}
			else
			{
				if (!mouse.ShiftPressed)
				{
					_unitViews.Selected.Clear();
					_contactViews.Selected.Clear();
					_status.Text = "No units selected";
				}
			}
		}
		if (mouse.ButtonIndex == MouseButton.Right)
		{
			if (_unitViews.Selected.Count > 0)
			{
				ContactId? targetContact = null;
				if (_roleBadgeOverlay.TryHitBadge(mouse.Position, out _, out ContactId? badgeEnemy) && badgeEnemy is not null)
				{
					targetContact = badgeEnemy;
				}
				else
				{
					float nearestContactDistance = 28 * 28;
					foreach ((ContactId id, ContactView view) in _contactViews.Views)
					{
						Vector3 center = view.Root.Position + Vector3.Up * 1.5f;
						if (_camera.IsPositionBehind(center)) continue;
						float distance = _camera.UnprojectPosition(center).DistanceSquaredTo(mouse.Position);
						if (distance < nearestContactDistance)
						{
							nearestContactDistance = distance;
							targetContact = id;
						}
					}
				}

				if (targetContact is not null)
				{
					int totalUnits = _unitViews.Selected.Count;
					int sent = 0;
					foreach (EntityId id in _unitViews.Selected)
					{
						if (_session.Attack(id, targetContact.Value)) sent++;
					}
					_destination.Visible = false;
					string attackMsg = sent == 0
						? $"Failed to submit attack order / 0 of {totalUnits} units -> Contact #{targetContact.Value.Value}"
						: sent < totalUnits
							? $"Attack order submitted for {sent} of {totalUnits} units -> Contact #{targetContact.Value.Value}"
							: $"Attack order submitted / {sent} units -> Contact #{targetContact.Value.Value}";
					_orderNotification?.ShowNotification(attackMsg);
					_status.Text = attackMsg;
					return;
				}

				Vector3 origin = _camera.ProjectRayOrigin(mouse.Position);
				Vector3 direction = _camera.ProjectRayNormal(mouse.Position);
				if (!_map.TryRaycast(new NumericsVector3(origin.X, origin.Y, origin.Z),
					new NumericsVector3(direction.X, direction.Y, direction.Z), _camera.Far, out NumericsVector3 hit)) return;
				int totalUnitsMove = _unitViews.Selected.Count;
				int sentMove = 0;
				foreach (EntityId id in _unitViews.Selected)
					if (_session.Move(id, new NumericsVector2(hit.X, hit.Z))) sentMove++;
				_destination.Position = new Vector3(hit.X, hit.Y + 0.12f, hit.Z);
				_destination.Visible = sentMove > 0;
				string moveMsg = sentMove == 0
					? $"Failed to submit move order / 0 of {totalUnitsMove} units / {hit.X:F0}, {hit.Z:F0} m"
					: sentMove < totalUnitsMove
						? $"Move order submitted for {sentMove} of {totalUnitsMove} units / {hit.X:F0}, {hit.Z:F0} m"
						: $"Move order submitted / {sentMove} units / {hit.X:F0}, {hit.Z:F0} m";
				_orderNotification?.ShowNotification(moveMsg);
				_status.Text = moveMsg;
			}
			else if (_contactViews.Selected.Count > 0)
			{
				_orderNotification?.ShowNotification("Enemy contacts cannot receive orders");
				_status.Text = "Enemy contacts cannot receive orders";
			}
		}
	}

	private void UpdateViews()
	{
		int selectedUnits = _unitViews.Selected.Count;
		int selectedContacts = _contactViews.Selected.Count;
		_unitViews.Sync(_session.Replica, _session.RenderTick);
		_contactViews.Sync(_session.Replica, _session.RenderTick);
		if (selectedUnits != _unitViews.Selected.Count || selectedContacts != _contactViews.Selected.Count)
		{
			if (_unitViews.Selected.Count > 0)
				_status.Text = $"{_unitViews.Selected.Count} selected / right-click terrain to move";
			else if (_contactViews.Selected.Count > 0)
				_status.Text = $"{_contactViews.Selected.Count} contacts selected / cannot receive orders";
			else
				_status.Text = "No selection / unit or contact removed from view";
		}
	}

	public override void _Notification(int what)
	{
		base._Notification(what);

		if (what is (int)NotificationApplicationFocusOut or (int)NotificationWMWindowFocusOut)
		{
			_cameraRig?.ClearInput();
			Godot.Engine.MaxFps = 30;
		}
		else if (what is (int)NotificationApplicationFocusIn or (int)NotificationWMWindowFocusIn)
		{
			if (_settingsPanel is not null)
			{
				SettingsPanel.ApplyToEngine(_settingsPanel.CurrentSettings);
			}
		}
	}

	private void BuildHud()
	{
		CanvasLayer canvas = new();
		AddChild(canvas);

		_roleBadgeOverlay = new RoleBadgeOverlay(_catalog);
		canvas.AddChild(_roleBadgeOverlay);

		PanelContainer top = new() { Position = new Vector2(24, 24), MouseFilter = Control.MouseFilterEnum.Ignore };
		top.AddThemeStyleboxOverride("panel", PanelStyle());
		MarginContainer margin = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
		foreach (string side in new[] { "left", "right", "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 18);
		VBoxContainer stack = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
		stack.AddThemeConstantOverride("separation", 9);
		_title = new Label
		{
			Text = FormatTitle(_session.AssignedPlayerId, _scenario),
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		_title.AddThemeFontSizeOverride("font_size", 21);
		_title.AddThemeColorOverride("font_color", new Color("edc76b"));
		stack.AddChild(_title);
		_subtitle = new Label { Text = _session.StatusDescription, MouseFilter = Control.MouseFilterEnum.Ignore };
		_subtitle.AddThemeColorOverride("font_color", new Color("b2beb1"));
		stack.AddChild(_subtitle);
		_stats = new Label { Text = "Starting session...", MouseFilter = Control.MouseFilterEnum.Ignore };
		_stats.AddThemeFontSizeOverride("font_size", 14);
		stack.AddChild(_stats);
		margin.AddChild(stack); top.AddChild(margin); canvas.AddChild(top);
		_topHud = top;

		PanelContainer bottom = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
		canvas.AddChild(bottom);
		bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
		bottom.OffsetLeft = 24; bottom.OffsetRight = -24; bottom.OffsetTop = -88; bottom.OffsetBottom = -24;
		bottom.AddThemeStyleboxOverride("panel", PanelStyle());
		VBoxContainer instructions = new() { MouseFilter = Control.MouseFilterEnum.Ignore };

		HBoxContainer footer = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
		footer.AddThemeConstantOverride("separation", 16);
		Label controls = new()
		{
			Text = "  LMB select   /   Shift+LMB add   /   RMB order   /   F attack   /   WASD pan   /   Wheel zoom",
			MouseFilter = Control.MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
		};
		controls.AddThemeFontSizeOverride("font_size", 15);
		Button settingsBtn = new()
		{
			Text = "SETTINGS [F10]",
			MouseFilter = Control.MouseFilterEnum.Stop
		};
		settingsBtn.Pressed += () => _settingsPanel.Toggle();
		footer.AddChild(controls);
		footer.AddChild(settingsBtn);

		_status = new Label { Text = "  Select a unit to issue orders", MouseFilter = Control.MouseFilterEnum.Ignore };
		_status.AddThemeColorOverride("font_color", new Color("edc76b"));
		instructions.AddChild(footer); instructions.AddChild(_status); bottom.AddChild(instructions);
		_bottomHud = bottom;

		_selectionCard = new SelectionCard(_catalog);
		_selectionCard.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
		_selectionCard.OffsetLeft = 24;
		_selectionCard.OffsetBottom = -96;
		_selectionCard.OffsetTop = -96 - 100;
		_selectionCard.OffsetRight = 24 + 380;
		canvas.AddChild(_selectionCard);

		_orderNotification = new OrderNotification();
		_orderNotification.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
		_orderNotification.OffsetBottom = -96;
		_orderNotification.OffsetTop = -96 - 36;
		_orderNotification.OffsetLeft = -180;
		_orderNotification.OffsetRight = 180;
		canvas.AddChild(_orderNotification);

		if (_scenario == "recon-raid")
		{
			_missionPanel = new MissionPanel(this, _map);
			canvas.AddChild(_missionPanel);
			_missionPanel.SetLocalMode(_session is LocalSession);
			_missionPanel.RetryRequested += OnRetryMission;
		}
	}

	private void OnRetryMission()
	{
		if (_session is not LocalSession || _scenarioGeometry is null) return;

		_unitViews.Clear();
		_contactViews.Clear();
		_roleBadgeOverlay?.Clear();
		_selectionCard?.UpdateEmpty();
		_orderNotification?.Clear();
		_destination.Visible = false;
		_combatEffects.Clear();
		_session.Dispose();

		Simulation newSim = ReconRaidScenario.CreateSimulation(_scenarioGeometry);
		_session = new LocalSession(newSim);
		_lastCommandResult = 0;

		_cameraRig.ResetToDefault();
		_missionPanel?.ResetUi();
		UpdateViews();

		if (_session.Replica.OwnUnits.Length > 0)
		{
			_unitViews.Selected.Add(_session.Replica.OwnUnits[0].Id);
		}
		_status.Text = "Mission restarted. Objective: Destroy enemy command vehicle. Preserve recon vehicle.";
		_orderNotification?.ShowNotification("Mission restarted. Destroy enemy command vehicle.");
	}

	private static StyleBoxFlat PanelStyle() => new()
	{
		BgColor = new Color(0.055f, 0.08f, 0.065f, 0.94f),
		BorderColor = new Color("46533e"),
		BorderWidthBottom = 1,
		BorderWidthTop = 1,
		BorderWidthLeft = 1,
		BorderWidthRight = 1,
		CornerRadiusBottomLeft = 6,
		CornerRadiusBottomRight = 6,
		CornerRadiusTopLeft = 6,
		CornerRadiusTopRight = 6
	};

	public static string FormatTitle(int assignedSide, string scenario) =>
		$"DEAD GROUND   /   SIDE {assignedSide}   /   {(scenario == "visibility" ? "RECON SCENARIO" : scenario == "combat" ? "COMBAT SCENARIO" : scenario == "recon-raid" ? "RECON RAID MISSION" : "M0 NETWORK TEST")}";

	public string TitleText => _title?.Text ?? string.Empty;

	public override void _ExitTree()
	{
		_missionPanel?.Cleanup();
		_session?.Dispose();
	}
}

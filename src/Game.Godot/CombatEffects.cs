using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal partial class CombatEffects : Node3D
{
    public const int MaxTracers = 16;
    public const int MaxMuzzleFlashes = 16;
    public const int MaxSmokePuffs = 24;
    public const int MaxImpacts = 16;
    public const int MaxSparks = 16;
    public const int MaxExplosions = 8;
    public const int MaxResidues = 16;
    public const int MaxPendingImpacts = 16;
    public const int MaxAudioPlayers = 6;
    public const int MaxBlastDust = 6;
    public const int MaxBarrelSmoke = 6;
    public const int EventHistorySize = 256;

    public const float TracerSpeed = 160f;
    public const float TracerLength = 3.2f;
    public const float TracerThickness = 0.28f;
    public const float MinTracerDuration = 0.08f;
    public const float MaxTracerDuration = 0.22f;

    public const float MuzzleFlashLifetime = 0.08f;
    public const float SmokePuffLifetime = 0.40f;
    public const float ImpactLifetime = 0.16f;
    public const float SparksLifetime = 0.20f;
    public const float ExplosionLifetime = 0.60f;
    public const float ResidueLifetime = 15.0f;
    public const float ResidueFadeStart = 4.0f;
    // Muzzle blast dust is strongest for a gun close to the ground and fades out above this height.
    public const float BlastDustMaxMuzzleHeight = 3.5f;
    // Shot particles are skipped for events older than this; younger ones are fast-forwarded.
    public const float GunParticlesMaxEventAge = 0.2f;

    private readonly HeightMap _map;
    private readonly GpuParticles3D[] _blastDust = new GpuParticles3D[MaxBlastDust];
    private readonly GpuParticles3D[] _barrelSmoke = new GpuParticles3D[MaxBarrelSmoke];
    private int _nextBlastDust;
    private int _nextBarrelSmoke;

    internal GpuParticles3D[] BlastDust => _blastDust;
    internal GpuParticles3D[] BarrelSmoke => _barrelSmoke;
    private readonly uint[] _recentEventIds = new uint[EventHistorySize];
    private int _recentEventIndex;

    private readonly Random _presentationRng = new(42);

    internal struct TimedTracer
    {
        public MeshInstance3D Mesh;
        public Vector3 Origin;
        public Vector3 Target;
        public Vector3 Direction;
        public float TotalDistance;
        public float TotalDuration;
        public float ElapsedTime;
        public float StreakLength;
        public bool Active;
    }

    internal struct TimedMesh
    {
        public MeshInstance3D Mesh;
        public float Lifetime;
        public float MaxLifetime;
        public Vector3 Velocity;
        public float InitialScale;
        public float TargetScale;
        public Vector3 InitialScaleVector;
        public bool Active;
        public bool ScaleDown;
        public bool ScaleUp;
        public bool FadeAlpha;
    }

    internal struct PendingImpact
    {
        public Vector3 Target;
        public float DelayRemaining;
        public bool Active;
    }

    internal enum SoundPriority
    {
        TookFire = 1,
        Cannon = 2,
        Explosion = 3
    }

    private struct ActiveVoice
    {
        public SoundPriority Priority;
        public float StartTime;
    }

    internal TimedTracer[] Tracers => _tracers;
    internal TimedMesh[] MuzzleFlashes => _muzzleFlashes;
    internal TimedMesh[] SmokePuffs => _smokePuffs;
    internal TimedMesh[] Sparks => _sparks;
    internal TimedMesh[] Impacts => _impacts;
    internal TimedMesh[] Explosions => _explosions;
    internal TimedMesh[] Residues => _residues;
    internal PendingImpact[] PendingImpactsQueue => _pendingImpacts;
    internal AudioStreamPlayer3D[] AudioPlayers => _audioPlayers;

    private readonly TimedTracer[] _tracers = new TimedTracer[MaxTracers];
    private readonly TimedMesh[] _muzzleFlashes = new TimedMesh[MaxMuzzleFlashes];
    private readonly TimedMesh[] _smokePuffs = new TimedMesh[MaxSmokePuffs];
    private readonly TimedMesh[] _sparks = new TimedMesh[MaxSparks];
    private readonly TimedMesh[] _impacts = new TimedMesh[MaxImpacts];
    private readonly TimedMesh[] _explosions = new TimedMesh[MaxExplosions];
    private readonly TimedMesh[] _residues = new TimedMesh[MaxResidues];
    private readonly PendingImpact[] _pendingImpacts = new PendingImpact[MaxPendingImpacts];

    private readonly AudioStreamPlayer3D[] _audioPlayers = new AudioStreamPlayer3D[MaxAudioPlayers];
    private readonly ActiveVoice[] _activeVoices = new ActiveVoice[MaxAudioPlayers];
    private readonly AudioListener3D _audioListener;
    // A few seeds of the same gun report, so a series of shots does not repeat one sample.
    public const int CannonVariantCount = 3;
    private readonly AudioStreamWav[] _cannonVariants = new AudioStreamWav[CannonVariantCount];
    internal AudioStreamWav[] CannonVariants => _cannonVariants;
    private AudioStreamWav? _tookFireAudio;
    private AudioStreamWav? _explosionAudio;

    private int _droppedMeshEffectsCount;
    private int _droppedAudioVoicesCount;
    private int _peakEffectsCount;
    private int _totalAudioRequests;

    public int ActiveTracersCount => CountActiveTracers();
    public int ActiveMuzzleFlashesCount => CountActiveMeshPool(_muzzleFlashes);
    public int ActiveSmokePuffsCount => CountActiveMeshPool(_smokePuffs);
    public int ActiveSparksCount => CountActiveMeshPool(_sparks);
    public int ActiveImpactsCount => CountActiveMeshPool(_impacts);
    public int ActiveExplosionsCount => CountActiveMeshPool(_explosions);
    public int ActiveResiduesCount => CountActiveMeshPool(_residues);
    public int PendingImpactsCount => CountActivePendingImpacts();

    public int ActiveEffectsCount => CalculateActiveEffectsCount();
    public int DroppedMeshEffectsCount => _droppedMeshEffectsCount;
    public int DroppedAudioVoicesCount => _droppedAudioVoicesCount;
    public int DroppedEffectsCount => _droppedMeshEffectsCount + _droppedAudioVoicesCount;
    public int PeakEffectsCount => _peakEffectsCount;
    public int TotalAudioRequests => _totalAudioRequests;

    public CombatEffects(HeightMap map)
    {
        _map = map;
        InitTracers();
        InitMuzzleFlashes();
        InitSmokePuffs();
        InitSparks();
        InitImpacts();
        InitExplosions();
        InitResidues();
        for (int i = 0; i < MaxBlastDust; i++)
        {
            _blastDust[i] = ParticleEffects.CreateBlastDust();
            AddChild(_blastDust[i]);
        }
        for (int i = 0; i < MaxBarrelSmoke; i++)
        {
            _barrelSmoke[i] = ParticleEffects.CreateBarrelSmoke();
            AddChild(_barrelSmoke[i]);
        }

        _audioListener = new AudioListener3D { Name = "CombatAudioListener" };
        AddChild(_audioListener);
        try
        {
            _audioListener.MakeCurrent();
        }
        catch
        {
            // Headless safe
        }

        InitAudio();
    }

    public void UpdateListener(Vector3 cameraFocus)
    {
        _audioListener.Position = cameraFocus;
    }

    private void InitTracers()
    {
        StandardMaterial3D tracerMat = new()
        {
            AlbedoColor = new Color("ffe477"),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        for (int i = 0; i < MaxTracers; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new BoxMesh { Size = new Vector3(1.0f, 1.0f, 1.0f) },
                MaterialOverride = tracerMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _tracers[i] = new TimedTracer { Mesh = mesh, Active = false };
        }
    }

    private void InitMuzzleFlashes()
    {
        StandardMaterial3D flashMat = new()
        {
            AlbedoColor = new Color("fff275"),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        for (int i = 0; i < MaxMuzzleFlashes; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new SphereMesh { Radius = 0.50f, Height = 1.0f, RadialSegments = 8, Rings = 4 },
                MaterialOverride = flashMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _muzzleFlashes[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitSmokePuffs()
    {
        StandardMaterial3D smokeMat = new()
        {
            AlbedoColor = new Color(0.7f, 0.7f, 0.7f, 0.60f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 1.0f
        };
        for (int i = 0; i < MaxSmokePuffs; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new SphereMesh { Radius = 0.65f, Height = 1.3f, RadialSegments = 8, Rings = 4 },
                MaterialOverride = smokeMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _smokePuffs[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitSparks()
    {
        StandardMaterial3D sparksMat = new()
        {
            AlbedoColor = new Color("ffaa33"),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        for (int i = 0; i < MaxSparks; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new SphereMesh { Radius = 0.70f, Height = 1.4f, RadialSegments = 8, Rings = 4 },
                MaterialOverride = sparksMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _sparks[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitImpacts()
    {
        StandardMaterial3D impactMat = new()
        {
            AlbedoColor = new Color("ff7733"),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        for (int i = 0; i < MaxImpacts; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new SphereMesh { Radius = 1.05f, Height = 2.1f, RadialSegments = 8, Rings = 4 },
                MaterialOverride = impactMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _impacts[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitExplosions()
    {
        StandardMaterial3D explosionMat = new()
        {
            AlbedoColor = new Color("ff4411"),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        for (int i = 0; i < MaxExplosions; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new SphereMesh { Radius = 1.6f, Height = 3.2f, RadialSegments = 12, Rings = 6 },
                MaterialOverride = explosionMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _explosions[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitResidues()
    {
        StandardMaterial3D residueMat = new()
        {
            AlbedoColor = new Color(0.10f, 0.09f, 0.08f, 0.85f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 1.0f
        };
        for (int i = 0; i < MaxResidues; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new CylinderMesh { TopRadius = 1.9f, BottomRadius = 1.9f, Height = 0.02f, RadialSegments = 14 },
                MaterialOverride = residueMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _residues[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitAudio()
    {
        for (int i = 0; i < CannonVariantCount; i++)
            _cannonVariants[i] = CannonSound.CreateStream(1337 + i);
        _tookFireAudio = SynthesizeTookFireSound();
        _explosionAudio = SynthesizeExplosionSound();

        for (int i = 0; i < MaxAudioPlayers; i++)
        {
            AudioStreamPlayer3D player = new()
            {
                MaxDistance = 250f,
                UnitSize = 15f
            };
            AddChild(player);
            _audioPlayers[i] = player;
        }
    }

    private static AudioStreamWav SynthesizeTookFireSound()
    {
        const int sampleRate = 44100;
        const float duration = 0.45f;
        int count = (int)(sampleRate * duration);
        byte[] data = new byte[count * 2];
        Random rand = new(999);

        float lpState = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / sampleRate;

            // 1. Ultra-sharp ballistic strike transient (0-10ms)
            float snapEnv = MathF.Exp(-t * 240.0f);
            float snap = (((float)rand.NextDouble() * 2f - 1f) * 0.7f + MathF.Sin(2f * MathF.PI * 1850.0f * t) * 0.5f) * snapEnv;

            // 2. Kinetic mass momentum transfer thud (0-150ms)
            float thudEnv = MathF.Exp(-t * 24.0f);
            float thudFreq = 45.0f + 75.0f * MathF.Exp(-t * 35.0f);
            float thud = MathF.Sin(2f * MathF.PI * thudFreq * t) * thudEnv * 0.9f;

            // 3. Heavy rolled homogeneous armor ringing (inharmonic steel modes)
            float ringEnv = MathF.Exp(-t * 16.0f);
            float ring = (MathF.Sin(2f * MathF.PI * 380.0f * t) * 0.40f +
                          MathF.Sin(2f * MathF.PI * 690.0f * t) * 0.35f +
                          MathF.Sin(2f * MathF.PI * 1280.0f * t) * 0.25f +
                          MathF.Sin(2f * MathF.PI * 2450.0f * t) * 0.15f) * ringEnv;

            // 4. Spall and metal fragment scatter
            float white = (float)rand.NextDouble() * 2f - 1f;
            lpState += (white - lpState) * 0.35f;
            float sizzle = lpState * MathF.Exp(-t * 30.0f) * 0.35f;

            float raw = snap * 1.0f + thud * 1.1f + ring * 0.8f + sizzle * 0.5f;
            float sat = MathF.Tanh(raw * 1.5f) * 0.88f;

            short pcm = (short)(sat * 28000);
            data[i * 2] = (byte)(pcm & 0xFF);
            data[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = sampleRate,
            Stereo = false,
            Data = data
        };
    }

    private static AudioStreamWav SynthesizeExplosionSound()
    {
        const int sampleRate = 44100;
        const float duration = 1.8f;
        int count = (int)(sampleRate * duration);
        byte[] data = new byte[count * 2];
        Random rand = new(777);

        float lpState = 0f;
        float lpState2 = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / sampleRate;

            // 1. Initial explosive blast wave transient (0-30ms)
            float crackEnv = MathF.Exp(-t * 120.0f);
            float crack = (((float)rand.NextDouble() * 2f - 1f) * 0.8f + MathF.Sin(2f * MathF.PI * 180.0f * MathF.Exp(-t * 60.0f) * t) * 0.6f) * crackEnv;

            // 2. Catastrophic ammo cook-off & fuel fireball roar (20ms - 600ms)
            float roarEnv = MathF.Pow(MathF.Sin(MathF.Min(1.0f, t / 0.05f) * (MathF.PI * 0.5f)), 2f) * MathF.Exp(-t * 5.5f);
            float subRumble = MathF.Sin(2f * MathF.PI * (32.0f + 18.0f * MathF.Exp(-t * 4.0f)) * t);
            float midRumble = MathF.Sin(2f * MathF.PI * (52.0f + 26.0f * MathF.Exp(-t * 6.0f)) * t) * 0.5f;
            float roar = (subRumble + midRumble) * roarEnv;

            // 3. Structural metal tear / shudder
            float shudderEnv = MathF.Exp(-t * 14.0f);
            float shudder = (MathF.Sin(2f * MathF.PI * 120.0f * t) * 0.4f + MathF.Sin(2f * MathF.PI * 280.0f * t) * 0.3f) * shudderEnv;

            // 4. Long rolling shockwave rumble & debris tail (50ms - 1.8s)
            float white = (float)rand.NextDouble() * 2f - 1f;
            float cutoff = MathF.Max(0.012f, 0.16f * MathF.Exp(-t * 2.2f));
            lpState += (white - lpState) * cutoff;
            lpState2 += (lpState - lpState2) * cutoff;

            float tailEnv = MathF.Pow(MathF.Sin(MathF.Min(1.0f, t / 0.07f) * (MathF.PI * 0.5f)), 2f) * MathF.Exp(-t * 2.8f) * 0.8f;
            float tail = lpState2 * tailEnv;

            float raw = crack * 0.95f + roar * 1.3f + shudder * 0.4f + tail * 1.1f;
            float sat = MathF.Tanh(raw * 1.7f) * 0.94f;

            short pcm = (short)(sat * 31000);
            data[i * 2] = (byte)(pcm & 0xFF);
            data[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = sampleRate,
            Stereo = false,
            Data = data
        };
    }

    private void PlaySound(AudioStreamWav? stream, Vector3 position, SoundPriority priority)
    {
        if (stream is null) return;
        _totalAudioRequests++;

        try
        {
            int selectedPlayer = -1;

            for (int i = 0; i < MaxAudioPlayers; i++)
            {
                if (!_audioPlayers[i].Playing)
                {
                    selectedPlayer = i;
                    break;
                }
            }

            if (selectedPlayer == -1)
            {
                int lowestPriorityIdx = -1;
                SoundPriority lowestPrio = SoundPriority.Explosion + 1;
                for (int i = 0; i < MaxAudioPlayers; i++)
                {
                    if (_activeVoices[i].Priority < lowestPrio)
                    {
                        lowestPrio = _activeVoices[i].Priority;
                        lowestPriorityIdx = i;
                    }
                }

                if (lowestPriorityIdx != -1 && lowestPrio < priority)
                {
                    selectedPlayer = lowestPriorityIdx;
                }
                else
                {
                    _droppedAudioVoicesCount++;
                    return;
                }
            }

            AudioStreamPlayer3D player = _audioPlayers[selectedPlayer];
            player.Position = position;
            player.Stream = stream;
            player.PitchScale = 0.96f + (float)(_presentationRng.NextDouble() * 0.08);
            player.Play();

            _activeVoices[selectedPlayer] = new ActiveVoice
            {
                Priority = priority,
                StartTime = (float)Godot.Time.GetTicksMsec() / 1000f
            };
        }
        catch
        {
            // Headless dummy audio driver fallback
        }
    }

    public void ProcessEvents(ReadOnlySpan<CombatEventObservation> events, UnitViewSet unitViews, int currentTick = 0)
    {
        for (int i = 0; i < events.Length; i++)
        {
            ref readonly CombatEventObservation ev = ref events[i];
            if (HasSeenEvent(ev.EventId)) continue;
            RecordSeenEvent(ev.EventId);

            float ageSeconds = 0f;
            if (currentTick > 0 && ev.ServerTick > 0 && currentTick >= ev.ServerTick)
            {
                ageSeconds = (currentTick - ev.ServerTick) * 0.05f;
            }

            Vector3 origin = new(ev.Origin.X, ev.Origin.Y, ev.Origin.Z);
            Vector3 target = new(ev.Target.X, ev.Target.Y, ev.Target.Z);
            Vector3 shotDir = (target - origin).LengthSquared() > 0.0001f
                ? (target - origin).Normalized()
                : Vector3.Forward;

            if (ev.Kind == CombatEventKind.ShotFired)
            {
                float distance = origin.DistanceTo(target);
                float flightDuration = Mathf.Clamp(distance / TracerSpeed, MinTracerDuration, MaxTracerDuration);

                // If entire shot sequence including impact has expired, discard
                if (ageSeconds >= flightDuration + SmokePuffLifetime)
                {
                    continue;
                }

                if (ev.ShooterUnitId.Value != 0 && unitViews.Views.TryGetValue(ev.ShooterUnitId, out UnitView? shooter))
                {
                    if (shooter.TryGetMuzzleTransform(out Transform3D muzzleXform))
                    {
                        origin = muzzleXform.Origin;
                        if (origin != target)
                        {
                            shotDir = (target - origin).Normalized();
                        }
                    }
                    if (ageSeconds < 0.10f)
                    {
                        shooter.ApplyRecoil(shotDir);
                    }
                }

                if (ageSeconds < MuzzleFlashLifetime)
                {
                    SpawnMuzzleFlash(origin, shotDir, ageSeconds);
                }

                if (ageSeconds < GunParticlesMaxEventAge)
                {
                    SpawnGunParticles(origin, shotDir, ageSeconds);
                }

                if (ageSeconds < SmokePuffLifetime)
                {
                    SpawnSmokePuff(origin, isImpactSmoke: false, ageSeconds);
                }

                if (ageSeconds < flightDuration)
                {
                    SpawnTracer(origin, target, flightDuration, ageSeconds);
                    QueuePendingImpact(target, flightDuration - ageSeconds);
                }
                else
                {
                    float hitElapsed = ageSeconds - flightDuration;
                    if (hitElapsed < SmokePuffLifetime)
                    {
                        TriggerImpactFeedback(target, hitElapsed);
                    }
                }

                if (ageSeconds < 0.15f)
                {
                    PlaySound(_cannonVariants[_presentationRng.Next(CannonVariantCount)], origin, SoundPriority.Cannon);
                }
            }
            else if (ev.Kind == CombatEventKind.UnitDestroyed)
            {
                if (ageSeconds >= ResidueLifetime)
                {
                    continue;
                }

                CancelPendingImpactsNear(target, 3.0f);

                if (ageSeconds < ExplosionLifetime)
                {
                    SpawnExplosion(target, ageSeconds);
                }

                SpawnResidue(target, ageSeconds);

                if (ageSeconds < 0.20f)
                {
                    PlaySound(_explosionAudio, target, SoundPriority.Explosion);
                }
            }
            else if (ev.Kind == CombatEventKind.TookFire)
            {
                if (ageSeconds < SmokePuffLifetime)
                {
                    TriggerImpactFeedback(target, ageSeconds);
                }
            }
        }
    }

    internal void SpawnDirectShot(Vector3 origin, Vector3 target)
    {
        Vector3 shotDir = (target - origin).LengthSquared() > 0.0001f
            ? (target - origin).Normalized()
            : Vector3.Forward;

        float distance = origin.DistanceTo(target);
        float flightDuration = Mathf.Clamp(distance / TracerSpeed, MinTracerDuration, MaxTracerDuration);

        SpawnMuzzleFlash(origin, shotDir);
        SpawnSmokePuff(origin, isImpactSmoke: false);
        SpawnGunParticles(origin, shotDir);
        SpawnTracer(origin, target, flightDuration);
        QueuePendingImpact(target, flightDuration);
        PlaySound(_cannonVariants[_presentationRng.Next(CannonVariantCount)], origin, SoundPriority.Cannon);
    }

    // Ground dust from the muzzle blast and gun gas drifting from the barrel. Presentation only.
    private void SpawnGunParticles(Vector3 muzzle, Vector3 shotDir, float ageSeconds = 0f)
    {
        Vector3 flat = new(shotDir.X, 0f, shotDir.Z);
        flat = flat.LengthSquared() > 0.0001f ? flat.Normalized() : Vector3.Forward;
        Basis facing = Basis.LookingAt(flat, Vector3.Up);

        float ground = _map.GetHeight(muzzle.X, muzzle.Z);
        float strength = Mathf.Clamp(1f - (muzzle.Y - ground) / BlastDustMaxMuzzleHeight, 0f, 1f);
        if (strength > 0.05f)
        {
            GpuParticles3D dust = _blastDust[_nextBlastDust];
            _nextBlastDust = (_nextBlastDust + 1) % MaxBlastDust;
            dust.Transform = new Transform3D(facing, new Vector3(muzzle.X, ground + 1.0f, muzzle.Z) - flat * 0.6f);
            dust.AmountRatio = 0.35f + 0.65f * strength;
            dust.Preprocess = ageSeconds;
            dust.Restart();
        }

        GpuParticles3D smoke = _barrelSmoke[_nextBarrelSmoke];
        _nextBarrelSmoke = (_nextBarrelSmoke + 1) % MaxBarrelSmoke;
        smoke.Transform = new Transform3D(facing, muzzle);
        smoke.Preprocess = ageSeconds;
        smoke.Restart();
    }

    private void TriggerImpactFeedback(Vector3 position, float elapsedOffset = 0f)
    {
        if (elapsedOffset >= SmokePuffLifetime)
        {
            return;
        }

        SpawnImpact(position, elapsedOffset);
        SpawnSparks(position, elapsedOffset);
        SpawnSmokePuff(position, isImpactSmoke: true, elapsedOffset);
        if (elapsedOffset < 0.1f)
        {
            PlaySound(_tookFireAudio, position, SoundPriority.TookFire);
        }
    }

    private bool HasSeenEvent(uint id)
    {
        for (int i = 0; i < _recentEventIds.Length; i++)
        {
            if (_recentEventIds[i] == id) return true;
        }
        return false;
    }

    private void RecordSeenEvent(uint id)
    {
        _recentEventIds[_recentEventIndex] = id;
        _recentEventIndex = (_recentEventIndex + 1) % _recentEventIds.Length;
    }

    private void SpawnMuzzleFlash(Vector3 position, Vector3 direction, float elapsedOffset = 0f)
    {
        float remainingLifetime = MuzzleFlashLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_muzzleFlashes, MaxMuzzleFlashes);
        ref TimedMesh tm = ref _muzzleFlashes[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = MuzzleFlashLifetime;
        tm.Velocity = Vector3.Zero;
        tm.ScaleDown = true;
        tm.ScaleUp = false;
        tm.FadeAlpha = false;
        tm.InitialScaleVector = new Vector3(0.5f, 0.5f, 1.1f);
        tm.Mesh.Position = position;
        float factor = MathF.Max(0.01f, remainingLifetime / MuzzleFlashLifetime);
        tm.Mesh.Scale = tm.InitialScaleVector * factor;
        if (direction != Vector3.Zero)
        {
            tm.Mesh.LookAt(position + direction, Vector3.Up);
        }
        tm.Mesh.Transparency = 0f;
        tm.Mesh.Visible = true;
    }

    private void SpawnSmokePuff(Vector3 position, bool isImpactSmoke = false, float elapsedOffset = 0f)
    {
        float remainingLifetime = SmokePuffLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_smokePuffs, MaxSmokePuffs);
        ref TimedMesh tm = ref _smokePuffs[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = SmokePuffLifetime;
        tm.Velocity = isImpactSmoke ? new Vector3(0, 1.2f, 0) : new Vector3(0, 0.7f, 0);
        tm.ScaleDown = false;
        tm.ScaleUp = true;
        tm.FadeAlpha = true;
        tm.InitialScale = isImpactSmoke ? 0.65f : 0.55f;
        tm.TargetScale = isImpactSmoke ? 2.2f : 1.7f;
        tm.InitialScaleVector = Vector3.Zero;
        tm.Mesh.Position = position + tm.Velocity * elapsedOffset;
        tm.Mesh.Scale = Vector3.One * tm.InitialScale;
        tm.Mesh.Transparency = 0f;
        tm.Mesh.Visible = true;
    }

    private static void CalculateTracerSegment(
        Vector3 origin, Vector3 target, Vector3 direction, float totalDistance,
        float streakLength, float alpha,
        out Vector3 mid, out float segLen, out Vector3 front)
    {
        front = origin + direction * (alpha * totalDistance);
        Vector3 back = front - direction * streakLength;

        if ((back - origin).Dot(direction) < 0f)
        {
            back = origin;
        }
        if ((front - target).Dot(direction) > 0f)
        {
            front = target;
        }

        Vector3 segment = front - back;
        segLen = segment.Length();
        mid = (front + back) * 0.5f;
    }

    private void SpawnTracer(Vector3 origin, Vector3 target, float flightDuration, float elapsedOffset = 0f)
    {
        float distance = origin.DistanceTo(target);
        if (distance < 0.1f) return;
        if (elapsedOffset >= flightDuration) return;

        int idx = AcquireTracerSlot();
        ref TimedTracer tracer = ref _tracers[idx];
        tracer.Active = true;
        tracer.Origin = origin;
        tracer.Target = target;
        tracer.TotalDistance = distance;
        tracer.Direction = (target - origin) / distance;
        tracer.TotalDuration = flightDuration;
        tracer.ElapsedTime = elapsedOffset;
        tracer.StreakLength = Mathf.Min(TracerLength, distance * 0.8f);

        float alpha = elapsedOffset / flightDuration;
        CalculateTracerSegment(
            origin, target, tracer.Direction, distance, tracer.StreakLength, alpha,
            out Vector3 mid, out float segLen, out Vector3 front);

        tracer.Mesh.Position = mid;
        tracer.Mesh.Scale = new Vector3(TracerThickness, TracerThickness, Mathf.Max(0.1f, segLen));
        tracer.Mesh.LookAt(front + tracer.Direction * 5f, Vector3.Up);
        tracer.Mesh.Visible = true;
    }

    private void QueuePendingImpact(Vector3 target, float delay)
    {
        int idx = AcquirePendingImpactSlot();
        ref PendingImpact pi = ref _pendingImpacts[idx];
        pi.Active = true;
        pi.Target = target;
        pi.DelayRemaining = delay;
    }

    private void CancelPendingImpactsNear(Vector3 position, float radius)
    {
        float rSq = radius * radius;
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            if (_pendingImpacts[i].Active && _pendingImpacts[i].Target.DistanceSquaredTo(position) <= rSq)
            {
                _pendingImpacts[i].Active = false;
            }
        }
    }

    private void SpawnSparks(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = SparksLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_sparks, MaxSparks);
        ref TimedMesh tm = ref _sparks[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = SparksLifetime;
        tm.Velocity = new Vector3(0, 2.0f, 0);
        tm.ScaleDown = true;
        tm.ScaleUp = false;
        tm.FadeAlpha = false;
        tm.InitialScale = 1.0f;
        tm.InitialScaleVector = Vector3.Zero;
        tm.Mesh.Position = position + tm.Velocity * elapsedOffset;
        tm.Mesh.Scale = Vector3.One;
        tm.Mesh.Transparency = 0f;
        tm.Mesh.Visible = true;
    }

    private void SpawnImpact(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = ImpactLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_impacts, MaxImpacts);
        ref TimedMesh tm = ref _impacts[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = ImpactLifetime;
        tm.Velocity = Vector3.Zero;
        tm.ScaleDown = true;
        tm.ScaleUp = false;
        tm.FadeAlpha = false;
        tm.InitialScale = 1.0f;
        tm.InitialScaleVector = Vector3.Zero;
        tm.Mesh.Position = position;
        tm.Mesh.Scale = Vector3.One * 1.0f;
        tm.Mesh.Transparency = 0f;
        tm.Mesh.Visible = true;
    }

    private void SpawnExplosion(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = ExplosionLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_explosions, MaxExplosions);
        ref TimedMesh tm = ref _explosions[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = ExplosionLifetime;
        tm.Velocity = new Vector3(0, 0.9f, 0);
        tm.ScaleDown = false;
        tm.ScaleUp = true;
        tm.FadeAlpha = true;
        tm.InitialScale = 0.8f;
        tm.TargetScale = 3.6f;
        tm.InitialScaleVector = Vector3.Zero;
        tm.Mesh.Position = position + tm.Velocity * elapsedOffset;
        float t = elapsedOffset / ExplosionLifetime;
        float factor = Mathf.Lerp(tm.InitialScale, tm.TargetScale, t);
        tm.Mesh.Scale = Vector3.One * factor;
        tm.Mesh.Transparency = t;
        tm.Mesh.Visible = true;
    }

    private void SpawnResidue(Vector3 position, float elapsedOffset = 0f)
    {
        float remainingLifetime = ResidueLifetime - elapsedOffset;
        if (remainingLifetime <= 0f) return;

        int idx = AcquireMeshSlot(_residues, MaxResidues);
        ref TimedMesh tm = ref _residues[idx];
        tm.Active = true;
        tm.Lifetime = remainingLifetime;
        tm.MaxLifetime = ResidueLifetime;
        tm.Velocity = Vector3.Zero;
        tm.ScaleDown = false;
        tm.ScaleUp = false;
        tm.FadeAlpha = false;
        tm.InitialScaleVector = Vector3.Zero;

        float groundY = _map.GetHeight(position.X, position.Z) + 0.03f;
        tm.Mesh.Position = new Vector3(position.X, groundY, position.Z);
        tm.Mesh.Scale = Vector3.One;
        if (remainingLifetime < ResidueFadeStart)
        {
            float fadeRatio = remainingLifetime / ResidueFadeStart;
            tm.Mesh.Transparency = 1f - fadeRatio;
        }
        else
        {
            tm.Mesh.Transparency = 0f;
        }
        tm.Mesh.Visible = true;
    }

    private int AcquireMeshSlot(TimedMesh[] pool, int max)
    {
        for (int i = 0; i < max; i++)
        {
            if (!pool[i].Active) return i;
        }

        _droppedMeshEffectsCount++;
        int oldestIdx = 0;
        float minLifetime = float.MaxValue;
        for (int i = 0; i < max; i++)
        {
            if (pool[i].Lifetime < minLifetime)
            {
                minLifetime = pool[i].Lifetime;
                oldestIdx = i;
            }
        }
        return oldestIdx;
    }

    private int AcquireTracerSlot()
    {
        for (int i = 0; i < MaxTracers; i++)
        {
            if (!_tracers[i].Active) return i;
        }

        _droppedMeshEffectsCount++;
        int oldestIdx = 0;
        float maxElapsed = -1f;
        for (int i = 0; i < MaxTracers; i++)
        {
            if (_tracers[i].ElapsedTime > maxElapsed)
            {
                maxElapsed = _tracers[i].ElapsedTime;
                oldestIdx = i;
            }
        }
        return oldestIdx;
    }

    private int AcquirePendingImpactSlot()
    {
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            if (!_pendingImpacts[i].Active) return i;
        }

        _droppedMeshEffectsCount++;
        int oldestIdx = 0;
        float minDelay = float.MaxValue;
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            if (_pendingImpacts[i].DelayRemaining < minDelay)
            {
                minDelay = _pendingImpacts[i].DelayRemaining;
                oldestIdx = i;
            }
        }
        return oldestIdx;
    }

    public void Clear()
    {
        System.Array.Clear(_recentEventIds, 0, _recentEventIds.Length);
        _recentEventIndex = 0;

        for (int i = 0; i < MaxTracers; i++)
        {
            _tracers[i].Active = false;
            _tracers[i].Mesh.Visible = false;
        }
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            _pendingImpacts[i].Active = false;
        }

        ResetPool(_muzzleFlashes);
        ResetPool(_smokePuffs);
        ResetPool(_sparks);
        ResetPool(_impacts);
        ResetPool(_explosions);
        ResetPool(_residues);
        foreach (GpuParticles3D dust in _blastDust)
        {
            dust.Emitting = false;
        }
        foreach (GpuParticles3D smoke in _barrelSmoke)
        {
            smoke.Emitting = false;
        }

        for (int i = 0; i < MaxAudioPlayers; i++)
        {
            if (_audioPlayers[i].Playing)
            {
                _audioPlayers[i].Stop();
            }
            _audioPlayers[i].Stream = null;
            _activeVoices[i] = default;
        }
    }

    public void ResetCounters()
    {
        _droppedMeshEffectsCount = 0;
        _droppedAudioVoicesCount = 0;
        _peakEffectsCount = CalculateActiveEffectsCount();
        _totalAudioRequests = 0;
    }

    private static void ResetPool(TimedMesh[] pool)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            pool[i].Active = false;
            pool[i].Mesh.Visible = false;
            pool[i].InitialScaleVector = Vector3.Zero;
            pool[i].Velocity = Vector3.Zero;
            pool[i].Mesh.Transparency = 0f;
        }
    }

    public void Update(double delta)
    {
        float dt = (float)delta;

        UpdateTracers(dt);
        UpdateMeshPool(_muzzleFlashes, dt);
        UpdateMeshPool(_smokePuffs, dt);
        UpdateMeshPool(_sparks, dt);
        UpdateMeshPool(_impacts, dt);
        UpdateMeshPool(_explosions, dt);
        UpdateResiduePool(dt);

        // Update pending impacts after existing mesh pools so newly spawned effects are not aged in the same frame
        UpdatePendingImpacts(dt);

        int currentActive = CalculateActiveEffectsCount();
        if (currentActive > _peakEffectsCount)
        {
            _peakEffectsCount = currentActive;
        }
    }

    private void UpdateTracers(float dt)
    {
        for (int i = 0; i < MaxTracers; i++)
        {
            ref TimedTracer tracer = ref _tracers[i];
            if (!tracer.Active) continue;

            tracer.ElapsedTime += dt;
            if (tracer.ElapsedTime >= tracer.TotalDuration)
            {
                tracer.Active = false;
                tracer.Mesh.Visible = false;
                continue;
            }

            float alpha = tracer.ElapsedTime / tracer.TotalDuration;
            CalculateTracerSegment(
                tracer.Origin, tracer.Target, tracer.Direction, tracer.TotalDistance,
                tracer.StreakLength, alpha,
                out Vector3 mid, out float segLen, out Vector3 front);

            tracer.Mesh.Position = mid;
            tracer.Mesh.Scale = new Vector3(TracerThickness, TracerThickness, Mathf.Max(0.1f, segLen));
            if (segLen > 0.001f)
            {
                tracer.Mesh.LookAt(front + tracer.Direction * 5f, Vector3.Up);
            }
        }
    }

    private void UpdatePendingImpacts(float dt)
    {
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            ref PendingImpact pi = ref _pendingImpacts[i];
            if (!pi.Active) continue;

            pi.DelayRemaining -= dt;
            if (pi.DelayRemaining <= 0f)
            {
                float overshoot = -pi.DelayRemaining;
                pi.Active = false;
                TriggerImpactFeedback(pi.Target, overshoot);
            }
        }
    }

    private static void UpdateMeshPool(TimedMesh[] pool, float dt)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            ref TimedMesh tm = ref pool[i];
            if (!tm.Active) continue;

            tm.Lifetime -= dt;
            if (tm.Lifetime <= 0f)
            {
                tm.Active = false;
                tm.Mesh.Visible = false;
                tm.InitialScaleVector = Vector3.Zero;
                continue;
            }

            if (tm.Velocity != Vector3.Zero)
            {
                tm.Mesh.Position += tm.Velocity * dt;
            }

            float t = 1f - (tm.Lifetime / tm.MaxLifetime);

            if (tm.ScaleDown)
            {
                float factor = MathF.Max(0.01f, 1f - t);
                if (tm.InitialScaleVector != Vector3.Zero)
                {
                    tm.Mesh.Scale = tm.InitialScaleVector * factor;
                }
                else
                {
                    tm.Mesh.Scale = Vector3.One * (tm.InitialScale * factor);
                }
            }
            else if (tm.ScaleUp)
            {
                float factor = Mathf.Lerp(tm.InitialScale, tm.TargetScale, t);
                tm.Mesh.Scale = Vector3.One * factor;
            }

            if (tm.FadeAlpha)
            {
                tm.Mesh.Transparency = t;
            }
        }
    }

    private void UpdateResiduePool(float dt)
    {
        for (int i = 0; i < MaxResidues; i++)
        {
            ref TimedMesh residue = ref _residues[i];
            if (!residue.Active) continue;

            residue.Lifetime -= dt;
            if (residue.Lifetime <= 0f)
            {
                residue.Active = false;
                residue.Mesh.Visible = false;
                continue;
            }

            if (residue.Lifetime < ResidueFadeStart)
            {
                float fadeRatio = residue.Lifetime / ResidueFadeStart;
                residue.Mesh.Transparency = 1f - fadeRatio;
            }
            else
            {
                residue.Mesh.Transparency = 0f;
            }
        }
    }

    private int CountActiveTracers()
    {
        int count = 0;
        for (int i = 0; i < MaxTracers; i++)
        {
            if (_tracers[i].Active) count++;
        }
        return count;
    }

    private static int CountActiveMeshPool(TimedMesh[] pool)
    {
        int count = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i].Active) count++;
        }
        return count;
    }

    private int CountActivePendingImpacts()
    {
        int count = 0;
        for (int i = 0; i < MaxPendingImpacts; i++)
        {
            if (_pendingImpacts[i].Active) count++;
        }
        return count;
    }

    private int CalculateActiveEffectsCount()
    {
        return CountActiveTracers() +
               CountActiveMeshPool(_muzzleFlashes) +
               CountActiveMeshPool(_smokePuffs) +
               CountActiveMeshPool(_sparks) +
               CountActiveMeshPool(_impacts) +
               CountActiveMeshPool(_explosions) +
               CountActiveMeshPool(_residues) +
               CountActivePendingImpacts();
    }
}

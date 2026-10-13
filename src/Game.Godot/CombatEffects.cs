using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal partial class CombatEffects : Node3D
{
    private const int MaxTracers = 16;
    private const int MaxMuzzleFlashes = 16;
    private const int MaxSmokePuffs = 16;
    private const int MaxImpacts = 16;
    private const int MaxExplosions = 8;
    private const int MaxAudioPlayers = 4;
    private const int EventHistorySize = 128;

    private readonly HeightMap _map;
    private readonly uint[] _recentEventIds = new uint[EventHistorySize];
    private int _recentEventIndex;

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
    }

    internal TimedMesh[] Tracers => _tracers;

    private readonly TimedMesh[] _tracers = new TimedMesh[MaxTracers];
    private readonly TimedMesh[] _muzzleFlashes = new TimedMesh[MaxMuzzleFlashes];
    private readonly TimedMesh[] _smokePuffs = new TimedMesh[MaxSmokePuffs];
    private readonly TimedMesh[] _impacts = new TimedMesh[MaxImpacts];
    private readonly TimedMesh[] _explosions = new TimedMesh[MaxExplosions];

    private readonly AudioStreamPlayer3D[] _audioPlayers = new AudioStreamPlayer3D[MaxAudioPlayers];
    private int _nextAudioPlayer;
    private AudioStreamWav? _cannonAudio;
    private AudioStreamWav? _explosionAudio;

    public CombatEffects(HeightMap map)
    {
        _map = map;
        InitTracers();
        InitMuzzleFlashes();
        InitSmokePuffs();
        InitImpacts();
        InitExplosions();
        InitAudio();
    }

    private void InitTracers()
    {
        StandardMaterial3D tracerMat = new()
        {
            AlbedoColor = new Color("ffe066"),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        for (int i = 0; i < MaxTracers; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new BoxMesh { Size = new Vector3(0.12f, 0.12f, 1.0f) },
                MaterialOverride = tracerMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _tracers[i] = new TimedMesh { Mesh = mesh, Active = false };
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
                Mesh = new SphereMesh { Radius = 0.55f, Height = 1.1f, RadialSegments = 8, Rings = 4 },
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
            AlbedoColor = new Color(0.7f, 0.7f, 0.7f, 0.65f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 1.0f
        };
        for (int i = 0; i < MaxSmokePuffs; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new SphereMesh { Radius = 0.7f, Height = 1.4f, RadialSegments = 8, Rings = 4 },
                MaterialOverride = smokeMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _smokePuffs[i] = new TimedMesh { Mesh = mesh, Active = false };
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
                Mesh = new SphereMesh { Radius = 0.6f, Height = 1.2f, RadialSegments = 8, Rings = 4 },
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
            AlbedoColor = new Color("ff4d1a"),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
        };
        for (int i = 0; i < MaxExplosions; i++)
        {
            MeshInstance3D mesh = new()
            {
                Mesh = new SphereMesh { Radius = 1.4f, Height = 2.8f, RadialSegments = 12, Rings = 6 },
                MaterialOverride = explosionMat,
                Visible = false,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
            };
            AddChild(mesh);
            _explosions[i] = new TimedMesh { Mesh = mesh, Active = false };
        }
    }

    private void InitAudio()
    {
        _cannonAudio = SynthesizeCannonSound();
        _explosionAudio = SynthesizeExplosionSound();

        for (int i = 0; i < MaxAudioPlayers; i++)
        {
            AudioStreamPlayer3D player = new()
            {
                MaxDistance = 150f,
                UnitSize = 10f
            };
            AddChild(player);
            _audioPlayers[i] = player;
        }
    }

    private static AudioStreamWav SynthesizeCannonSound()
    {
        const int sampleRate = 22050;
        const float duration = 0.35f;
        int count = (int)(sampleRate * duration);
        byte[] data = new byte[count * 2];
        Random rand = new(1337);

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / sampleRate;
            float env = MathF.Exp(-t * 14f);
            float freq = 85f - t * 65f;
            float thump = MathF.Sin(2f * MathF.PI * freq * t);
            float noise = ((float)rand.NextDouble() * 2f - 1f) * MathF.Exp(-t * 24f);
            float s = Math.Clamp((thump * 0.65f + noise * 0.35f) * env, -1f, 1f);
            short pcm = (short)(s * 28000);
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
        const int sampleRate = 22050;
        const float duration = 0.55f;
        int count = (int)(sampleRate * duration);
        byte[] data = new byte[count * 2];
        Random rand = new(777);

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / sampleRate;
            float env = MathF.Exp(-t * 7f);
            float rumble = MathF.Sin(2f * MathF.PI * (50f - t * 25f) * t);
            float noise = ((float)rand.NextDouble() * 2f - 1f) * MathF.Exp(-t * 12f);
            float s = Math.Clamp((rumble * 0.45f + noise * 0.55f) * env, -1f, 1f);
            short pcm = (short)(s * 30000);
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

    private void PlaySound(AudioStreamWav? stream, Vector3 position)
    {
        if (stream is null) return;
        try
        {
            AudioStreamPlayer3D player = _audioPlayers[_nextAudioPlayer];
            _nextAudioPlayer = (_nextAudioPlayer + 1) % MaxAudioPlayers;
            player.Position = position;
            player.Stream = stream;
            player.Play();
        }
        catch
        {
            // Headless dummy audio driver fallback
        }
    }

    public void ProcessEvents(ReadOnlySpan<CombatEventObservation> events, UnitViewSet unitViews)
    {
        for (int i = 0; i < events.Length; i++)
        {
            ref readonly CombatEventObservation ev = ref events[i];
            if (HasSeenEvent(ev.EventId)) continue;
            RecordSeenEvent(ev.EventId);

            Vector3 origin = new(ev.Origin.X, ev.Origin.Y, ev.Origin.Z);
            Vector3 target = new(ev.Target.X, ev.Target.Y, ev.Target.Z);

            if (ev.Kind == CombatEventKind.ShotFired)
            {
                SpawnMuzzleFlash(origin);
                SpawnSmokePuff(origin);
                SpawnTracer(origin, target);
                SpawnImpact(target);
                PlaySound(_cannonAudio, origin);

                // Vehicle recoil applied only to the actual shooter unit
                if (ev.ShooterUnitId.Value != 0 && unitViews.Views.TryGetValue(ev.ShooterUnitId, out UnitView? shooter))
                {
                    Vector3 shotDir = (target - origin).Normalized();
                    shooter.ApplyRecoil(shotDir);
                }
            }
            else if (ev.Kind == CombatEventKind.UnitDestroyed)
            {
                SpawnExplosion(target);
                PlaySound(_explosionAudio, target);
            }
            else if (ev.Kind == CombatEventKind.TookFire)
            {
                SpawnImpact(target);
                PlaySound(_cannonAudio, target);
            }
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

    private void SpawnMuzzleFlash(Vector3 position)
    {
        for (int i = 0; i < MaxMuzzleFlashes; i++)
        {
            if (!_muzzleFlashes[i].Active)
            {
                ref TimedMesh tm = ref _muzzleFlashes[i];
                tm.Active = true;
                tm.Lifetime = 0.12f;
                tm.MaxLifetime = 0.12f;
                tm.Mesh.Position = position;
                tm.Mesh.Scale = Vector3.One * 1.1f;
                tm.Mesh.Visible = true;
                break;
            }
        }
    }

    private void SpawnSmokePuff(Vector3 position)
    {
        for (int i = 0; i < MaxSmokePuffs; i++)
        {
            if (!_smokePuffs[i].Active)
            {
                ref TimedMesh tm = ref _smokePuffs[i];
                tm.Active = true;
                tm.Lifetime = 0.45f;
                tm.MaxLifetime = 0.45f;
                tm.Velocity = new Vector3(0, 0.8f, 0);
                tm.InitialScale = 0.6f;
                tm.TargetScale = 1.8f;
                tm.Mesh.Position = position;
                tm.Mesh.Scale = Vector3.One * tm.InitialScale;
                tm.Mesh.Visible = true;
                break;
            }
        }
    }

    private void SpawnTracer(Vector3 origin, Vector3 target)
    {
        float length = origin.DistanceTo(target);
        if (length < 0.1f) return;

        for (int i = 0; i < MaxTracers; i++)
        {
            if (!_tracers[i].Active)
            {
                ref TimedMesh tm = ref _tracers[i];
                tm.Active = true;
                tm.Lifetime = 0.16f;
                tm.MaxLifetime = 0.16f;

                Vector3 mid = (origin + target) * 0.5f;
                tm.Mesh.Position = mid;
                tm.InitialScaleVector = new Vector3(1f, 1f, length);
                tm.Mesh.Scale = tm.InitialScaleVector;
                if (origin != target)
                {
                    tm.Mesh.LookAt(target, Vector3.Up);
                }
                tm.Mesh.Visible = true;
                break;
            }
        }
    }

    private void SpawnImpact(Vector3 position)
    {
        for (int i = 0; i < MaxImpacts; i++)
        {
            if (!_impacts[i].Active)
            {
                ref TimedMesh tm = ref _impacts[i];
                tm.Active = true;
                tm.Lifetime = 0.22f;
                tm.MaxLifetime = 0.22f;
                tm.Mesh.Position = position;
                tm.Mesh.Scale = Vector3.One * 1.3f;
                tm.Mesh.Visible = true;
                break;
            }
        }
    }

    private void SpawnExplosion(Vector3 position)
    {
        for (int i = 0; i < MaxExplosions; i++)
        {
            if (!_explosions[i].Active)
            {
                ref TimedMesh tm = ref _explosions[i];
                tm.Active = true;
                tm.Lifetime = 0.55f;
                tm.MaxLifetime = 0.55f;
                tm.InitialScale = 0.8f;
                tm.TargetScale = 3.2f;
                tm.Mesh.Position = position;
                tm.Mesh.Scale = Vector3.One * tm.InitialScale;
                tm.Mesh.Visible = true;
                break;
            }
        }
    }

    public void Clear()
    {
        System.Array.Clear(_recentEventIds, 0, _recentEventIds.Length);
        _recentEventIndex = 0;
        ResetPool(_tracers);
        ResetPool(_muzzleFlashes);
        ResetPool(_smokePuffs);
        ResetPool(_impacts);
        ResetPool(_explosions);
        for (int i = 0; i < _audioPlayers.Length; i++)
        {
            if (_audioPlayers[i].Playing)
            {
                _audioPlayers[i].Stop();
            }
        }
    }

    private static void ResetPool(TimedMesh[] pool)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            pool[i].Active = false;
            pool[i].Mesh.Visible = false;
            pool[i].InitialScaleVector = Vector3.Zero;
            pool[i].Velocity = Vector3.Zero;
        }
    }

    public void Update(double delta)
    {
        float dt = (float)delta;
        UpdateMeshPool(_tracers, dt, scaleDown: true);
        UpdateMeshPool(_muzzleFlashes, dt, scaleDown: true);
        UpdateMeshPool(_smokePuffs, dt, scaleDown: false, scaleUp: true);
        UpdateMeshPool(_impacts, dt, scaleDown: true);
        UpdateMeshPool(_explosions, dt, scaleDown: false, scaleUp: true);
    }

    private static void UpdateMeshPool(TimedMesh[] pool, float dt, bool scaleDown = false, bool scaleUp = false)
    {
        for (int i = 0; i < pool.Length; i++)
        {
            if (!pool[i].Active) continue;
            pool[i].Lifetime -= dt;
            if (pool[i].Lifetime <= 0f)
            {
                pool[i].Active = false;
                pool[i].Mesh.Visible = false;
                pool[i].InitialScaleVector = Vector3.Zero;
                continue;
            }

            if (pool[i].Velocity != Vector3.Zero)
            {
                pool[i].Mesh.Position += pool[i].Velocity * dt;
            }

            float t = 1f - (pool[i].Lifetime / pool[i].MaxLifetime);
            if (scaleDown)
            {
                float factor = MathF.Max(0.01f, 1f - t);
                if (pool[i].InitialScaleVector != Vector3.Zero)
                {
                    pool[i].Mesh.Scale = new Vector3(
                        pool[i].InitialScaleVector.X * factor,
                        pool[i].InitialScaleVector.Y * factor,
                        pool[i].InitialScaleVector.Z);
                }
                else
                {
                    pool[i].Mesh.Scale = Vector3.One * factor;
                }
            }
            else if (scaleUp)
            {
                float factor = Mathf.Lerp(pool[i].InitialScale, pool[i].TargetScale, t);
                pool[i].Mesh.Scale = Vector3.One * factor;
            }
        }
    }
}

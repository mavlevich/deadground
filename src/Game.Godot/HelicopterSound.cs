using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Procedural CH-47D tandem-rotor audio synthesizer and zero-allocation playback controller.
/// Synthesizes a seamless looping low-frequency rotor slap ("wop-wop-wop") layered over
/// twin-turbine hum and soft rotor wash without external audio assets.
/// </summary>
internal static class HelicopterSound
{
    internal const int SampleRate = 44100;
    // 1.0714s = exact 12 blade-pass cycles at 11.2 Hz so the WAV loops seamlessly without clicks.
    internal const float LoopDuration = 12.0f / 11.2f;
    internal const float Peak = 0.85f;

    internal static float[] RenderRotorLoop(int seed = 47)
    {
        int count = (int)MathF.Round(SampleRate * LoopDuration);
        float[] samples = new float[count];
        Random rand = new(seed);

        float lp1 = 0f;
        float lp2 = 0f;
        float washLp = 0f;
        const float bladeFreq = 11.2f; // Tandem 3-blade ~225 RPM -> ~11.25 Hz slap fundamental
        const float turbineFreq = 188.0f;

        // Prime filters for seamless wraparound
        for (int i = 0; i < count; i++)
        {
            float t = (float)i / SampleRate;
            float phase = MathF.Tau * bladeFreq * t;
            float slapEnv = MathF.Pow(0.5f + 0.5f * MathF.Sin(phase), 3.0f);
            float secondarySlap = MathF.Pow(0.5f + 0.5f * MathF.Sin(phase + 2.1f), 4.0f) * 0.45f;
            float envelope = 0.22f + 0.78f * MathF.Min(1.0f, slapEnv + secondarySlap);

            float white = (float)rand.NextDouble() * 2f - 1f;
            // Low-passed rotor thump (~140 Hz)
            const float kThump = 0.020f;
            lp1 += (white - lp1) * kThump;
            lp2 += (lp1 - lp2) * kThump;

            // Mid-low rotor air wash (~480 Hz)
            const float kWash = 0.065f;
            washLp += (white - washLp) * kWash;

            // Subtle sub-bass blade harmonic + twin turboshaft hum
            float subThump = MathF.Sin(phase * 3.0f) * 0.14f * slapEnv;
            float turbine = (MathF.Sin(MathF.Tau * turbineFreq * t) * 0.035f +
                             MathF.Sin(MathF.Tau * (turbineFreq * 2.0f) * t) * 0.015f);

            samples[i] = (lp2 * 4.2f + washLp * 0.95f + subThump) * envelope + turbine;
        }

        // Crossfade final 30ms with start so loop boundary is completely click-free
        int fadeSamples = Math.Min(count / 8, (int)(SampleRate * 0.03f));
        for (int i = 0; i < fadeSamples; i++)
        {
            float alpha = (float)i / fadeSamples;
            int tailIdx = count - fadeSamples + i;
            samples[tailIdx] = samples[tailIdx] * (1f - alpha) + samples[i] * alpha;
        }

        float maxAbs = 0.0001f;
        for (int i = 0; i < count; i++)
        {
            float a = MathF.Abs(samples[i]);
            if (a > maxAbs) maxAbs = a;
        }
        float scale = Peak / maxAbs;
        for (int i = 0; i < count; i++)
        {
            samples[i] *= scale;
        }

        return samples;
    }

    internal static AudioStreamWav CreateRotorLoopStream(int seed = 47)
    {
        float[] samples = RenderRotorLoop(seed);
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short pcm = (short)MathF.Round(Math.Clamp(samples[i], -1f, 1f) * 32767f);
            data[i * 2] = (byte)(pcm & 0xFF);
            data[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = SampleRate,
            Stereo = false,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward,
            LoopBegin = 0,
            LoopEnd = samples.Length,
            Data = data
        };
    }
}

/// <summary>
/// Manages continuous rotor audio for active helicopters with zero per-frame allocations.
/// Automatically silent in headless mode or when no helicopters are near the camera.
/// </summary>
internal sealed class HelicopterAudioController
{
    private readonly AudioStreamPlayer? _player;
    private float _currentDb = -80f;
    private float _currentPitch = 1.0f;
    private bool _isPlaying;

    internal bool IsEnabled => _player is not null;
    internal bool IsPlaying => _isPlaying;
    internal float CurrentVolumeDb => _currentDb;
    internal float CurrentPitchScale => _currentPitch;

    public HelicopterAudioController(Node parent, bool enabled = true)
    {
        if (!enabled)
            return;

        _player = new AudioStreamPlayer
        {
            Name = "HelicopterRotorAudio",
            Stream = HelicopterSound.CreateRotorLoopStream(),
            VolumeDb = -80f,
            PitchScale = 1.0f
        };
        parent.AddChild(_player);
    }

    public void Update(ClientReplica replica, Vector3 cameraFocus, float cameraSize, double delta)
    {
        if (_player is null)
            return;

        float bestScore = 0f;
        FlightPhase bestPhase = FlightPhase.Ground;

        ReadOnlySpan<OwnUnitObservation> units = replica.OwnUnits;
        for (int i = 0; i < units.Length; i++)
        {
            OwnUnitObservation u = units[i];
            if (!u.HasFlightCapability)
                continue;

            float dx = u.Position.X - cameraFocus.X;
            float dz = u.Position.Y - cameraFocus.Z;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            float audibleRadius = MathF.Max(220f, cameraSize * 1.9f);
            if (dist >= audibleRadius)
                continue;

            float prox = 1f - (dist / audibleRadius);
            float phaseWeight = u.FlightPhase switch
            {
                FlightPhase.Ground => 0.25f,
                FlightPhase.Crashing => 1.1f,
                _ => 1.0f
            };
            float score = prox * prox * phaseWeight;
            if (score > bestScore)
            {
                bestScore = score;
                bestPhase = u.FlightPhase;
            }
        }

        float dt = (float)Math.Clamp(delta, 0.001, 0.2);
        if (bestScore < 0.01f)
        {
            _currentDb = Mathf.MoveToward(_currentDb, -80f, 60f * dt);
            if (_currentDb <= -75f && _isPlaying)
            {
                _player.Stop();
                _isPlaying = false;
            }
            else if (_isPlaying)
            {
                _player.VolumeDb = _currentDb;
            }
            return;
        }

        float targetDb = Mathf.Lerp(-36f, -12f, MathF.Min(1f, bestScore));
        float targetPitch = bestPhase switch
        {
            FlightPhase.Ground => 0.52f,
            FlightPhase.TakingOff => 0.96f,
            FlightPhase.Landing => 0.90f,
            FlightPhase.Crashing => 1.24f,
            _ => 1.0f
        };

        _currentDb = Mathf.MoveToward(_currentDb, targetDb, 40f * dt);
        _currentPitch = Mathf.MoveToward(_currentPitch, targetPitch, 1.5f * dt);

        if (!_isPlaying)
        {
            _player.VolumeDb = _currentDb;
            _player.PitchScale = _currentPitch;
            _player.Play();
            _isPlaying = true;
        }
        else
        {
            if (MathF.Abs(_player.VolumeDb - _currentDb) > 0.15f)
                _player.VolumeDb = _currentDb;
            if (MathF.Abs(_player.PitchScale - _currentPitch) > 0.01f)
                _player.PitchScale = _currentPitch;
        }
    }

    public void Stop()
    {
        if (_player is not null && _isPlaying)
        {
            _player.Stop();
            _isPlaying = false;
            _currentDb = -80f;
        }
    }
}

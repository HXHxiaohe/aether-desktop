using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Aether.Core;
using Aether.Interop;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Aether.Island;

/// <summary>One app currently making sound.</summary>
public sealed record SoundSource(uint Pid, string ExePath, string Name, float Peak);

/// <summary>
/// Default output device: master volume (+ change notifications for the OSD), which apps
/// are audible right now, and loopback capture for the spectrum. Lives on the UI thread.
/// </summary>
public static class AudioService
{
    private static MMDeviceEnumerator? _enum;
    private static MMDevice? _device;
    private static DeviceNotifier? _notifier;
    private static DispatcherTimer? _poll;
    private static WasapiLoopbackCapture? _capture;
    private static int _pollTick;
    private static bool _paused;
    private static readonly Dictionary<uint, DateTime> LastHeard = new();
    private static readonly Dictionary<uint, string?> ExeCache = new();
    private static volatile bool _ignoreNextVolumeEvent;

    public static Spectrum Spectrum { get; } = new(32);
    public static bool SpectrumRunning => _capture is not null;
    public static List<SoundSource> Sources { get; private set; } = new();

    public static float Volume { get; private set; }
    public static bool Muted { get; private set; }

    /// <summary>(volume 0..1, muted) – raised on the UI thread.</summary>
    public static event Action<float, bool>? VolumeChanged;
    public static event Action? SourcesChanged;

    public static void Start()
    {
        try
        {
            _enum = new MMDeviceEnumerator();
            _notifier = new DeviceNotifier();
            _enum.RegisterEndpointNotificationCallback(_notifier);
            OpenDefault();
        }
        catch (Exception ex)
        {
            Log.Error("audio-start", ex);
        }

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        _poll.Tick += (_, _) => PollSources();
        _poll.Start();
    }

    public static void Stop()
    {
        _poll?.Stop();
        StopSpectrum();
        try { if (_enum is not null && _notifier is not null) _enum.UnregisterEndpointNotificationCallback(_notifier); } catch { }
        _device?.Dispose();
        _device = null;
    }

    /// <summary>Stop all polling (fullscreen games).</summary>
    public static void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) StopSpectrum();
        else if (SpectrumUsers.Count > 0) StartSpectrum();
    }

    private static void OpenDefault()
    {
        bool hadSpectrum = _capture is not null;
        StopSpectrum();
        if (_device is not null)
        {
            try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { }
            _device.Dispose();
            _device = null;
        }
        if (_enum is null) return;
        try
        {
            _device = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var ev = _device.AudioEndpointVolume;
            Volume = ev.MasterVolumeLevelScalar;
            Muted = ev.Mute;
            ev.OnVolumeNotification += OnVolume;
        }
        catch (Exception ex)
        {
            Log.Error("audio-open", ex);
        }
        if (hadSpectrum) StartSpectrum();
    }

    private static void OnVolume(AudioVolumeNotificationData data)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            bool changed = Math.Abs(data.MasterVolume - Volume) > 0.001f || data.Muted != Muted;
            Volume = data.MasterVolume;
            Muted = data.Muted;
            if (_ignoreNextVolumeEvent) { _ignoreNextVolumeEvent = false; return; }
            if (changed) VolumeChanged?.Invoke(Volume, Muted);
        });
    }

    public static void SetVolume(float v, bool fromUi = true)
    {
        try
        {
            if (_device is null) return;
            if (fromUi) _ignoreNextVolumeEvent = true;
            _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0f, 1f);
            if (v > 0 && Muted) _device.AudioEndpointVolume.Mute = false;
        }
        catch (Exception ex) { Log.Error("audio-setvol", ex); }
    }

    public static void ToggleMute()
    {
        try { if (_device is not null) _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute; }
        catch (Exception ex) { Log.Error("audio-mute", ex); }
    }

    // ---------- who is making sound ----------
    private static void PollSources()
    {
        if (_paused || _device is null) return;
        try
        {
            var mgr = _device.AudioSessionManager;
            if (++_pollTick % 6 == 1) mgr.RefreshSessions(); // pick up new apps every ~3 s
            var sessions = mgr.Sessions;
            var now = DateTime.Now;
            var own = (uint)Environment.ProcessId;
            var peaks = new Dictionary<uint, float>();
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.IsSystemSoundsSession) continue;
                uint pid = s.GetProcessID;
                if (pid == 0 || pid == own) continue;
                float peak = s.AudioMeterInformation.MasterPeakValue;
                if (peak > 0.008f)
                {
                    LastHeard[pid] = now;
                    peaks[pid] = Math.Max(peaks.GetValueOrDefault(pid), peak);
                }
            }
            // Keep a source for 2 s after it goes quiet, so icons don't flicker between words / songs.
            foreach (var pid in LastHeard.Where(kv => (now - kv.Value).TotalSeconds > 2).Select(kv => kv.Key).ToList())
                LastHeard.Remove(pid);

            var list = new List<SoundSource>();
            foreach (var pid in LastHeard.Keys)
            {
                if (!ExeCache.TryGetValue(pid, out var exe)) ExeCache[pid] = exe = Native.ProcessPath(pid);
                if (exe is null) continue;
                if (list.Any(x => AppsSame(x.ExePath, exe))) continue; // several processes of one app
                list.Add(new SoundSource(pid, exe, FriendlyName(exe), peaks.GetValueOrDefault(pid)));
            }
            list = list.OrderBy(x => x.Name).ToList();

            bool changed = list.Count != Sources.Count || list.Where((x, i) => !AppsSame(x.ExePath, Sources[i].ExePath)).Any();
            Sources = list;
            if (changed) SourcesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Sessions vanish while we read them; just try again next tick.
            if (ex is not COMException and not InvalidCastException) Log.Error("audio-poll", ex);
        }
    }

    private static bool AppsSame(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    public static string FriendlyName(string exe)
    {
        if (Names.TryGetValue(exe, out var n)) return n;
        try { n = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileDescription; } catch { }
        if (string.IsNullOrWhiteSpace(n)) n = System.IO.Path.GetFileNameWithoutExtension(exe);
        Names[exe] = n!;
        return n!;
    }

    // ---------- spectrum ----------
    private static readonly HashSet<object> SpectrumUsers = new();

    /// <summary>Several things (island, music card) may want the spectrum; capture runs while any do.</summary>
    public static void WantSpectrum(object who, bool want)
    {
        if (want) SpectrumUsers.Add(who); else SpectrumUsers.Remove(who);
        if (SpectrumUsers.Count > 0) StartSpectrum(); else StopSpectrum();
    }

    // ---------- per-app volume (mixer card) ----------
    public sealed record MixerApp(string ExePath, string Name, float Volume, bool Muted, uint[] Pids);

    /// <summary>Apps with an audio session on the default device (one entry per exe).</summary>
    public static List<MixerApp> MixerApps()
    {
        var result = new List<MixerApp>();
        if (_device is null) return result;
        try
        {
            var mgr = _device.AudioSessionManager;
            mgr.RefreshSessions();
            var sessions = mgr.Sessions;
            var own = (uint)Environment.ProcessId;
            var byExe = new Dictionary<string, (float vol, bool mute, List<uint> pids)>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.IsSystemSoundsSession) continue;
                uint pid = s.GetProcessID;
                if (pid == 0 || pid == own) continue;
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                if (!ExeCache.TryGetValue(pid, out var exe)) ExeCache[pid] = exe = Native.ProcessPath(pid);
                if (exe is null) continue;
                var v = s.SimpleAudioVolume;
                if (byExe.TryGetValue(exe, out var e)) e.pids.Add(pid);
                else byExe[exe] = (v.Volume, v.Mute, new List<uint> { pid });
            }
            foreach (var (exe, v) in byExe)
                result.Add(new MixerApp(exe, FriendlyName(exe), v.vol, v.mute, v.pids.ToArray()));
        }
        catch (Exception ex)
        {
            if (ex is not COMException and not InvalidCastException) Log.Error("audio-mixer", ex);
        }
        return result.OrderBy(a => a.Name).ToList();
    }

    public static void SetAppVolume(uint[] pids, float volume, bool? mute = null)
    {
        if (_device is null) return;
        try
        {
            var sessions = _device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (!pids.Contains(s.GetProcessID)) continue;
                s.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                if (mute is bool m) s.SimpleAudioVolume.Mute = m;
            }
        }
        catch (Exception ex) { Log.Error("audio-setapp", ex); }
    }

    public static void StartSpectrum()
    {
        if (_capture is not null || _device is null || _paused) return;
        try
        {
            _capture = new WasapiLoopbackCapture(_device);
            var fmt = _capture.WaveFormat;
            Spectrum.SetSampleRate(fmt.SampleRate);
            int channels = fmt.Channels;
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat || (fmt is WaveFormatExtensible && fmt.BitsPerSample == 32);
            _capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0 || !isFloat) return;
                Spectrum.Feed(MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded)), channels);
            };
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
        }
        catch (Exception ex)
        {
            Log.Error("spectrum-start", ex);
            _capture?.Dispose();
            _capture = null;
        }
    }

    public static void StopSpectrum()
    {
        var c = _capture;
        _capture = null;
        if (c is null) return;
        try { c.StopRecording(); } catch { }
        c.Dispose();
    }

    // ---------- default device changes (headphones plugged in, etc.) ----------
    private sealed class DeviceNotifier : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
                Application.Current?.Dispatcher.BeginInvoke(OpenDefault);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

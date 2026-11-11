using System;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Threading;
using Aether.Core;

namespace Aether.Island;

/// <summary>
/// One countdown (start / pause / +1 min / reset) and simple alarms (HH:mm, once or daily).
/// The 1-second timer only runs while a countdown is running; alarms are checked every 15 s.
/// Aether has to be running for either to fire.
/// </summary>
public static class TimerService
{
    public static TimeSpan Total { get; private set; } = TimeSpan.FromMinutes(25);
    public static bool Running { get; private set; }
    public static bool Active => Running || _pausedLeft is not null;
    private static DateTime _endsAt;
    private static TimeSpan? _pausedLeft;

    private static DispatcherTimer? _tick, _alarmTick;
    private static SoundPlayer? _player;
    private static DateTime _stopSoundAt;
    private static string _lastAlarmKey = "";

    /// <summary>Raised every second while running (and on any state change).</summary>
    public static event Action? Changed;

    /// <summary>A countdown finished or an alarm rang. Argument: text to show.</summary>
    public static event Action<string>? Ring;

    public static TimeSpan Remaining =>
        Running ? (_endsAt - DateTime.Now) < TimeSpan.Zero ? TimeSpan.Zero : _endsAt - DateTime.Now
        : _pausedLeft ?? Total;

    public static double Progress => Total.TotalSeconds <= 0 ? 0 : 1 - Remaining.TotalSeconds / Total.TotalSeconds;

    public static void Start()
    {
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
        _alarmTick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _alarmTick.Tick += (_, _) => CheckAlarms();
        _alarmTick.Start();
        Total = TimeSpan.FromMinutes(Math.Clamp(SettingsStore.Current.Island.LastTimerMinutes, 1, 600));
    }

    public static void Stop()
    {
        _tick?.Stop();
        _alarmTick?.Stop();
        StopSound();
    }

    // ---------- countdown ----------
    public static void SetMinutes(int minutes)
    {
        minutes = Math.Clamp(minutes, 1, 600);
        Total = TimeSpan.FromMinutes(minutes);
        SettingsStore.Current.Island.LastTimerMinutes = minutes;
        SettingsStore.Touch();
        Running = false;
        _pausedLeft = null;
        _tick?.Stop();
        Changed?.Invoke();
    }

    public static void StartOrPause()
    {
        if (Running)
        {
            _pausedLeft = Remaining;
            Running = false;
            _tick?.Stop();
        }
        else
        {
            var left = _pausedLeft ?? Total;
            if (left <= TimeSpan.Zero) left = Total;
            _endsAt = DateTime.Now + left;
            _pausedLeft = null;
            Running = true;
            _tick?.Start();
        }
        Changed?.Invoke();
    }

    public static void AddMinute()
    {
        if (Running) _endsAt = _endsAt.AddMinutes(1);
        else if (_pausedLeft is { } p) _pausedLeft = p + TimeSpan.FromMinutes(1);
        else Total += TimeSpan.FromMinutes(1);
        if (Remaining > Total) Total = Remaining;
        Changed?.Invoke();
    }

    public static void Reset()
    {
        Running = false;
        _pausedLeft = null;
        _tick?.Stop();
        StopSound();
        Changed?.Invoke();
    }

    /// <summary>"Remind me again in 5 minutes".</summary>
    public static void Snooze()
    {
        StopSound();
        Total = TimeSpan.FromMinutes(5);
        _endsAt = DateTime.Now.AddMinutes(5);
        _pausedLeft = null;
        Running = true;
        _tick?.Start();
        Changed?.Invoke();
    }

    private static void OnTick()
    {
        if (_player is not null && DateTime.Now >= _stopSoundAt) StopSound();
        if (!Running) { if (_player is null) _tick?.Stop(); return; }
        if (Remaining <= TimeSpan.Zero)
        {
            Running = false;
            _pausedLeft = null;
            PlaySound();
            Changed?.Invoke();
            Ring?.Invoke($"计时结束 · {Fmt(Total)}");
            return;
        }
        Changed?.Invoke();
    }

    // ---------- alarms ----------
    private static void CheckAlarms()
    {
        var now = DateTime.Now;
        var key = now.ToString("yyyyMMddHHmm");
        if (key == _lastAlarmKey) return;
        foreach (var a in SettingsStore.Current.Island.Alarms.Where(a => a.Enabled))
        {
            if (a.Hour != now.Hour || a.Minute != now.Minute) continue;
            _lastAlarmKey = key;
            if (!a.Daily) { a.Enabled = false; SettingsStore.Touch(); }
            PlaySound();
            Changed?.Invoke();
            Ring?.Invoke($"闹钟 · {a.Hour:00}:{a.Minute:00}");
        }
    }

    // ---------- sound ----------
    private static void PlaySound()
    {
        try
        {
            StopSound();
            var media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            var file = new[] { "Alarm01.wav", "Alarm02.wav", "Windows Notify Calendar.wav" }.Select(f => Path.Combine(media, f)).FirstOrDefault(File.Exists);
            if (file is null) { SystemSounds.Exclamation.Play(); return; }
            _player = new SoundPlayer(file);
            _player.PlayLooping();
            _stopSoundAt = DateTime.Now.AddSeconds(20); // never ring forever
            _tick?.Start();
        }
        catch (Exception ex) { Log.Error("timer-sound", ex); }
    }

    public static void StopSound()
    {
        try { _player?.Stop(); _player?.Dispose(); } catch { }
        _player = null;
    }

    public static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}

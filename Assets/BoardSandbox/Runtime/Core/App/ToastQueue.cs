using System;
using System.Collections.Generic;

namespace BoardSandbox.Core.App
{
    public enum ToastLevel
    {
        Info,
        Warning,
        Error
    }

    public sealed class Toast
    {
        private const double FadeSeconds = 0.5;

        public Toast(string text, ToastLevel level, double createdAt, double duration)
        {
            Text = text;
            Level = level;
            CreatedAt = createdAt;
            Duration = duration;
        }

        public string Text { get; }
        public ToastLevel Level { get; }
        public double CreatedAt { get; internal set; }
        public double Duration { get; }

        public bool IsExpired(double now)
        {
            return now - CreatedAt >= Duration;
        }

        // 1 while the toast is fully shown, falling to 0 over its last half second.
        public float Opacity(double now)
        {
            double remaining = Duration - (now - CreatedAt);
            if (remaining <= 0)
                return 0f;
            return remaining >= FadeSeconds ? 1f : (float)(remaining / FadeSeconds);
        }
    }

    // Short messages for the person playing; the newest ones stay, repeats refresh instead of stacking.
    public sealed class ToastQueue
    {
        private readonly List<Toast> _items = new List<Toast>();
        private readonly int _capacity;
        private readonly double _duration;

        public ToastQueue(int capacity = 4, double durationSeconds = 3.5)
        {
            if (capacity < 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (durationSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(durationSeconds));
            _capacity = capacity;
            _duration = durationSeconds;
        }

        public void Add(string text, ToastLevel level, double now)
        {
            if (string.IsNullOrEmpty(text))
                return;
            Prune(now);
            Toast last = _items.Count > 0 ? _items[_items.Count - 1] : null;
            if (last != null && last.Level == level && string.Equals(last.Text, text, StringComparison.Ordinal))
            {
                last.CreatedAt = now;
                return;
            }
            _items.Add(new Toast(text, level, now, _duration));
            while (_items.Count > _capacity)
                _items.RemoveAt(0);
        }

        // The toasts still showing at the given time, oldest first.
        public IReadOnlyList<Toast> Active(double now)
        {
            Prune(now);
            return _items;
        }

        public void Clear()
        {
            _items.Clear();
        }

        private void Prune(double now)
        {
            _items.RemoveAll(t => t.IsExpired(now));
        }
    }
}

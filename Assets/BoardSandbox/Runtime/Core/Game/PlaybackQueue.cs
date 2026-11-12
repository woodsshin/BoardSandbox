using System;
using System.Collections.Generic;

namespace BoardSandbox.Core.Game
{
    // Plays the events of a game into a second ViewModel in order, pausing for every effect, so that a view can
    // show what happened step by step while the game itself is already further ahead.
    public sealed class PlaybackQueue
    {
        public const int MaxWaitingEffects = 48;
        public const double MaxSpeed = 12.0;

        private readonly Queue<ViewEvent> _pending = new Queue<ViewEvent>();
        private int _waitingEffects;
        private double _elapsed;

        public ViewModel Display { get; } = new ViewModel();

        // Raised for every event the moment it takes effect on the display.
        public event Action<ViewEvent> Played;

        // The effect that is running; null between effects.
        public ViewEvent ActiveEffect { get; private set; }

        public float Progress => ActiveEffect == null ? 0f : (float)Math.Min(1.0, _elapsed / ActiveEffect.Seconds);

        public bool IsBusy => ActiveEffect != null || _pending.Count > 0;

        // Effects play faster the more of them are waiting.
        public double Speed => Math.Min(MaxSpeed, 1.0 + 0.5 * _waitingEffects);

        public void Enqueue(ViewEvent e)
        {
            if (e == null)
                return;
            if (e.Kind == ViewEventKind.Reset)
            {
                Drop();
                Play(e);
                return;
            }
            _pending.Enqueue(e);
            if (e.Kind == ViewEventKind.Effect)
                _waitingEffects++;
            if (ActiveEffect == null)
                Pump();
        }

        // Shows the state of another model at once, without playing anything.
        public void Reset(ViewModel source)
        {
            Drop();
            Display.Clear();
            if (source == null)
                return;
            foreach (ViewEvent e in source.Synthesize())
                Display.Apply(e);
        }

        public void Update(double deltaSeconds)
        {
            if (ActiveEffect != null)
            {
                _elapsed += Math.Max(0.0, deltaSeconds) * Speed;
                if (_elapsed < ActiveEffect.Seconds)
                    return;
                ActiveEffect = null;
            }
            Pump();
        }

        // Applies everything that is waiting at once.
        public void Finish()
        {
            ActiveEffect = null;
            while (_pending.Count > 0)
            {
                ViewEvent e = _pending.Dequeue();
                if (e.Kind != ViewEventKind.Effect)
                    Play(e);
            }
            _waitingEffects = 0;
        }

        private void Pump()
        {
            while (ActiveEffect == null && _pending.Count > 0)
            {
                ViewEvent e = _pending.Dequeue();
                if (e.Kind != ViewEventKind.Effect)
                {
                    Play(e);
                    continue;
                }
                _waitingEffects--;
                if (e.Seconds <= 0 || _waitingEffects >= MaxWaitingEffects)
                    continue;
                ActiveEffect = e;
                _elapsed = 0;
            }
        }

        private void Play(ViewEvent e)
        {
            Display.Apply(e);
            Played?.Invoke(e);
        }

        private void Drop()
        {
            _pending.Clear();
            _waitingEffects = 0;
            ActiveEffect = null;
            _elapsed = 0;
        }
    }
}

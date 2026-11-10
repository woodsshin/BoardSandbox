using System;
using System.Threading;
using System.Threading.Tasks;
using BoardSandbox.Core.Mods;
using BoardSandbox.Core.Sim;

namespace BoardSandbox.Core.App
{
    // One background simulation at a time; the owner polls the state from its own thread.
    public sealed class SimulationJob
    {
        private readonly object _gate = new object();
        private CancellationTokenSource _cancel;
        private Task _task;
        private SimulationProgress _progress;
        private SimulationResult _result;
        private bool _running;
        private int _totalGames;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return _running;
            }
        }

        public int TotalGames
        {
            get
            {
                lock (_gate)
                    return _totalGames;
            }
        }

        public SimulationProgress Progress
        {
            get
            {
                lock (_gate)
                    return _progress;
            }
        }

        public SimulationResult LastResult
        {
            get
            {
                lock (_gate)
                    return _result;
            }
        }

        public bool Start(ModPackage mod, SimulationOptions options)
        {
            if (mod == null)
                throw new ArgumentNullException(nameof(mod));
            options = options ?? new SimulationOptions();
            CancellationTokenSource cancel;
            lock (_gate)
            {
                if (_running)
                    return false;
                _running = true;
                _result = null;
                _progress = null;
                _totalGames = Math.Max(1, options.Games);
                cancel = _cancel = new CancellationTokenSource();
            }

            Task task = Task.Run(() =>
            {
                SimulationResult result;
                try
                {
                    result = SimulationRunner.Run(mod, options, p =>
                    {
                        lock (_gate)
                            _progress = p;
                    }, cancel.Token);
                }
                catch (Exception e)
                {
                    result = new SimulationResult { Error = e.Message };
                }
                lock (_gate)
                {
                    _result = result;
                    _running = false;
                }
            });
            lock (_gate)
                _task = task;
            return true;
        }

        public void Cancel()
        {
            lock (_gate)
                _cancel?.Cancel();
        }

        // Waits for the running simulation to finish; true when none is running afterwards.
        public bool Wait(int timeoutMilliseconds)
        {
            Task task;
            lock (_gate)
                task = _task;
            if (task == null)
                return true;
            return task.Wait(timeoutMilliseconds);
        }
    }
}

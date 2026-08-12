using System;
using System.Threading.Tasks;
using Tizen.Applications;

namespace HyperTizen
{
    public enum ServiceState
    {
        Idle,
        Starting,
        Capturing,
        Paused,
        Stopping,
        Error
    }

    public class ServiceStatus
    {
        public ServiceState State { get; set; }
        public long FramesCaptured { get; set; }
        public double AverageFPS { get; set; }
        public int ErrorCount { get; set; }
        public bool IsConnected { get; set; }
        public string LastError { get; set; }
        public DateTime StartTime { get; set; }
    }

    /// <summary>
    /// Control-only TV client. Image acquisition, decoding and HyperHDR frame
    /// submission live exclusively in the PC/WSL source bridge.
    /// </summary>
    internal sealed class HyperionClient
    {
        public ServiceState State { get; private set; } = ServiceState.Idle;

        public HyperionClient()
        {
            Globals.Instance.Enabled = false;
            Preference.Set("enabled", "false");
            Helper.Log.Write(Helper.eLogType.Info,
                "HyperTizen TV service ready (control-only; no image capture code loaded)");
        }

        public Task Start()
        {
            Globals.Instance.Enabled = false;
            Preference.Set("enabled", "false");
            State = ServiceState.Idle;
            Helper.Log.Write(Helper.eLogType.Info,
                "TV capture is not part of this build; use the PC/WSL source bridge");
            return Task.CompletedTask;
        }

        public Task Stop()
        {
            Globals.Instance.Enabled = false;
            Preference.Set("enabled", "false");
            State = ServiceState.Idle;
            return Task.CompletedTask;
        }

        public void Pause()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                "Pause is handled by the external source bridge");
        }

        public void Resume()
        {
            Helper.Log.Write(Helper.eLogType.Info,
                "Resume is handled by the external source bridge");
        }

        public ServiceStatus GetStatus()
        {
            return new ServiceStatus
            {
                State = State,
                FramesCaptured = 0,
                AverageFPS = 0,
                ErrorCount = 0,
                IsConnected = false,
                LastError = null,
                StartTime = default(DateTime)
            };
        }

        public string ActiveCaptureMethod => "None (not compiled)";
        public string SourceAdapter => "External source adapter (PC/WSL)";
    }
}

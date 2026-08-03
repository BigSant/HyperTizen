using System;
using System.Collections.Generic;
using System.Linq;

namespace HyperTizen.Capture
{
    /// <summary>
    /// Keeps an ordered capture chain alive for the whole session. If the active
    /// method later starts failing or returns black overlay frames, the caller can
    /// advance to the next hardware-tested fallback without rebuilding the service.
    /// </summary>
    public sealed class CaptureMethodSelector
    {
        private readonly object _sync = new object();
        private readonly List<ICaptureMethod> _methods;
        private readonly HashSet<ICaptureMethod> _failed = new HashSet<ICaptureMethod>();
        private ICaptureMethod _selected;

        public CaptureMethodSelector()
        {
            _methods = new List<ICaptureMethod>
            {
                new DiagnosisFastCaptureMethod(),
                new DirectVideoCaptureMethod(),
                new FrameBufferCaptureMethod(),
                new T9VideoCaptureMethod(),
                new T9DisplayCaptureMethod(),
                new SecVideoCaptureMethod(),
                new EflScreenMirrorCaptureMethod(),
                new EflScreenshotCaptureMethod(),
                new T8SdkCaptureMethod(),
                new T7SdkCaptureMethod(),
                new PixelSamplingCaptureV2Method(),
                new PixelSamplingCaptureMethod()
            }.OrderByDescending(method => method.Type).ToList();

            Helper.Log.Write(Helper.eLogType.Info,
                "Capture fallback chain: " + string.Join(" -> ", _methods.Select(method => method.Name)));
        }

        public ICaptureMethod SelectBestMethod()
        {
            lock (_sync)
            {
                if (_selected != null)
                    return _selected;
                return SelectFromIndex(0);
            }
        }

        public ICaptureMethod SelectNextMethod(ICaptureMethod failedMethod)
        {
            lock (_sync)
            {
                int start = 0;
                if (failedMethod != null)
                {
                    int failedIndex = _methods.IndexOf(failedMethod);
                    start = failedIndex < 0 ? 0 : failedIndex + 1;
                    _failed.Add(failedMethod);
                    try { failedMethod.Cleanup(); } catch { }
                }
                _selected = null;
                return SelectFromIndex(start);
            }
        }

        private ICaptureMethod SelectFromIndex(int start)
        {
            for (int index = start; index < _methods.Count; index++)
            {
                ICaptureMethod method = _methods[index];
                if (_failed.Contains(method))
                    continue;
                try
                {
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"CaptureMethodSelector: testing {method.Name} ({index + 1}/{_methods.Count})");
                    if (!method.IsAvailable() || !method.Test())
                    {
                        _failed.Add(method);
                        try { method.Cleanup(); } catch { }
                        continue;
                    }
                    _selected = method;
                    Helper.Log.Write(Helper.eLogType.Info,
                        $"CaptureMethodSelector: selected {method.Name}");
                    return method;
                }
                catch (Exception ex)
                {
                    Helper.Log.Write(Helper.eLogType.Warning,
                        $"CaptureMethodSelector: {method.Name} failed: {ex.Message}");
                    _failed.Add(method);
                    try { method.Cleanup(); } catch { }
                }
            }
            Helper.Log.Write(Helper.eLogType.Error,
                "CaptureMethodSelector: fallback chain exhausted");
            return null;
        }

        public ICaptureMethod GetSelectedMethod()
        {
            lock (_sync) { return _selected; }
        }

        public void Reset()
        {
            lock (_sync)
            {
                foreach (ICaptureMethod method in _methods)
                {
                    try { method.Cleanup(); } catch { }
                }
                _failed.Clear();
                _selected = null;
            }
        }
    }
}

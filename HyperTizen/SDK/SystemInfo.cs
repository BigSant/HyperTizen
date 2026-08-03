using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HyperTizen.SDK
{
    public static class SystemInfo
    {
        public static int TizenVersionMajor
        {
            get
            {
                string version;
                Tizen.System.Information.TryGetValue("http://tizen.org/feature/platform.version", out version);
                return int.Parse(version.Split('.')[0]);
            }
        }
        public static int TizenVersionMinor
        {
            get
            {
                string version;
                Tizen.System.Information.TryGetValue("http://tizen.org/feature/platform.version", out version);
                return int.Parse(version.Split('.')[1]);
            }
        }
        public static bool ImageCapture
        {
            get
            {
                bool isSupported;
                Tizen.System.Information.TryGetValue("http://tizen.org/feature/media.image_capture", out isSupported);
                return isSupported;
            }
        }
        public static bool VideoRecording
        {
            get
            {
                bool isSupported;
                Tizen.System.Information.TryGetValue("http://tizen.org/feature/media.video_recording", out isSupported);
                return isSupported;
            }
        }
        public static int ScreenWidth
        {
            get
            {
                int width;
                Tizen.System.Information.TryGetValue("http://tizen.org/feature/screen.width", out width);
                return width;
            }
        }
        public static int ScreenHeight
        {
            get
            {
                int height;
                Tizen.System.Information.TryGetValue("http://tizen.org/feature/screen.height", out height);
                return height;
            }
        }

        public static string ModelName
        {
            get
            {
                string name;
                Tizen.System.Information.TryGetValue("http://tizen.org/system/model_name", out name);
                return name;
            }
        }
    }
}

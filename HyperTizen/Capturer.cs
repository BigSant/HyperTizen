using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;
using Tizen.Applications.Notifications;
using Tizen.System;

namespace HyperTizen
{
    public static class Capturer
    {
        private static Condition _condition;

        private static bool IsTizen7OrHigher
        {
            get
            {
                string version;
                Information.TryGetValue("http://tizen.org/feature/platform.version", out version);
                if (int.Parse(version.Split('.')[0]) >= 7)
                {
                    return true;
                } else
                {
                    return false;
                }
            }
        }

        private static CapturePoint[] _capturedPoints = new CapturePoint[] {
            new CapturePoint(0.21, 0.05),
            new CapturePoint(0.45, 0.05),
            new CapturePoint(0.7, 0.05),
            new CapturePoint(0.93, 0.07),
            new CapturePoint(0.95, 0.275),
            new CapturePoint(0.95, 0.5),
            new CapturePoint(0.95, 0.8),
            new CapturePoint(0.79, 0.95),
            new CapturePoint(0.65, 0.95),
            new CapturePoint(0.35, 0.95),
            new CapturePoint(0.15, 0.95),
            new CapturePoint(0.05, 0.725),
            new CapturePoint(0.05, 0.4),
            new CapturePoint(0.05, 0.2),
            new CapturePoint(0.35, 0.5),
            new CapturePoint(0.65, 0.5)
        };
        
        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cs_ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition(out Condition unknown);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cs_ve_set_rgb_measure_position")]
        private static extern int MeasurePosition(int i, int x, int y);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "cs_ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel(int i, out Color color);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ve_get_rgb_measure_condition")]
        private static extern int MeasureCondition7(out Condition unknown);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ve_set_rgb_measure_position")]
        private static extern int MeasurePosition7(int i, int x, int y);

        [DllImport("/usr/lib/libvideoenhance.so", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ve_get_rgb_measure_pixel")]
        private static extern int MeasurePixel7(int i, out Color color);
        
        public static bool GetCondition()
        {
            int res = -1;
            try
            {
                if (!IsTizen7OrHigher)
                {
                    res = MeasureCondition(out _condition);
                } else
                {
                    res = MeasureCondition7(out _condition);
                }
            } catch
            {
                Notification notification = new Notification
                {
                    Title = "HyperTizen",
                    Content = "Your TV does not support the required functions for HyperTizen.",
                    Count = 1
                };

                NotificationManager.Post(notification);
            }
            if (res < 0)
            {
                return false;
            } else
            {
                return true;
            }
        }

        public static void SetCapturePoints(CapturePoint[] capturePoints)
        {
            _capturedPoints = capturePoints;
        }
        
        public static Color[] GetColors()
        {
            Color[] colorData = new Color[_capturedPoints.Length];
            int[] updatedIndexes = new int[_condition.ScreenCapturePoints];

            int i = 0;
            while (i < _capturedPoints.Length)
            {
                if (_condition.ScreenCapturePoints == 0) break;
                for (int j = 0; j < _condition.ScreenCapturePoints; j++)
                {
                    updatedIndexes[j] = i;
                    int x = (int)(_capturedPoints[i].X * (double)_condition.Width) - _condition.PixelDensityX / 2;
                    int y = (int)(_capturedPoints[i].Y * (double)_condition.Height) - _condition.PixelDensityY / 2;
                    x = (x >= _condition.Width - _condition.PixelDensityX) ? _condition.Width - (_condition.PixelDensityX + 1) : x;
                    y = (y >= _condition.Height - _condition.PixelDensityY) ? (_condition.Height - _condition.PixelDensityY + 1) : y;
                    int res;
                    if (!IsTizen7OrHigher)
                    {
                        res = MeasurePosition(j, x, y);
                    } else
                    {
                        res = MeasurePosition7(j, x, y);
                    }
                  
                    i++;
                    if (res < 0)
                    {
                        // This should not happen, handle it.
                    }
                }

                if (_condition.SleepMS > 0)
                {
                    Thread.Sleep(_condition.SleepMS);
                }
                int k = 0;
                while (k < _condition.ScreenCapturePoints)
                {
                    Color color;

                    int res;

                    if (!IsTizen7OrHigher)
                    {
                        res = MeasurePixel(k, out color);
                    } else
                    {
                        res = MeasurePixel7(k, out color);
                    }

                    if (res < 0)
                    {
                        // This should not happen, handle it.
                    } else
                    {
                        bool invalidColorData = color.R > 1023 || color.G > 1023 || color.B > 1023;

                        if (invalidColorData)
                        {
                            // This should not happen, handle it.
                        } else
                        {
                            colorData[i - _condition.ScreenCapturePoints + k] = color;
                            k++;
                        }
                    }
                }
            }
            return colorData;
        }

        public static string ToImage(Color[] colors)
        {
            using (var image = new SKBitmap(64, 48))
            {
                for (int x = 0; x < 64; x++)
                {
                    Color color = colors[x / 16];
                    SKColor sKColor = ClampColor(color);
                    for (int y = 0; y < 4; y++)
                    {
                        image.SetPixel(x, y, sKColor);
                    }
                }

                for (int x = 0; x < 64; x++)
                {
                    Color color = colors[x / 16 + 7];
                    SKColor sKColor = ClampColor(color);
                    for (int y = 44; y < 48; y++)
                    {
                        image.SetPixel(x, y, sKColor);
                    }
                }

                for (int y = 0; y < 48; y++)
                {
                    Color color = colors[11 + y / 16];
                    SKColor sKColor = ClampColor(color);
                    for (int x = 0; x < 3; x++)
                    {
                        image.SetPixel(x, y, sKColor);
                    }
                }

                for (int y = 0; y < 48; y++)
                {
                    Color color = colors[4 + y / 16];
                    SKColor sKColor = ClampColor(color);
                    for (int x = 61; x < 64; x++)
                    {
                        image.SetPixel(x, y, sKColor);
                    }
                }

                using (var memoryStream = new MemoryStream())
                {
                    using (var data = SKImage.FromBitmap(image).Encode(SKEncodedImageFormat.Png, 100))
                    {
                        data.SaveTo(memoryStream);
                    }
                    byte[] imageBytes = memoryStream.ToArray();
                    string base64String = Convert.ToBase64String(imageBytes);
                    return base64String;
                }
            }
        }

        static SKColor ClampColor(Color color)
        {
            byte r = (byte)Math.Min(color.R, 255);
            byte g = (byte)Math.Min(color.G, 255);
            byte b = (byte)Math.Min(color.B, 255);
            return new SKColor(r, g, b);
        }
    }

    public struct Color
    {
        public int R;
        public int G;
        public int B;
    }

    public struct Condition
    {
        public int ScreenCapturePoints;

        public int PixelDensityX;

        public int PixelDensityY;

        public int SleepMS;

        public int Width;

        public int Height;
    }

    public struct CapturePoint
    {
        public CapturePoint(double x, double y) {
            this.X = x;
            this.Y = y;
        }

        public double X;
        public double Y;
    }
}

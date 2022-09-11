using FFmpeg.AutoGen;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Videcoder
{
    internal static class Utils
    {
        internal static bool HWDeviceIsMatch(AVHWDeviceType deviceType, HWDevice deviceFlag)
        {
            return (deviceFlag & (HWDevice)(1 << (int)deviceType)) != 0;
        }

    }
}

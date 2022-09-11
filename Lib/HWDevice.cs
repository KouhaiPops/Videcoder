using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Videcoder
{
    [Flags]
    internal enum HWDevice
    {
        NONE = 0,
        VDPAU = 1,
        CUDA = 1<<2,
        VAAPI = 1<<3,
        DXVA2 = 1<<4,
        QSV = 1<<5,
        VIDEOTOOLBOX = 1<<6,
        D3D11VA = 1<<7,
        DRM = 1<<8,
        OPENCL = 1<<9,
        MEDIACODEC = 1<<10,
        VULKAN = 1<<11,
        All = -1
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Videcoder
{
    [Flags]
    public enum HWDecoder
    {
        None = 0,
        Android = HWDevice.MEDIACODEC,
        Ios = HWDevice.VIDEOTOOLBOX,
        Intel = HWDevice.QSV,
        Linux = HWDevice.VDPAU | HWDevice.VAAPI | HWDevice.DRM,
        Window = HWDevice.D3D11VA | HWDevice.DXVA2,
        OpenCL = HWDevice.OPENCL,
        Nvidia = HWDevice.CUDA,
        Vulkan = HWDevice.VULKAN
    }
}

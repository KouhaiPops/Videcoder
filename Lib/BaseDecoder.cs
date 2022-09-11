
using FFmpeg.AutoGen;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Videcoder
{
    public unsafe class BaseDecoder : IDisposable
    {
        private const int BUFFER_SIZE = 16384;
        private static readonly avio_alloc_context_read_packet _readPacket = ReadPacket;
        private static readonly avio_alloc_context_seek _ffmpegSeek  = FFmpegSeek;
        private static readonly ConcurrentDictionary<int, BaseDecoder> decoders = new();

        private Stream stream;
        private bool decodedFirstFrame;
        private bool initializeSws = true;
        private ArrayPool<byte> memory = new MemoryManager<byte>();
        private AVPixelFormat hwTransferTarget;
        private AVPixelFormat ffmpegFrameFormat = AVPixelFormat.AV_PIX_FMT_RGB24;
        private PixelFormat pixelFormat = PixelFormat.RGB;
        private bool disposed = false;

        public TimeSpan Position { get; private set; }
        public PixelFormat FrameFormat
        {
            get => pixelFormat;
            set
            {
                var format = value switch
                {
                    PixelFormat.RGB => AVPixelFormat.AV_PIX_FMT_RGB24,
                    PixelFormat.RGBA => AVPixelFormat.AV_PIX_FMT_RGBA,
                    _ => throw new InvalidDataException("Provided pixel format is invalid.")
                };
                if (IsHardwareDecoder &&  (hwTransferTarget == ffmpegFrameFormat || hwTransferTarget == AVPixelFormat.AV_PIX_FMT_NONE))
                {
                    hwTransferTarget = format;
                }
                ffmpegFrameFormat = format;
                pixelFormat = value;
                initializeSws = true;
            }
        }
        public TimeSpan Duration { get; }
        public float FPS { get; }
        public int PixelPadding { get; set; } = 4;
        public int Height { get; private set; }
        public int Width { get; private set; }
        public bool EOF { get; private set; }
        public bool IsHardwareDecoder { get; }


        private AVCodecContext* codecContext;
        private AVFormatContext* formatContext;
        private SwsContext* swsContext;
        private AVPacket* packet;
        private AVFrame* avFrame;
        private byte* ffmpegBuffer;
        private AVIOContext* ioContext;
        private AVCodec* avCodec;
        private AVStream* avStream;
        private float streamTimeBase;

        private List<(AVHWDeviceType deviceType, Pointer<AVCodec> codec)> GetDecoders(AVCodecID codecId, HWDevice targetHWDevice)
        {
            var decoders = new List<(AVHWDeviceType, Pointer<AVCodec> codec)>();
            if(IsHardwareDecoder)
            {
                void* iterator;
                while (true)
                {
                    var codec = ffmpeg.av_codec_iterate(&iterator);
                    if (codec == null)
                        break;
                    if (codec->id != codecId || codec->hw_configs == null)
                    {
                        continue;
                    }

                    if (ffmpeg.av_codec_is_decoder(codec) == 0)
                        continue;
                    foreach (var device in GetHWConfig(codec, targetHWDevice))
                    {
                        decoders.Add(new (device, codec));
                    }
                }
            }
            decoders.Add(new (AVHWDeviceType.AV_HWDEVICE_TYPE_NONE, ffmpeg.avcodec_find_decoder(codecId)));
            return decoders;
        }

        private List<AVHWDeviceType> GetHWConfig(AVCodec* codec, HWDevice targetHWDevice)
        {
            int index = 0;
            var hwDevices = new List<AVHWDeviceType>();
            while (true)
            {
                var cfg = ffmpeg.avcodec_get_hw_config(codec, index++);
                if (cfg == null)
                    break;
                if (cfg->device_type.IsMatching(targetHWDevice))
                    hwDevices.Add(cfg->device_type);
            }
            return hwDevices;
        }

        /// <summary>
        /// Throw a <see cref="DecoderException"/> when <paramref name="result"/> does not equal 0
        /// </summary>
        /// <param name="result">An ffmpeg function call result</param>
  
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfError(int result)
        {
            if(result < 0)
            {
                var buffer = new byte[8096];
                fixed(byte* ptr = buffer)
                {
                    ffmpeg.av_make_error_string(ptr, (ulong)buffer.Length, result);
                    var errorMsg = Encoding.ASCII.GetString(buffer);
                    throw new DecoderException(errorMsg, result);
                }
            }
        }

        public BaseDecoder(Stream stream, HWDecoder hwDecoder = HWDecoder.None)
        {
            if(hwDecoder != HWDecoder.None)
            {
                IsHardwareDecoder = true;
                hwTransferTarget = ffmpegFrameFormat;
            }
            this.stream = stream;
            var hash = GetHashCode();
            decoders.TryAdd(hash, this);
            ffmpegBuffer = (byte*)ffmpeg.av_malloc(BUFFER_SIZE);

            ioContext = ffmpeg.avio_alloc_context(ffmpegBuffer, BUFFER_SIZE, 0, (void*)hash,
                        _readPacket, null, stream.CanSeek ? _ffmpegSeek : null);

            if(ioContext == null)
            {
                throw new DecoderException("Could not allocate an AVIO Context.");
            }

            formatContext = ffmpeg.avformat_alloc_context();
            if(formatContext == null)
            {
                throw new DecoderException("Could not allocate an AVFormat Context.");
            }

            formatContext->pb = ioContext;
            fixed (AVFormatContext** ptr = &formatContext)
            {
                ThrowIfError(ffmpeg.avformat_open_input(ptr, "null", null, null));

            }

            ThrowIfError(ffmpeg.avformat_find_stream_info(formatContext, null));

            var index = ffmpeg.av_find_best_stream(formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            ThrowIfError(index);

            avStream = formatContext->streams[index];

            foreach(var (type, codec) in GetDecoders(avStream->codecpar->codec_id, (HWDevice)hwDecoder))
            {
                avCodec = codec;
                var res = 0;
                if (codecContext != null)
                {
                    fixed (AVCodecContext** codecCtxPtr = &codecContext)
                    {
                        ffmpeg.avcodec_free_context(codecCtxPtr);
                    }
                }
                codecContext = ffmpeg.avcodec_alloc_context3(avCodec);
                if(codecContext == null)
                {
                    fixed (AVCodecContext** codecCtxPtr = &codecContext)
                    {
                        ffmpeg.avcodec_free_context(codecCtxPtr);
                    }
                    throw new DecoderException("Could not allocate an AVCodecContext.");
                }

                ThrowIfError(ffmpeg.avcodec_parameters_to_context(codecContext, avStream->codecpar));
                if(type != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
                {
                    res = ffmpeg.av_hwdevice_ctx_create(&codecContext->hw_device_ctx, type, null, null, 0);
                    if (res != 0)
                        continue;
                }
                res = ffmpeg.avcodec_open2(codecContext, avCodec, null);
                if(res == -ffmpeg.EINVAL)
                {
                    continue;
                }
                IsHardwareDecoder = type != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
                break;
            }

            packet = ffmpeg.av_packet_alloc();
            if(packet == null)
            {
                throw new DecoderException("Could not allocate an AVPacket.");
            }

            avFrame = ffmpeg.av_frame_alloc();
            if(avFrame == null)
            {
                throw new DecoderException("Could not allocate an AVFrame.");
            }

            streamTimeBase = avStream->time_base.num / (float)avStream->time_base.den;
            FPS = avStream->avg_frame_rate.num / avStream->avg_frame_rate.den;
            Duration = TimeSpan.FromMilliseconds(avStream->duration * streamTimeBase * 1000);
        }

        /// <summary>
        /// Open a <see cref="BaseDecoder"/> over the provided array
        /// </summary>
        /// <param name="data"></param>
        public BaseDecoder(byte[] data, HWDecoder hwDecoder = HWDecoder.None) : this(new MemoryStream(data), hwDecoder)
        { }
        
        /// <summary>
        /// Open a <see cref="FileStream"/> from the provided <paramref name="path"/> and return a decoder over the stream.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static BaseDecoder File(string path, HWDecoder hwDecoder = HWDecoder.None)
        {
            return new BaseDecoder(System.IO.File.OpenRead(path), hwDecoder);
        }

        /// <summary>
        /// Open a <see cref="Stream"/> from the provided <paramref name="url"/> and return a decoder over the stream.
        /// </summary>
        /// <param name="url"></param>
        /// <returns></returns>
        public static BaseDecoder Url(string url, HWDecoder hwDecoder = HWDecoder.None)
        {
            var client = new HttpClient();
            var _stream = client.GetStreamAsync(url);
            _stream.Wait();
            return new BaseDecoder(_stream.Result, hwDecoder);
        }

        /// <summary>
        /// Add an array pool to be used by the decoder.<br/>
        /// By default the decoder internally creates a <see cref="MemoryManager{T}"/> instance.
        /// </summary>
        /// <param name="arrayPool"></param>
        /// <returns></returns>
        public BaseDecoder ConfigureArrayPool(ArrayPool<byte> arrayPool)
        {
            memory = arrayPool;
            return this;
        }
        /// <summary>
        /// Seek to the timestamp
        /// </summary>
        /// <param name="timestamp"></param>
        public virtual void Seek(TimeSpan timestamp)
        {
            Seek((float)timestamp.TotalMilliseconds);
        }

        /// <summary>
        /// Seek to milliseconds timestamp.
        /// </summary>
        /// <param name="timestamp">Timestamp in milliseconds</param>
        public virtual void Seek(float timestamp)
        {
            if (!stream.CanSeek)
                throw new NotSupportedException("Cannot seek current stream");
            ffmpeg.av_seek_frame(formatContext, avStream->index, (long)(timestamp / streamTimeBase / 1000), 1);
        }

        /// <summary>
        /// Get the next frame from the decoder
        /// </summary>
        /// <returns>A <see cref="Frame"/> if it was decoded, otherwise <seealso cref="null"/> </returns>
        public virtual Frame? GetNextFrame()
        {
            while (!EOF)
            {
                if (packet->data == null)
                {
                    var res = ffmpeg.av_read_frame(formatContext, packet);
                    if(res == ffmpeg.AVERROR_EOF)
                    {
                        EOF = true;
                        Dispose();
                        return null;
                    }
                    ThrowIfError(res);
                    if (packet->stream_index != avStream->index)
                    {
                        ffmpeg.av_packet_unref(packet);
                        continue;
                    }
                }
                var frame = SendPacket();
                if (!frame.data.IsEmpty)
                {
                    decodedFirstFrame = true;
                    Height = frame.height;
                    Width = frame.width;
                    return frame;
                }
            }
            return null;
        }

        private static int ReadPacket(void* opaque, byte* bufferPtr, int bufferSize)
        {
            var decoder = decoders[(int)opaque];
            var span = new Span<byte>(bufferPtr, bufferSize);
            return decoder.stream.Read(span);
        }
        
        private Frame SendPacket()
        {
            var res = ffmpeg.avcodec_send_packet(codecContext, packet);
            Frame frame = default;
            if (res == 0 || res == -ffmpeg.EAGAIN)
            {
                if (res == 0)
                {
                    ffmpeg.av_packet_unref(packet);
                }
                frame = DecodeFrame(avFrame);
                ffmpeg.av_frame_unref(avFrame);
            }
            return frame;
        }
        private Frame DecodeFrame(AVFrame* frame)
        {
            var res = ffmpeg.avcodec_receive_frame(codecContext, frame);
            // Supress errors?
            if (res == -ffmpeg.EAGAIN)
            {
                return default;
            }
            ThrowIfError(res);


            var height = frame->height;
            var width = frame->width;


            int pixelCount = FrameFormat switch
            {
                PixelFormat.RGB => 3,
                PixelFormat.RGBA => 4,
                _ => throw new InvalidDataException("Provided format is invalid."),
            };
            var paddingPerRow = PixelPadding != 0 ? (frame->width * pixelCount) % PixelPadding : 0;
            var bufferSize = (frame->height * frame->width * pixelCount) + (frame->height * paddingPerRow);
            var rentedArr = memory.Rent(bufferSize);
            var dstStride = new int[] { (frame->width * pixelCount) + paddingPerRow };

            if (!IsHardwareDecoder)
            {
               ReformatFramePixelFormat(frame, ref rentedArr, dstStride);
            }
            else
            {
                if(!decodedFirstFrame)
                {
                    SetHWTransferTarget(frame->hw_frames_ctx);
                }

                var dstFrame = ffmpeg.av_frame_alloc();
                dstFrame->format = (int)hwTransferTarget;
                var _res = ffmpeg.av_hwframe_transfer_data(dstFrame, frame, 0);
                if(hwTransferTarget != ffmpegFrameFormat)
                {
                    ReformatFramePixelFormat(dstFrame, ref rentedArr, dstStride);
                }
                else
                {
                    Marshal.Copy((IntPtr)dstFrame->data[0], rentedArr, 0, dstFrame->height * dstFrame->width * pixelCount);
                }
                ffmpeg.av_frame_free(&dstFrame);
            }

            var frameTimestamp = frame->pts;
            var lastFrameTimestamp = (frameTimestamp - avStream->start_time) * streamTimeBase * 1000;
            Position = TimeSpan.FromMilliseconds(lastFrameTimestamp);
            return new Frame(rentedArr, lastFrameTimestamp, height, width);
        }

        private void ReformatFramePixelFormat(AVFrame* frame, ref byte[] dst, int[] dstStride)
        {
            if (initializeSws)
            {
                if(swsContext != null)
                {
                    ffmpeg.sws_freeContext(swsContext);
                }
                swsContext = ffmpeg.sws_getContext(frame->width, frame->height, (AVPixelFormat)frame->format, frame->width, frame->height, ffmpegFrameFormat, 0, null, null, null);
                initializeSws = false;
            }


            fixed (byte* dstPtr = dst)
            {
                var dstPtrArray = new byte*[] { dstPtr };
                var res = ffmpeg.sws_scale(swsContext, frame->data, frame->linesize, 0, frame->height, dstPtrArray, dstStride);
            }
        }
        private void SetHWTransferTarget(AVBufferRef* hwCtx)
        {
            AVPixelFormat* pixelFormats;
            ffmpeg.av_hwframe_transfer_get_formats(hwCtx, AVHWFrameTransferDirection.AV_HWFRAME_TRANSFER_DIRECTION_FROM, &pixelFormats, 0);
            hwTransferTarget = *pixelFormats;
            var formats = new List<AVPixelFormat>();
            while((*pixelFormats) != AVPixelFormat.AV_PIX_FMT_NONE)
            {
                if(*pixelFormats == ffmpegFrameFormat)
                {
                    hwTransferTarget = *pixelFormats;
                }
                pixelFormats++;
            }
        }
        private static long FFmpegSeek(void* opaque, long offset, int whence)
        {
            var decoder = decoders[(int)opaque];
            if (!decoder.stream.CanSeek)
            {
                throw new InvalidOperationException("Provided stream cannot be seeked");
            }
            if (whence == 0x10000)
                return decoder.stream.Length;
            return decoder.stream.Seek(offset, (SeekOrigin)whence);
        }
        
        public void Dispose()
        {
            if(!disposed)
            {
                GC.SuppressFinalize(this);
                stream.Dispose();
                ffmpeg.av_freep(ffmpegBuffer);
                fixed(AVPacket** packetPtr = &packet)
                {
                    ffmpeg.av_packet_free(packetPtr);
                }
                fixed(AVCodecContext** codecCtxPtr = &codecContext)
                {
                    ffmpeg.avcodec_free_context(codecCtxPtr);
                }
                fixed(AVIOContext** ioCtxPtr = &ioContext)
                {
                    ffmpeg.avio_context_free(ioCtxPtr);
                }
                ffmpeg.avformat_free_context(formatContext);
                ffmpeg.sws_freeContext(swsContext);
                memory = null;
                disposed = true;
            }
        }
    }
}

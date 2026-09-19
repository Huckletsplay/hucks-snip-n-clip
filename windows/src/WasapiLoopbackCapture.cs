using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace QSnipAndClip
{
    internal sealed class PcmAudioFormat
    {
        public PcmAudioFormat(int sampleRate, int channels, int bitsPerSample)
        {
            if (sampleRate <= 0 || channels <= 0 || bitsPerSample <= 0 || (bitsPerSample % 8) != 0)
            {
                throw new ArgumentOutOfRangeException("sampleRate");
            }

            this.SampleRate = sampleRate;
            this.Channels = channels;
            this.BitsPerSample = bitsPerSample;
        }

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BitsPerSample { get; private set; }
        public int BlockAlign { get { return this.Channels * (this.BitsPerSample / 8); } }
        public int AverageBytesPerSecond { get { return this.SampleRate * this.BlockAlign; } }
    }

    internal sealed class PcmSourceFormat
    {
        private static readonly Guid PcmSubType = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid FloatSubType = new Guid("00000003-0000-0010-8000-00aa00389b71");

        private PcmSourceFormat()
        {
        }

        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public int BitsPerSample { get; private set; }
        public int BlockAlign { get; private set; }
        public bool IsFloat { get; private set; }

        public string Description
        {
            get
            {
                return this.SampleRate + " Hz, " + this.Channels + " channels, "
                    + this.BitsPerSample + "-bit " + (this.IsFloat ? "float" : "PCM")
                    + ", block " + this.BlockAlign;
            }
        }

        public static PcmSourceFormat FromPointer(IntPtr pointer)
        {
            WaveFormatEx wave = (WaveFormatEx)Marshal.PtrToStructure(pointer, typeof(WaveFormatEx));
            bool isFloat;
            if (wave.FormatTag == 1)
            {
                isFloat = false;
            }
            else if (wave.FormatTag == 3)
            {
                isFloat = true;
            }
            else if (wave.FormatTag == 0xfffe && wave.ExtraSize >= 22)
            {
                Guid subType = (Guid)Marshal.PtrToStructure(IntPtr.Add(pointer, 24), typeof(Guid));
                if (subType == PcmSubType)
                {
                    isFloat = false;
                }
                else if (subType == FloatSubType)
                {
                    isFloat = true;
                }
                else
                {
                    throw new NotSupportedException("The default microphone uses an unsupported Windows audio format.");
                }
            }
            else
            {
                throw new NotSupportedException("The default microphone uses an unsupported Windows audio format.");
            }

            if (wave.Channels < 1 || wave.SamplesPerSecond < 1 || wave.BlockAlign < 1)
            {
                throw new NotSupportedException("The default microphone reported an invalid Windows audio format.");
            }

            return new PcmSourceFormat
            {
                SampleRate = (int)wave.SamplesPerSecond,
                Channels = wave.Channels,
                BitsPerSample = wave.BitsPerSample,
                BlockAlign = wave.BlockAlign,
                IsFloat = isFloat
            };
        }

        public static PcmSourceFormat CreatePcm(PcmAudioFormat format)
        {
            return new PcmSourceFormat
            {
                SampleRate = format.SampleRate,
                Channels = format.Channels,
                BitsPerSample = format.BitsPerSample,
                BlockAlign = format.BlockAlign,
                IsFloat = false
            };
        }
    }

    internal sealed class PcmAudioConverter
    {
        private readonly PcmSourceFormat sourceFormat;
        private readonly PcmAudioFormat targetFormat;
        private long resampleAccumulator;

        public PcmAudioConverter(PcmSourceFormat sourceFormat, PcmAudioFormat targetFormat)
        {
            this.sourceFormat = sourceFormat;
            this.targetFormat = targetFormat;
        }

        public void AddConverted(byte[] source, int frameCount, PcmSampleQueue destination)
        {
            int maximumOutputFrames = checked(
                (int)Math.Ceiling((double)frameCount * this.targetFormat.SampleRate / this.sourceFormat.SampleRate) + 2);
            byte[] output = new byte[maximumOutputFrames * this.targetFormat.BlockAlign];
            int outputFrames = 0;

            for (int frame = 0; frame < frameCount; frame++)
            {
                short left = ReadSample(source, frame, 0);
                short right = this.sourceFormat.Channels == 1
                    ? left
                    : ReadSample(source, frame, 1);

                this.resampleAccumulator += this.targetFormat.SampleRate;
                while (this.resampleAccumulator >= this.sourceFormat.SampleRate)
                {
                    int offset = outputFrames * this.targetFormat.BlockAlign;
                    WriteSample(output, offset, left);
                    WriteSample(output, offset + 2, right);
                    outputFrames++;
                    this.resampleAccumulator -= this.sourceFormat.SampleRate;
                }
            }

            if (outputFrames == 0)
            {
                return;
            }

            if (outputFrames != maximumOutputFrames)
            {
                Array.Resize(ref output, outputFrames * this.targetFormat.BlockAlign);
            }

            destination.AddPcm16(output, outputFrames, this.targetFormat);
        }

        private short ReadSample(byte[] source, int frame, int channel)
        {
            int bytesPerSample = this.sourceFormat.BitsPerSample / 8;
            int offset = (frame * this.sourceFormat.BlockAlign) + (channel * bytesPerSample);
            double normalized;
            if (this.sourceFormat.IsFloat && this.sourceFormat.BitsPerSample == 32)
            {
                normalized = BitConverter.ToSingle(source, offset);
            }
            else if (this.sourceFormat.IsFloat && this.sourceFormat.BitsPerSample == 64)
            {
                normalized = BitConverter.ToDouble(source, offset);
            }
            else if (!this.sourceFormat.IsFloat && this.sourceFormat.BitsPerSample == 16)
            {
                return BitConverter.ToInt16(source, offset);
            }
            else if (!this.sourceFormat.IsFloat && this.sourceFormat.BitsPerSample == 24)
            {
                int value = source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16);
                if ((value & 0x800000) != 0)
                {
                    value |= unchecked((int)0xff000000);
                }

                normalized = value / 8388608.0;
            }
            else if (!this.sourceFormat.IsFloat && this.sourceFormat.BitsPerSample == 32)
            {
                normalized = BitConverter.ToInt32(source, offset) / 2147483648.0;
            }
            else
            {
                throw new NotSupportedException("The default microphone sample format is not supported.");
            }

            if (normalized >= 1.0)
            {
                return Int16.MaxValue;
            }

            if (normalized <= -1.0)
            {
                return Int16.MinValue;
            }

            return (short)Math.Round(normalized * Int16.MaxValue);
        }

        private static void WriteSample(byte[] output, int offset, short sample)
        {
            output[offset] = (byte)(sample & 0xff);
            output[offset + 1] = (byte)((sample >> 8) & 0xff);
        }
    }

    internal class WasapiAudioCapture : IDisposable
    {
        private const int CLSCTX_ALL = 23;
        private const int E_RENDER = 0;
        private const int E_CAPTURE = 1;
        private const int E_CONSOLE = 0;
        private const int E_COMMUNICATIONS = 2;
        private const int AUDCLNT_SHAREMODE_SHARED = 0;
        private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x00000002;
        private const long RequestedBufferDuration = 10000000L;
        private const long MicrophoneBufferDuration = 50000000L;

        private static readonly Guid IID_IAudioClient = new Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("c8adbd64-e71e-48a0-a4de-185c395cd317");

        private IMMDeviceEnumerator deviceEnumerator;
        private IMMDevice device;
        private IAudioClient audioClient;
        private IAudioCaptureClient captureClient;
        private readonly string sourceName;
        private PcmSourceFormat sourceFormat;
        private PcmAudioConverter converter;
        private EventWaitHandle captureReadyEvent;
        private bool started;

        protected WasapiAudioCapture(bool loopback, string sourceName)
            : this(loopback, sourceName, null) { }

        protected WasapiAudioCapture(bool loopback, string sourceName, string deviceId)
        {
            this.sourceName = sourceName;
            this.Format = new PcmAudioFormat(48000, 2, 16);
            IntPtr formatPointer = IntPtr.Zero;

            try
            {
                this.deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (!loopback && !String.IsNullOrEmpty(deviceId))
                {
                    int status = this.deviceEnumerator.GetDevice(deviceId, out this.device);
                    int state = 0;
                    if (status < 0 || this.device == null || this.device.GetState(out state) < 0 || state != 1)
                    {
                        Release(this.device); this.device = null; UsedDefaultFallback = true;
                    }
                }
                if (this.device == null) Check(
                    this.deviceEnumerator.GetDefaultAudioEndpoint(
                        loopback ? E_RENDER : E_CAPTURE,
                        loopback ? E_CONSOLE : E_COMMUNICATIONS,
                        out this.device),
                    "Windows could not find the default " + this.sourceName + " device");

                object audioClientObject;
                Guid audioClientId = IID_IAudioClient;
                Check(
                    this.device.Activate(ref audioClientId, CLSCTX_ALL, IntPtr.Zero, out audioClientObject),
                    "Windows could not open the " + this.sourceName + " device");
                this.audioClient = (IAudioClient)audioClientObject;

                uint flags;
                if (loopback)
                {
                    WaveFormatEx waveFormat = new WaveFormatEx();
                    waveFormat.FormatTag = 1;
                    waveFormat.Channels = (ushort)this.Format.Channels;
                    waveFormat.SamplesPerSecond = (uint)this.Format.SampleRate;
                    waveFormat.AverageBytesPerSecond = (uint)this.Format.AverageBytesPerSecond;
                    waveFormat.BlockAlign = (ushort)this.Format.BlockAlign;
                    waveFormat.BitsPerSample = (ushort)this.Format.BitsPerSample;
                    waveFormat.ExtraSize = 0;

                    formatPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WaveFormatEx)));
                    Marshal.StructureToPtr(waveFormat, formatPointer, false);
                    this.sourceFormat = PcmSourceFormat.CreatePcm(this.Format);
                    flags = AUDCLNT_STREAMFLAGS_LOOPBACK
                        | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                        | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                }
                else
                {
                    Check(
                        this.audioClient.GetMixFormat(out formatPointer),
                        "Windows could not read the default microphone format");
                    this.sourceFormat = PcmSourceFormat.FromPointer(formatPointer);
                    flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
                }

                this.converter = new PcmAudioConverter(this.sourceFormat, this.Format);
                Check(
                    this.audioClient.Initialize(
                        AUDCLNT_SHAREMODE_SHARED,
                        flags,
                        loopback ? RequestedBufferDuration : MicrophoneBufferDuration,
                        0,
                        formatPointer,
                        IntPtr.Zero),
                    "Windows could not initialize " + this.sourceName + " capture ("
                    + this.sourceFormat.Description + ")");

                object captureClientObject;
                Guid captureClientId = IID_IAudioCaptureClient;
                Check(
                    this.audioClient.GetService(ref captureClientId, out captureClientObject),
                    "Windows could not create the " + this.sourceName + " capture service");
                this.captureClient = (IAudioCaptureClient)captureClientObject;

                if (!loopback)
                {
                    this.captureReadyEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                    Check(
                        this.audioClient.SetEventHandle(this.captureReadyEvent.SafeWaitHandle.DangerousGetHandle()),
                        "Windows could not create the microphone capture signal");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                if (formatPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(formatPointer);
                }
            }
        }

        public PcmAudioFormat Format { get; private set; }
        public bool UsedDefaultFallback { get; private set; }

        public void Start()
        {
            if (this.started)
            {
                return;
            }

            Check(this.audioClient.Start(), this.sourceName + " capture could not start");
            this.started = true;
        }

        public void DrainTo(PcmSampleQueue destination)
        {
            if (!this.started || destination == null)
            {
                return;
            }

            uint packetFrames;
            Check(
                this.captureClient.GetNextPacketSize(out packetFrames),
                this.sourceName + " packet size could not be read");
            while (packetFrames > 0)
            {
                IntPtr source;
                uint frames;
                int flags;
                ulong devicePosition;
                ulong qpcPosition;
                Check(
                    this.captureClient.GetBuffer(
                        out source,
                        out frames,
                        out flags,
                        out devicePosition,
                        out qpcPosition),
                    this.sourceName + " data could not be read");

                try
                {
                    int byteCount = checked((int)frames * this.sourceFormat.BlockAlign);
                    byte[] data = new byte[byteCount];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && source != IntPtr.Zero)
                    {
                        Marshal.Copy(source, data, 0, byteCount);
                    }

                    this.converter.AddConverted(data, (int)frames, destination);
                }
                finally
                {
                    Check(
                        this.captureClient.ReleaseBuffer(frames),
                        this.sourceName + " data could not be released");
                }

                Check(
                    this.captureClient.GetNextPacketSize(out packetFrames),
                    this.sourceName + " packet size could not be read");
            }
        }

        public void Dispose()
        {
            if (this.started && this.audioClient != null)
            {
                try
                {
                    this.audioClient.Stop();
                }
                catch
                {
                    // Continue releasing native audio resources.
                }

                this.started = false;
            }

            Release(this.captureClient);
            this.captureClient = null;
            Release(this.audioClient);
            this.audioClient = null;
            Release(this.device);
            this.device = null;
            Release(this.deviceEnumerator);
            this.deviceEnumerator = null;
            if (this.captureReadyEvent != null)
            {
                this.captureReadyEvent.Dispose();
                this.captureReadyEvent = null;
            }
        }

        private static void Check(int result, string message)
        {
            if (result < 0)
            {
                throw new COMException(message + " (0x" + result.ToString("X8") + ").", result);
            }
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.ReleaseComObject(value);
            }
        }
    }

    internal sealed class WasapiLoopbackCapture : WasapiAudioCapture
    {
        public WasapiLoopbackCapture()
            : base(true, "computer audio")
        {
        }
    }

    internal sealed class WasapiMicrophoneCapture : WasapiAudioCapture
    {
        public WasapiMicrophoneCapture()
            : base(false, "microphone")
        {
        }
        public WasapiMicrophoneCapture(string deviceId) : base(false, "microphone", deviceId) { }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [ComImport, Guid("bcde0395-e52f-467c-8e3d-c4579291692e")]
    internal class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(
            ref Guid interfaceId,
            int classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IAudioPropertyStore properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(
            int shareMode,
            uint streamFlags,
            long bufferDuration,
            long periodicity,
            IntPtr format,
            IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("c8adbd64-e71e-48a0-a4de-185c395cd317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(
            out IntPtr data,
            out uint frames,
            out int flags,
            out ulong devicePosition,
            out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}

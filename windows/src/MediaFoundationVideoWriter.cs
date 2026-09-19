using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace QSnipAndClip
{
    internal sealed class MediaFoundationVideoWriter : IDisposable
    {
        private const int MF_VERSION = 0x00020070;
        private const int MFSTARTUP_FULL = 0;
        private const int MFVideoInterlace_Progressive = 2;

        private static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_AVG_BITRATE = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        private static readonly Guid MF_MT_INTERLACE_MODE = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        private static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_MT_FRAME_RATE = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new Guid("c9173739-5e56-461c-b713-46fb995cb95f");
        private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new Guid("7632f0e6-9538-4d61-acda-ea29c8c14456");
        private static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new Guid("a634a91c-822b-41b9-a494-4de4643612b0");
        private static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_H264 = new Guid("34363248-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFAudioFormat_AAC = new Guid("00001610-0000-0010-8000-00aa00389b71");
        private static readonly Guid MFAudioFormat_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");

        private readonly int width;
        private readonly int height;
        private readonly long frameDuration;
        private readonly PcmAudioFormat audioFormat;
        private readonly int audioTrackCount;
        private IMFSinkWriter sinkWriter;
        private int streamIndex;
        private int[] audioStreamIndexes;
        private long nextSampleTime;
        private bool mediaFoundationStarted;
        private bool finalized;

        public MediaFoundationVideoWriter(string path, int width, int height, int framesPerSecond, int bitsPerSecond)
            : this(path, width, height, framesPerSecond, bitsPerSecond, null)
        {
        }

        public MediaFoundationVideoWriter(
            string path,
            int width,
            int height,
            int framesPerSecond,
            int bitsPerSecond,
            PcmAudioFormat audioFormat)
            : this(path, width, height, framesPerSecond, bitsPerSecond, audioFormat, 1)
        {
        }

        // Windows can carry more than one AAC track in the same MP4. Separate Computer and
        // Microphone tracks stay independently editable instead of being flattened into one mix.
        public MediaFoundationVideoWriter(
            string path,
            int width,
            int height,
            int framesPerSecond,
            int bitsPerSecond,
            PcmAudioFormat audioFormat,
            int audioTrackCount)
        {
            if (audioFormat != null && (audioTrackCount < 1 || audioTrackCount > 2))
            {
                throw new ArgumentOutOfRangeException("audioTrackCount");
            }

            if (String.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An output path is required.", "path");
            }

            if (width <= 0 || height <= 0 || (width % 2) != 0 || (height % 2) != 0)
            {
                throw new ArgumentOutOfRangeException("width", "H.264 video dimensions must be positive even numbers.");
            }

            if (framesPerSecond <= 0)
            {
                throw new ArgumentOutOfRangeException("framesPerSecond");
            }

            this.width = width;
            this.height = height;
            this.frameDuration = 10000000L / framesPerSecond;
            this.audioFormat = audioFormat;
            this.audioTrackCount = audioFormat == null ? 0 : audioTrackCount;
            this.audioStreamIndexes = new int[0];

            try
            {
                Check(MediaFoundationNative.MFStartup(MF_VERSION, MFSTARTUP_FULL), "Media Foundation could not start");
                this.mediaFoundationStarted = true;
                Initialize(path, framesPerSecond, bitsPerSecond);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void WriteFrame(Bitmap frame)
        {
            WriteFrame(frame, this.nextSampleTime);
        }

        public void WriteFrame(Bitmap frame, long sampleTime)
        {
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }

            if (this.finalized || this.sinkWriter == null)
            {
                throw new InvalidOperationException("The video writer is closed.");
            }

            if (frame.Width != this.width || frame.Height != this.height)
            {
                throw new ArgumentException("The frame dimensions do not match the video.", "frame");
            }

            int bufferLength = checked(this.width * this.height * 4);
            IMFMediaBuffer buffer = null;
            IMFSample sample = null;
            BitmapData data = null;

            try
            {
                Check(MediaFoundationNative.MFCreateMemoryBuffer(bufferLength, out buffer), "A video buffer could not be created");

                IntPtr destination;
                int maximumLength;
                int currentLength;
                Check(buffer.Lock(out destination, out maximumLength, out currentLength), "The video buffer could not be locked");
                try
                {
                    Rectangle rectangle = new Rectangle(0, 0, frame.Width, frame.Height);
                    data = frame.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    CopyFrameRows(data, destination, this.width, this.height);
                }
                finally
                {
                    if (data != null)
                    {
                        frame.UnlockBits(data);
                    }

                    Check(buffer.Unlock(), "The video buffer could not be unlocked");
                }

                Check(buffer.SetCurrentLength(bufferLength), "The video buffer length could not be set");
                Check(MediaFoundationNative.MFCreateSample(out sample), "A video sample could not be created");
                Check(sample.AddBuffer(buffer), "The video buffer could not be attached to its sample");
                Check(sample.SetSampleTime(sampleTime), "The video timestamp could not be set");
                Check(sample.SetSampleDuration(this.frameDuration), "The video frame duration could not be set");
                Check(this.sinkWriter.WriteSample(this.streamIndex, sample), "The video frame could not be encoded");
                this.nextSampleTime = sampleTime + this.frameDuration;
            }
            finally
            {
                Release(sample);
                Release(buffer);
            }
        }

        public int AudioTrackCount
        {
            get { return this.audioTrackCount; }
        }

        public void WriteAudio(byte[] pcmData, int frameCount, long sampleTime)
        {
            WriteAudio(0, pcmData, frameCount, sampleTime);
        }

        public void WriteAudio(int track, byte[] pcmData, int frameCount, long sampleTime)
        {
            if (this.audioFormat == null || track < 0 || track >= this.audioStreamIndexes.Length)
            {
                throw new InvalidOperationException("This MP4 writer was not configured for that audio track.");
            }

            if (this.finalized || this.sinkWriter == null)
            {
                throw new InvalidOperationException("The video writer is closed.");
            }

            if (pcmData == null)
            {
                throw new ArgumentNullException("pcmData");
            }

            int expectedLength = checked(frameCount * this.audioFormat.BlockAlign);
            if (frameCount <= 0 || pcmData.Length != expectedLength)
            {
                throw new ArgumentException("The PCM audio length does not match its frame count.", "pcmData");
            }

            IMFMediaBuffer buffer = null;
            IMFSample sample = null;
            try
            {
                Check(MediaFoundationNative.MFCreateMemoryBuffer(expectedLength, out buffer), "An audio buffer could not be created");
                IntPtr destination;
                int maximumLength;
                int currentLength;
                Check(buffer.Lock(out destination, out maximumLength, out currentLength), "The audio buffer could not be locked");
                try
                {
                    Marshal.Copy(pcmData, 0, destination, expectedLength);
                }
                finally
                {
                    Check(buffer.Unlock(), "The audio buffer could not be unlocked");
                }

                Check(buffer.SetCurrentLength(expectedLength), "The audio buffer length could not be set");
                Check(MediaFoundationNative.MFCreateSample(out sample), "An audio sample could not be created");
                Check(sample.AddBuffer(buffer), "The audio buffer could not be attached to its sample");
                Check(sample.SetSampleTime(sampleTime), "The audio timestamp could not be set");
                long duration = frameCount * 10000000L / this.audioFormat.SampleRate;
                Check(sample.SetSampleDuration(duration), "The audio duration could not be set");
                Check(this.sinkWriter.WriteSample(this.audioStreamIndexes[track], sample), "The audio sample could not be encoded");
            }
            finally
            {
                Release(sample);
                Release(buffer);
            }
        }

        public void FinalizeVideo()
        {
            if (this.finalized)
            {
                return;
            }

            this.finalized = true;
            if (this.sinkWriter != null)
            {
                Check(this.sinkWriter.FinalizeWriter(), "The MP4 file could not be finalized");
            }
        }

        public void Dispose()
        {
            if (!this.finalized && this.sinkWriter != null)
            {
                try
                {
                    FinalizeVideo();
                }
                catch
                {
                    // Preserve the original recording error while still releasing native resources.
                }
            }

            Release(this.sinkWriter);
            this.sinkWriter = null;

            if (this.mediaFoundationStarted)
            {
                MediaFoundationNative.MFShutdown();
                this.mediaFoundationStarted = false;
            }
        }

        private void Initialize(string path, int framesPerSecond, int bitsPerSecond)
        {
            IMFMediaType outputType = null;
            IMFMediaType inputType = null;
            IMFMediaType audioOutputType = null;
            IMFMediaType audioInputType = null;
            IMFAttributes writerAttributes = null;

            try
            {
                Check(MediaFoundationNative.MFCreateAttributes(out writerAttributes, 1), "The MP4 writer settings could not be created");
                SetUInt32(writerAttributes, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
                Check(MediaFoundationNative.MFCreateSinkWriterFromURL(path, IntPtr.Zero, writerAttributes, out this.sinkWriter), "The MP4 writer could not be created");

                Check(MediaFoundationNative.MFCreateMediaType(out outputType), "The output video type could not be created");
                IMFAttributes outputAttributes = (IMFAttributes)outputType;
                SetGuid(outputAttributes, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetGuid(outputAttributes, MF_MT_SUBTYPE, MFVideoFormat_H264);
                SetUInt32(outputAttributes, MF_MT_AVG_BITRATE, bitsPerSecond);
                SetUInt32(outputAttributes, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                SetSize(outputAttributes, MF_MT_FRAME_SIZE, this.width, this.height, "The output size could not be set");
                SetRatio(outputAttributes, MF_MT_FRAME_RATE, framesPerSecond, 1, "The output frame rate could not be set");
                SetRatio(outputAttributes, MF_MT_PIXEL_ASPECT_RATIO, 1, 1, "The pixel ratio could not be set");
                Check(this.sinkWriter.AddStream(outputType, out this.streamIndex), "The H.264 stream could not be added");

                Check(MediaFoundationNative.MFCreateMediaType(out inputType), "The input video type could not be created");
                IMFAttributes inputAttributes = (IMFAttributes)inputType;
                SetGuid(inputAttributes, MF_MT_MAJOR_TYPE, MFMediaType_Video);
                SetGuid(inputAttributes, MF_MT_SUBTYPE, MFVideoFormat_RGB32);
                SetUInt32(inputAttributes, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
                SetUInt32(inputAttributes, MF_MT_DEFAULT_STRIDE, unchecked(this.width * 4));
                SetSize(inputAttributes, MF_MT_FRAME_SIZE, this.width, this.height, "The input size could not be set");
                SetRatio(inputAttributes, MF_MT_FRAME_RATE, framesPerSecond, 1, "The input frame rate could not be set");
                SetRatio(inputAttributes, MF_MT_PIXEL_ASPECT_RATIO, 1, 1, "The input pixel ratio could not be set");
                Check(this.sinkWriter.SetInputMediaType(this.streamIndex, inputType, null), "Windows could not connect RGB screen frames to the H.264 encoder");

                if (this.audioFormat != null)
                {
                    this.audioStreamIndexes = new int[this.audioTrackCount];
                    for (int track = 0; track < this.audioTrackCount; track++)
                    {
                        Release(audioOutputType);
                        Release(audioInputType);
                        audioOutputType = null;
                        audioInputType = null;

                        Check(MediaFoundationNative.MFCreateMediaType(out audioOutputType), "The output audio type could not be created");
                        IMFAttributes audioOutputAttributes = (IMFAttributes)audioOutputType;
                        SetGuid(audioOutputAttributes, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                        SetGuid(audioOutputAttributes, MF_MT_SUBTYPE, MFAudioFormat_AAC);
                        SetUInt32(audioOutputAttributes, MF_MT_AUDIO_NUM_CHANNELS, this.audioFormat.Channels);
                        SetUInt32(audioOutputAttributes, MF_MT_AUDIO_SAMPLES_PER_SECOND, this.audioFormat.SampleRate);
                        SetUInt32(audioOutputAttributes, MF_MT_AUDIO_BITS_PER_SAMPLE, this.audioFormat.BitsPerSample);
                        SetUInt32(audioOutputAttributes, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000);
                        SetUInt32(audioOutputAttributes, MF_MT_AUDIO_BLOCK_ALIGNMENT, 1);
                        SetUInt32(audioOutputAttributes, MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);
                        Check(this.sinkWriter.AddStream(audioOutputType, out this.audioStreamIndexes[track]), "The AAC audio stream could not be added");

                        Check(MediaFoundationNative.MFCreateMediaType(out audioInputType), "The input audio type could not be created");
                        IMFAttributes audioInputAttributes = (IMFAttributes)audioInputType;
                        SetGuid(audioInputAttributes, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
                        SetGuid(audioInputAttributes, MF_MT_SUBTYPE, MFAudioFormat_PCM);
                        SetUInt32(audioInputAttributes, MF_MT_AUDIO_NUM_CHANNELS, this.audioFormat.Channels);
                        SetUInt32(audioInputAttributes, MF_MT_AUDIO_SAMPLES_PER_SECOND, this.audioFormat.SampleRate);
                        SetUInt32(audioInputAttributes, MF_MT_AUDIO_BITS_PER_SAMPLE, this.audioFormat.BitsPerSample);
                        SetUInt32(audioInputAttributes, MF_MT_AUDIO_BLOCK_ALIGNMENT, this.audioFormat.BlockAlign);
                        SetUInt32(audioInputAttributes, MF_MT_AUDIO_AVG_BYTES_PER_SECOND, this.audioFormat.AverageBytesPerSecond);
                        SetUInt32(audioInputAttributes, MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                        Check(
                            this.sinkWriter.SetInputMediaType(this.audioStreamIndexes[track], audioInputType, null),
                            "Windows could not connect captured audio to the AAC encoder");
                    }
                }

                Check(this.sinkWriter.BeginWriting(), "The MP4 writer could not begin");
            }
            finally
            {
                Release(writerAttributes);
                Release(audioInputType);
                Release(audioOutputType);
                Release(inputType);
                Release(outputType);
            }
        }

        internal static void CopyFrameRows(BitmapData source, IntPtr destination, int width, int height)
        {
            // Copy directly between native buffers, retaining signed source stride and row order.
            // Avoid two managed/native transitions and an intermediate managed copy for every row.
            int rowBytes = checked(width * 4);
            Check(MediaFoundationNative.MFCopyImage(
                destination, rowBytes, source.Scan0, source.Stride, rowBytes, height),
                "The captured video frame could not be copied");
        }

        private static void SetSize(IMFAttributes attributes, Guid key, int width, int height, string message)
        {
            long packed = ((long)(uint)width << 32) | (uint)height;
            Check(attributes.SetUINT64(ref key, packed), message);
        }

        private static void SetRatio(IMFAttributes attributes, Guid key, int numerator, int denominator, string message)
        {
            long packed = ((long)(uint)numerator << 32) | (uint)denominator;
            Check(attributes.SetUINT64(ref key, packed), message);
        }

        private static void SetGuid(IMFAttributes attributes, Guid key, Guid value)
        {
            Check(attributes.SetGUID(ref key, ref value), "A media format setting could not be applied");
        }

        private static void SetUInt32(IMFAttributes attributes, Guid key, int value)
        {
            Check(attributes.SetUINT32(ref key, value), "A media format setting could not be applied");
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

    internal static class MediaFoundationNative
    {
        [DllImport("mfplat.dll", ExactSpelling = true)]
        internal static extern int MFCopyImage(
            IntPtr destination, int destinationStride, IntPtr source, int sourceStride,
            int widthInBytes, int lines);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFShutdown();

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMediaType(out IMFMediaType mediaType);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateMemoryBuffer(int maximumLength, out IMFMediaBuffer buffer);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        public static extern int MFCreateSample(out IMFSample sample);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int MFCreateSinkWriterFromURL(
            string outputUrl,
            IntPtr byteStream,
            IMFAttributes attributes,
            out IMFSinkWriter sinkWriter);
    }

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int itemCount);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out int compressed);
        [PreserveSig] int IsEqual(IMFMediaType mediaType, out int flags);
        [PreserveSig] int GetRepresentation(Guid representation, out IntPtr value);
        [PreserveSig] int FreeRepresentation(Guid representation, IntPtr value);
    }

    [ComImport, Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType targetMediaType, out int streamIndex);
        [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IMFAttributes encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
        [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
        [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
        [PreserveSig] int NotifyEndOfSegment(int streamIndex);
        [PreserveSig] int Flush(int streamIndex);
        [PreserveSig] int FinalizeWriter();
        [PreserveSig] int GetServiceForStream(int streamIndex, ref Guid service, ref Guid interfaceId, out IntPtr value);
        [PreserveSig] int GetStatistics(int streamIndex, IntPtr statistics);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, int size, out int blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buffer, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int itemCount);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long sampleTime);
        [PreserveSig] int SetSampleTime(long sampleTime);
        [PreserveSig] int GetSampleDuration(out long sampleDuration);
        [PreserveSig] int SetSampleDuration(long sampleDuration);
        [PreserveSig] int GetBufferCount(out int bufferCount);
        [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int totalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maximumLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int currentLength);
        [PreserveSig] int SetCurrentLength(int currentLength);
        [PreserveSig] int GetMaxLength(out int maximumLength);
    }
}

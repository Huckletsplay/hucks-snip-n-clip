using System;

namespace QSnipAndClip
{
    internal sealed class PcmSampleQueue
    {
        private short[] samples;
        private int readOffset;
        private int count;

        public PcmSampleQueue()
        {
            this.samples = new short[8192];
        }

        public int SampleCount
        {
            get { return this.count; }
        }

        public void Clear()
        {
            this.readOffset = 0;
            this.count = 0;
        }

        public void AddPcm16(byte[] data, int frameCount, PcmAudioFormat format)
        {
            if (data == null)
            {
                throw new ArgumentNullException("data");
            }

            int sampleCount = checked(frameCount * format.Channels);
            if (data.Length != sampleCount * 2)
            {
                throw new ArgumentException("PCM data does not match its frame count.", "data");
            }

            EnsureCapacity(sampleCount);
            int writeOffset = this.readOffset + this.count;
            for (int index = 0; index < sampleCount; index++)
            {
                int byteOffset = index * 2;
                this.samples[writeOffset + index] = (short)(data[byteOffset] | (data[byteOffset + 1] << 8));
            }

            this.count += sampleCount;
        }

        public short TakeOrSilence()
        {
            if (this.count == 0)
            {
                return 0;
            }

            short value = this.samples[this.readOffset++];
            this.count--;
            if (this.count == 0)
            {
                this.readOffset = 0;
            }

            return value;
        }

        private void EnsureCapacity(int additionalSamples)
        {
            int required = checked(this.count + additionalSamples);
            if (this.readOffset + required <= this.samples.Length)
            {
                return;
            }

            if (required <= this.samples.Length)
            {
                Array.Copy(this.samples, this.readOffset, this.samples, 0, this.count);
                this.readOffset = 0;
                return;
            }

            int capacity = this.samples.Length;
            while (capacity < required)
            {
                capacity = checked(capacity * 2);
            }

            short[] expanded = new short[capacity];
            Array.Copy(this.samples, this.readOffset, expanded, 0, this.count);
            this.samples = expanded;
            this.readOffset = 0;
        }
    }

    internal sealed class AudioPeakTracker
    {
        private int peak;

        public void Observe(short sample)
        {
            int magnitude = sample == Int16.MinValue ? 32768 : Math.Abs((int)sample);
            int current = this.peak;
            while (magnitude > current)
            {
                int previous = System.Threading.Interlocked.CompareExchange(
                    ref this.peak,
                    magnitude,
                    current);
                if (previous == current)
                {
                    return;
                }

                current = previous;
            }
        }

        public int TakePeak()
        {
            return System.Threading.Interlocked.Exchange(ref this.peak, 0);
        }
    }

    internal sealed class PcmAudioMixer
    {
        private const int MaximumFramesPerWrite = 4096;

        // Track order is the contract an editor sees: track 1 is Computer, track 2 is Microphone.
        internal const int ComputerTrackIndex = 0;
        internal const int MicrophoneTrackIndex = 1;

        private readonly PcmAudioFormat format;
        private readonly bool hasComputerAudio;
        private readonly bool hasMicrophone;
        private readonly bool separateTracks;
        private readonly int computerGainPercent;
        private readonly int microphoneGainPercent;
        private readonly PcmSampleQueue computerSamples;
        private readonly PcmSampleQueue microphoneSamples;
        private readonly AudioPeakTracker peakTracker;
        private long writtenFrames;

        public PcmAudioMixer(
            PcmAudioFormat format,
            bool hasComputerAudio,
            bool hasMicrophone,
            int computerGainPercent,
            int microphoneGainPercent)
            : this(format, hasComputerAudio, hasMicrophone, computerGainPercent, microphoneGainPercent, false)
        {
        }

        public PcmAudioMixer(
            PcmAudioFormat format,
            bool hasComputerAudio,
            bool hasMicrophone,
            int computerGainPercent,
            int microphoneGainPercent,
            bool separateTracks)
        {
            if (format == null)
            {
                throw new ArgumentNullException("format");
            }

            if (!hasComputerAudio && !hasMicrophone)
            {
                throw new ArgumentException("At least one audio source is required.");
            }

            this.format = format;
            this.hasComputerAudio = hasComputerAudio;
            this.hasMicrophone = hasMicrophone;
            // Two tracks only make sense when both sources are actually being captured.
            this.separateTracks = separateTracks && hasComputerAudio && hasMicrophone;
            this.computerGainPercent = SettingsStore.NormalizeAudioGainPercent(computerGainPercent);
            this.microphoneGainPercent = SettingsStore.NormalizeAudioGainPercent(microphoneGainPercent);
            this.computerSamples = new PcmSampleQueue();
            this.microphoneSamples = new PcmSampleQueue();
            this.peakTracker = new AudioPeakTracker();
        }

        public PcmSampleQueue ComputerSamples
        {
            get { return this.computerSamples; }
        }

        public PcmSampleQueue MicrophoneSamples
        {
            get { return this.microphoneSamples; }
        }

        public int TakePeak()
        {
            return this.peakTracker.TakePeak();
        }

        public bool SeparateTracks
        {
            get { return this.separateTracks; }
        }

        public int AudioTrackCount
        {
            get { return this.separateTracks ? 2 : 1; }
        }

        public void WriteThrough(MediaFoundationVideoWriter writer, long targetFrame)
        {
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }

            if (targetFrame < this.writtenFrames)
            {
                return;
            }

            long remainingFrames = targetFrame - this.writtenFrames;
            while (remainingFrames > 0)
            {
                int frameCount = (int)Math.Min(remainingFrames, MaximumFramesPerWrite);
                int sampleCount = frameCount * this.format.Channels;
                long sampleTime = this.writtenFrames * 10000000L / this.format.SampleRate;

                if (this.separateTracks)
                {
                    byte[] computerTrack = new byte[frameCount * this.format.BlockAlign];
                    byte[] microphoneTrack = new byte[frameCount * this.format.BlockAlign];
                    for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                    {
                        short computer = ApplyGain(this.computerSamples.TakeOrSilence(), this.computerGainPercent);
                        short microphone = ApplyGain(this.microphoneSamples.TakeOrSilence(), this.microphoneGainPercent);
                        // The single H meter still reflects everything the user chose to record.
                        this.peakTracker.Observe(computer);
                        this.peakTracker.Observe(microphone);
                        int byteOffset = sampleIndex * 2;
                        computerTrack[byteOffset] = (byte)(computer & 0xff);
                        computerTrack[byteOffset + 1] = (byte)((computer >> 8) & 0xff);
                        microphoneTrack[byteOffset] = (byte)(microphone & 0xff);
                        microphoneTrack[byteOffset + 1] = (byte)((microphone >> 8) & 0xff);
                    }

                    writer.WriteAudio(ComputerTrackIndex, computerTrack, frameCount, sampleTime);
                    writer.WriteAudio(MicrophoneTrackIndex, microphoneTrack, frameCount, sampleTime);
                }
                else
                {
                    byte[] mixed = new byte[frameCount * this.format.BlockAlign];
                    for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
                    {
                        short computer = this.hasComputerAudio
                            ? this.computerSamples.TakeOrSilence()
                            : (short)0;
                        short microphone = this.hasMicrophone
                            ? this.microphoneSamples.TakeOrSilence()
                            : (short)0;
                        short result = MixSamples(
                            computer,
                            this.computerGainPercent,
                            microphone,
                            this.microphoneGainPercent);
                        this.peakTracker.Observe(result);
                        int byteOffset = sampleIndex * 2;
                        mixed[byteOffset] = (byte)(result & 0xff);
                        mixed[byteOffset + 1] = (byte)((result >> 8) & 0xff);
                    }

                    writer.WriteAudio(mixed, frameCount, sampleTime);
                }

                this.writtenFrames += frameCount;
                remainingFrames -= frameCount;
            }
        }

        internal static short ApplyGain(short sample, int gainPercent)
        {
            long scaled = (long)sample * gainPercent / 100L;
            if (scaled > Int16.MaxValue) return Int16.MaxValue;
            if (scaled < Int16.MinValue) return Int16.MinValue;
            return (short)scaled;
        }

        internal static short MixSamples(
            short computer,
            int computerGainPercent,
            short microphone,
            int microphoneGainPercent)
        {
            long mixed = ((long)computer * computerGainPercent / 100L)
                + ((long)microphone * microphoneGainPercent / 100L);
            if (mixed > Int16.MaxValue)
            {
                return Int16.MaxValue;
            }

            if (mixed < Int16.MinValue)
            {
                return Int16.MinValue;
            }

            return (short)mixed;
        }
    }
}

/*  
 * I have mapped only used methods.
 * This code does not have all 'ALAC Decoder' functionality
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using AirPlay.Models.Enums;
using AirPlay.Utils;
using LibALAC;

namespace AirPlay
{
    // was unsafe
    public class ALACDecoder : IDecoder//, IDisposable
    {
        private int _pcm_pkt_size = 0;

        private Decoder _alacDecoder;

        public AudioFormat Type => AudioFormat.ALAC;

        public ALACDecoder()
        {
            // Constructor no longer needs library path as we're using NuGet package
        }

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            _pcm_pkt_size = frameLength * channels * bitDepth / 8;

            _alacDecoder = new Decoder(sampleRate, channels, bitDepth, frameLength);
            if (_alacDecoder == null)
            {
                return -1; // Initialization failed
            }
            return 0;
        }

        public int GetOutputStreamLength()
        {
            return _pcm_pkt_size;
        }

        public int DecodeFrame(byte[] input, ref byte[] output, int outputLen)
        {
            if (_alacDecoder == null)
            {
                throw new InvalidOperationException("Decoder is not initialized. Call Config() first.");
            }
            output = _alacDecoder.Decode(input, input.Length);
            return 0;
        }
    }

    public struct MagicCookie
    {
        public ALACSpecificConfig config;
        public ALACAudioChannelLayout channelLayoutInfo; // seems to be unused
    }

    public struct ALACSpecificConfig
    {
        public uint frameLength;
        public byte compatibleVersion;
        public byte bitDepth; // max 32
        public byte pb; // 0 <= pb <= 255
        public byte mb;
        public byte kb;
        public byte numChannels;
        public ushort maxRun;
        public uint maxFrameBytes;
        public uint avgBitRate;
        public uint sampleRate;
    }

    public struct ALACAudioChannelLayout
    {
        public uint mChannelLayoutTag;
        public uint mChannelBitmap;
        public uint mNumberChannelDescriptions;
    }
}

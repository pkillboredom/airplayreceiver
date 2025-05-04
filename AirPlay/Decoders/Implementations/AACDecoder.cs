﻿/*  
 * I have mapped only used methods.
 * This code does not have all 'AAC Decoder' functionality
 */

using System;
using System.IO;
using System.Runtime.InteropServices;
using AirPlay.Models.Enums;
using AirPlay.Utils;
using SharpJaad.AAC;

namespace AirPlay
{
    // was unsafe
    public class AACDecoder : IDecoder//, IDisposable
    {
        private Decoder _jaadDecoder;
        private DecoderConfig _decoderConfig;
        private SampleBuffer _outputBuffer = new SampleBuffer();

        private int _pcmPktSize;

        public AudioFormat Type => AudioFormat.AAC;

        public AACDecoder(TransportType transportFmt, AudioObjectType audioObjectType, uint nrOfLayers)
        {
            var decoderConfig = new DecoderConfig();
            if (GetProfileFromAudioObjectType(audioObjectType) is Profile profile)
            {
                decoderConfig.SetProfile(profile);
            }
            else
            {
                throw new NotSupportedException($"AudioObjectType {audioObjectType} is not supported.");
            }

            _decoderConfig = decoderConfig;
        }

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            _pcmPktSize = frameLength * channels * bitDepth / 8;

            var jaadSampleRate = SampleFrequencyExtensions.FromFrequency(sampleRate);
            _decoderConfig.SetSampleFrequency(jaadSampleRate);
            if (GetChannelConfigurationFromChannelCount(channels) is ChannelConfiguration channelConfig)
            {
                _decoderConfig.SetChannelConfiguration(channelConfig);
            }
            else
            {
                throw new NotSupportedException($"Channel count {channels} is not supported.");
            }

            _jaadDecoder = new Decoder(_decoderConfig);
            return 0;
        }

        public int GetOutputStreamLength()
        {
            return _pcmPktSize;
        }

        public int DecodeFrame(byte[] input, ref byte[] output, int length)
        {
            if (_jaadDecoder == null)
            {
                throw new InvalidOperationException("Decoder is not configured. Call Config() before decoding.");
            }
            _jaadDecoder.DecodeFrame(input, _outputBuffer);
            Array.Copy(_outputBuffer.Data, output, _outputBuffer.Data.Length);
            return 0;
        }

        /// <summary>
        /// Maps an AudioObjectType to the corresponding Profile in SharpJaad.AAC.
        /// Returns null if there is no mapping.
        /// </summary>
        /// <param name="audioObjectType">The AudioObjectType to map</param>
        /// <returns>The corresponding Profile or null if no mapping exists</returns>
        private static Profile? GetProfileFromAudioObjectType(AudioObjectType audioObjectType)
        {
            return audioObjectType switch
            {
                AudioObjectType.AOT_AAC_MAIN => Profile.AAC_MAIN,
                AudioObjectType.AOT_AAC_LC => Profile.AAC_LC,
                AudioObjectType.AOT_AAC_SSR => Profile.AAC_SSR,
                AudioObjectType.AOT_AAC_LTP => Profile.AAC_LTP,
                AudioObjectType.AOT_SBR => Profile.AAC_SBR,
                AudioObjectType.AOT_AAC_SCAL => Profile.AAC_SCALABLE,
                AudioObjectType.AOT_TWIN_VQ => Profile.TWIN_VQ,
                AudioObjectType.AOT_ER_AAC_LC => Profile.ER_AAC_LC,
                AudioObjectType.AOT_ER_AAC_LTP => Profile.ER_AAC_LTP,
                AudioObjectType.AOT_ER_AAC_SCAL => Profile.ER_AAC_SCALABLE,
                AudioObjectType.AOT_ER_TWIN_VQ => Profile.ER_TWIN_VQ,
                AudioObjectType.AOT_ER_BSAC => Profile.ER_BSAC,
                AudioObjectType.AOT_ER_AAC_LD => Profile.ER_AAC_LD,
                // No direct mapping for other values
                _ => null
            };
        }

        private static ChannelConfiguration? GetChannelConfigurationFromChannelCount(int channels)
        {
            return channels switch
            {
                0 => ChannelConfiguration.CHANNEL_CONFIG_NONE,
                1 => ChannelConfiguration.CHANNEL_CONFIG_MONO,
                2 => ChannelConfiguration.CHANNEL_CONFIG_STEREO,
                3 => ChannelConfiguration.CHANNEL_CONFIG_STEREO_PLUS_CENTER,
                4 => ChannelConfiguration.CHANNEL_CONFIG_STEREO_PLUS_CENTER_PLUS_REAR_MONO,
                5 => ChannelConfiguration.CHANNEL_CONFIG_FIVE,
                6 => ChannelConfiguration.CHANNEL_CONFIG_FIVE_PLUS_ONE,
                7 => ChannelConfiguration.CHANNEL_CONFIG_SEVEN_PLUS_ONE,
                8 => ChannelConfiguration.CHANNEL_CONFIG_SEVEN_PLUS_ONE,
                _ => null // No mapping for other channel counts
            };
        }
    }

    public enum TransportType
    {
        TT_UNKNOWN = -1, /* Unknown format. */
        TT_MP4_RAW = 0,  /* "as is" access units (packet based since there is obviously no sync layer) */
        TT_MP4_ADIF = 1, /* ADIF bitstream format. */
        TT_MP4_ADTS = 2, /* ADTS bitstream format. */

        TT_MP4_LATM_MCP1 = 6, /* Audio Mux Elements with muxConfigPresent = 1 */
        TT_MP4_LATM_MCP0 = 7, /* Audio Mux Elements with muxConfigPresent = 0, out of band StreamMuxConfig */

        TT_MP4_LOAS = 10, /* Audio Sync Stream. */

        TT_DRM = 12 /* Digital Radio Mondial (DRM30/DRM+) bitstream format. */

    }

    public enum AudioChannelType : int
    {
        ACT_NONE = 0x00,
        ACT_FRONT = 0x01, /* Front speaker position (at normal height) */
        ACT_SIDE = 0x02,  /* Side speaker position (at normal height) */
        ACT_BACK = 0x03,  /* Back speaker position (at normal height) */
        ACT_LFE = 0x04,   /* Low frequency effect speaker postion (front) */

        ACT_TOP = 0x10, /* Top speaker area (for combination with speaker positions) */
        ACT_FRONT_TOP = 0x11, /* Top front speaker = (ACT_FRONT|ACT_TOP) */
        ACT_SIDE_TOP = 0x12,  /* Top side speaker  = (ACT_SIDE |ACT_TOP) */
        ACT_BACK_TOP = 0x13,  /* Top back speaker  = (ACT_BACK |ACT_TOP) */

        ACT_BOTTOM = 0x20, /* Bottom speaker area (for combination with speaker positions) */
        ACT_FRONT_BOTTOM = 0x21, /* Bottom front speaker = (ACT_FRONT|ACT_BOTTOM) */
        ACT_SIDE_BOTTOM = 0x22,  /* Bottom side speaker  = (ACT_SIDE |ACT_BOTTOM) */
        ACT_BACK_BOTTOM = 0x23   /* Bottom back speaker  = (ACT_BACK |ACT_BOTTOM) */

    }

    public enum AudioObjectType : int
    {
        AOT_NONE = -1,
        AOT_NULL_OBJECT = 0,
        AOT_AAC_MAIN = 1, /* Main profile */
        AOT_AAC_LC = 2,   /* Low Complexity object */
        AOT_AAC_SSR = 3,
        AOT_AAC_LTP = 4,
        AOT_SBR = 5,
        AOT_AAC_SCAL = 6,
        AOT_TWIN_VQ = 7,
        AOT_CELP = 8,
        AOT_HVXC = 9,
        AOT_RSVD_10 = 10,          /* (reserved)                                */
        AOT_RSVD_11 = 11,          /* (reserved)                                */
        AOT_TTSI = 12,             /* TTSI Object                               */
        AOT_MAIN_SYNTH = 13,       /* Main Synthetic object                     */
        AOT_WAV_TAB_SYNTH = 14,    /* Wavetable Synthesis object                */
        AOT_GEN_MIDI = 15,         /* General MIDI object                       */
        AOT_ALG_SYNTH_AUD_FX = 16, /* Algorithmic Synthesis and Audio FX object */
        AOT_ER_AAC_LC = 17,        /* Error Resilient(ER) AAC Low Complexity    */
        AOT_RSVD_18 = 18,          /* (reserved)                                */
        AOT_ER_AAC_LTP = 19,       /* Error Resilient(ER) AAC LTP object        */
        AOT_ER_AAC_SCAL = 20,      /* Error Resilient(ER) AAC Scalable object   */
        AOT_ER_TWIN_VQ = 21,       /* Error Resilient(ER) TwinVQ object         */
        AOT_ER_BSAC = 22,          /* Error Resilient(ER) BSAC object           */
        AOT_ER_AAC_LD = 23,        /* Error Resilient(ER) AAC LowDelay object   */
        AOT_ER_CELP = 24,          /* Error Resilient(ER) CELP object           */
        AOT_ER_HVXC = 25,          /* Error Resilient(ER) HVXC object           */
        AOT_ER_HILN = 26,          /* Error Resilient(ER) HILN object           */
        AOT_ER_PARA = 27,          /* Error Resilient(ER) Parametric object     */
        AOT_RSVD_28 = 28,          /* might become SSC                          */
        AOT_PS = 29,               /* PS, Parametric Stereo (includes SBR)      */
        AOT_MPEGS = 30,            /* MPEG Surround                             */

        AOT_ESCAPE = 31, /* Signal AOT uses more than 5 bits          */

        AOT_MP3ONMP4_L1 = 32, /* MPEG-Layer1 in mp4                        */
        AOT_MP3ONMP4_L2 = 33, /* MPEG-Layer2 in mp4                        */
        AOT_MP3ONMP4_L3 = 34, /* MPEG-Layer3 in mp4                        */
        AOT_RSVD_35 = 35,     /* might become DST                          */
        AOT_RSVD_36 = 36,     /* might become ALS                          */
        AOT_AAC_SLS = 37,     /* AAC + SLS                                 */
        AOT_SLS = 38,         /* SLS                                       */
        AOT_ER_AAC_ELD = 39,  /* AAC Enhanced Low Delay                    */

        AOT_USAC = 42,     /* USAC                                      */
        AOT_SAOC = 43,     /* SAOC                                      */
        AOT_LD_MPEGS = 44, /* Low Delay MPEG Surround                   */

        /* Pseudo AOTs */
        AOT_MP2_AAC_LC = 129, /**< Virtual AOT MP2 Low Complexity profile */
        AOT_MP2_SBR = 132, /**< Virtual AOT MP2 Low Complexity Profile with SBR    */

        AOT_DRM_AAC = 143, /**< Virtual AOT for DRM (ER-AAC-SCAL without SBR) */
        AOT_DRM_SBR = 144, /**< Virtual AOT for DRM (ER-AAC-SCAL with SBR) */
        AOT_DRM_MPEG_PS =
            145, /**< Virtual AOT for DRM (ER-AAC-SCAL with SBR and MPEG-PS) */
        AOT_DRM_SURROUND =
            146, /**< Virtual AOT for DRM Surround (ER-AAC-SCAL (+SBR) +MPS) */
        AOT_DRM_USAC = 147 /**< Virtual AOT for DRM with USAC */

    }

    public enum AACDecoderError : int
    {
        AAC_DEC_OK =
            0x0000, /*!< No error occurred. Output buffer is valid and error free. */
        AAC_DEC_OUT_OF_MEMORY =
            0x0002, /*!< Heap returned NULL pointer. Output buffer is invalid. */
        AAC_DEC_UNKNOWN =
            0x0005, /*!< Error condition is of unknown reason, or from a another
                 module. Output buffer is invalid. */

        /* Synchronization errors. Output buffer is invalid. */
        aac_dec_sync_error_start = 0x1000,
        AAC_DEC_TRANSPORT_SYNC_ERROR = 0x1001, /*!< The transport decoder had
                                            synchronization problems. Do not
                                            exit decoding. Just feed new
                                              bitstream data. */
        AAC_DEC_NOT_ENOUGH_BITS = 0x1002, /*!< The input buffer ran out of bits. */
        aac_dec_sync_error_end = 0x1FFF,

        /* Initialization errors. Output buffer is invalid. */
        aac_dec_init_error_start = 0x2000,
        AAC_DEC_INVALID_HANDLE =
            0x2001, /*!< The handle passed to the function call was invalid (NULL). */
        AAC_DEC_UNSUPPORTED_AOT =
            0x2002, /*!< The AOT found in the configuration is not supported. */
        AAC_DEC_UNSUPPORTED_FORMAT =
            0x2003, /*!< The bitstream format is not supported.  */
        AAC_DEC_UNSUPPORTED_ER_FORMAT =
            0x2004, /*!< The error resilience tool format is not supported. */
        AAC_DEC_UNSUPPORTED_EPCONFIG =
            0x2005, /*!< The error protection format is not supported. */
        AAC_DEC_UNSUPPORTED_MULTILAYER =
            0x2006, /*!< More than one layer for AAC scalable is not supported. */
        AAC_DEC_UNSUPPORTED_CHANNELCONFIG =
            0x2007, /*!< The channel configuration (either number or arrangement) is
                 not supported. */
        AAC_DEC_UNSUPPORTED_SAMPLINGRATE = 0x2008, /*!< The sample rate specified in
                                                the configuration is not
                                                supported. */
        AAC_DEC_INVALID_SBR_CONFIG =
            0x2009, /*!< The SBR configuration is not supported. */
        AAC_DEC_SET_PARAM_FAIL = 0x200A,  /*!< The parameter could not be set. Either
                                       the value was out of range or the
                                       parameter does  not exist. */
        AAC_DEC_NEED_TO_RESTART = 0x200B, /*!< The decoder needs to be restarted,
                                       since the required configuration change
                                       cannot be performed. */
        AAC_DEC_OUTPUT_BUFFER_TOO_SMALL =
            0x200C, /*!< The provided output buffer is too small. */
        aac_dec_init_error_end = 0x2FFF,

        /* Decode errors. Output buffer is valid but concealed. */
        aac_dec_decode_error_start = 0x4000,
        AAC_DEC_TRANSPORT_ERROR =
            0x4001, /*!< The transport decoder encountered an unexpected error. */
        AAC_DEC_PARSE_ERROR = 0x4002, /*!< Error while parsing the bitstream. Most
                                   probably it is corrupted, or the system
                                   crashed. */
        AAC_DEC_UNSUPPORTED_EXTENSION_PAYLOAD =
            0x4003, /*!< Error while parsing the extension payload of the bitstream.
                 The extension payload type found is not supported. */
        AAC_DEC_DECODE_FRAME_ERROR = 0x4004, /*!< The parsed bitstream value is out of
                                          range. Most probably the bitstream is
                                          corrupt, or the system crashed. */
        AAC_DEC_CRC_ERROR = 0x4005,          /*!< The embedded CRC did not match. */
        AAC_DEC_INVALID_CODE_BOOK = 0x4006,  /*!< An invalid codebook was signaled.
                                          Most probably the bitstream is corrupt,
                                          or the system  crashed. */
        AAC_DEC_UNSUPPORTED_PREDICTION =
            0x4007, /*!< Predictor found, but not supported in the AAC Low Complexity
                 profile. Most probably the bitstream is corrupt, or has a wrong
                 format. */
        AAC_DEC_UNSUPPORTED_CCE = 0x4008, /*!< A CCE element was found which is not
                                       supported. Most probably the bitstream is
                                       corrupt, or has a wrong format. */
        AAC_DEC_UNSUPPORTED_LFE = 0x4009, /*!< A LFE element was found which is not
                                       supported. Most probably the bitstream is
                                       corrupt, or has a wrong format. */
        AAC_DEC_UNSUPPORTED_GAIN_CONTROL_DATA =
            0x400A, /*!< Gain control data found but not supported. Most probably the
                 bitstream is corrupt, or has a wrong format. */
        AAC_DEC_UNSUPPORTED_SBA =
            0x400B, /*!< SBA found, but currently not supported in the BSAC profile.
               */
        AAC_DEC_TNS_READ_ERROR = 0x400C, /*!< Error while reading TNS data. Most
                                      probably the bitstream is corrupt or the
                                      system crashed. */
        AAC_DEC_RVLC_ERROR =
            0x400D, /*!< Error while decoding error resilient data. */
        aac_dec_decode_error_end = 0x4FFF,
        /* Ancillary data errors. Output buffer is valid. */
        aac_dec_anc_data_error_start = 0x8000,
        AAC_DEC_ANC_DATA_ERROR =
            0x8001, /*!< Non severe error concerning the ancillary data handling. */
        AAC_DEC_TOO_SMALL_ANC_BUFFER = 0x8002,  /*!< The registered ancillary data
                                             buffer is too small to receive the
                                             parsed data. */
        AAC_DEC_TOO_MANY_ANC_ELEMENTS = 0x8003, /*!< More than the allowed number of
                                             ancillary data elements should be
                                             written to buffer. */
        aac_dec_anc_data_error_end = 0x8FFF

    }

    public enum FrequencyIndex
    {
        F_96000 = 0,
        F_88200 = 1,
        F_64000 = 2,
        F_48000 = 3,
        F_44100 = 4,
        F_32000 = 5,
        F_24000 = 6,
        F_22050 = 7,
        F_16000 = 8,
        F_12000 = 9,
        F_11025 = 10,
        F_8000 = 11,
        F_7350 = 12,
    }
}

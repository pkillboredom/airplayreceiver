using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Services.Implementations;
using AirPlay.Telemetry;
using AirPlay.Utils;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPlay.Listeners
{
    public class MirroringListener : BaseTcpListener
    {
        public const string AIR_PLAY_STREAM_KEY = "AirPlayStreamKey";
        public const string AIR_PLAY_STREAM_IV = "AirPlayStreamIV";

        private readonly IRtspReceiver _receiver;
        private readonly string _sessionId;
        private readonly IBufferedCipher _aesCtrDecrypt;
        private readonly OmgHax _omgHax = new OmgHax();

        private byte[] _og = new byte[16];
        private int _nextDecryptCount;

        public MirroringListener(IRtspReceiver receiver, string sessionId, ushort port) : base(port, true)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "MirroringListener.Constructor", 
                ActivityKind.Internal,
                sessionId: sessionId);
                
            activity?.SetTag("video.port", port);
            
            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _aesCtrDecrypt = CipherUtilities.GetCipher("AES/CTR/NoPadding");
            
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        public override async Task OnRawReceivedAsync(TcpClient client, NetworkStream stream, CancellationToken cancellationToken)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "MirroringListener.StreamProcessing", 
                ActivityKind.Consumer,
                sessionId: _sessionId);
                
            activity?.AddEvent(new ActivityEvent("MirroringStreamInitialized"));
            
            try
            {
                // Get session by active-remove header value
                var session = await SessionManager.Current.GetSessionAsync(_sessionId);
                
                // If we have not decripted session AesKey
                if (session.DecryptedAesKey == null)
                {
                    using var decryptActivity = AirPlayTelemetry.CreateSessionActivity(
                        "MirroringListener.DecryptAesKey",
                        ActivityKind.Internal,
                        sessionId: _sessionId);
                        
                    byte[] decryptedAesKey = new byte[16];
                    _omgHax.DecryptAesKey(session.KeyMsg, session.AesKey, decryptedAesKey);
                    session.DecryptedAesKey = decryptedAesKey;
                    
                    decryptActivity?.SetStatus(ActivityStatusCode.Ok);
                }

                // Initialize cipher for video decryption
                using (var cipherActivity = AirPlayTelemetry.CreateSessionActivity(
                    "MirroringListener.InitCipher",
                    ActivityKind.Internal,
                    sessionId: _sessionId))
                {
                    cipherActivity?.SetTag("stream.connection_id", session.StreamConnectionId);
                    InitAesCtrCipher(session.DecryptedAesKey, session.EcdhShared, session.StreamConnectionId);
                    cipherActivity?.SetStatus(ActivityStatusCode.Ok);
                }

                var headerBuffer = new byte[128];
                var readStart = 0;
                var frameCount = 0;

                do
                {
                    MirroringHeader header;
                    if (stream.DataAvailable)
                    {
                        using var packetActivity = AirPlayTelemetry.CreateSessionActivity(
                            "MirroringPacketProcessing",
                            ActivityKind.Internal,
                            sessionId: _sessionId);
                            
                        var ret = await stream.ReadAsync(headerBuffer, readStart, 4 - readStart);
                        readStart += ret;
                        
                        if (readStart < 4)
                        {
                            packetActivity?.SetStatus(ActivityStatusCode.Ok, "Incomplete header");
                            continue;
                        }

                        if ((headerBuffer[0] == 80 && headerBuffer[1] == 79 && headerBuffer[2] == 83 && headerBuffer[3] == 84) || 
                            (headerBuffer[0] == 71 && headerBuffer[1] == 69 && headerBuffer[2] == 84))
                        {
                            // Request is POST or GET (skip)
                            packetActivity?.SetTag("video.packet.is_http", true);
                            packetActivity?.SetStatus(ActivityStatusCode.Ok, "HTTP request ignored");
                        }
                        else
                        {
                            do
                            {
                                ret = await stream.ReadAsync(headerBuffer, readStart, 128 - readStart);
                                if (ret <= 0)
                                {
                                    break;
                                }
                                readStart += ret;
                            } while (readStart < 128);

                            header = new MirroringHeader(headerBuffer);
                            
                            packetActivity?.SetTag("video.header.payload_type", header.PayloadType);
                            packetActivity?.SetTag("video.header.payload_size", header.PayloadSize);
                            packetActivity?.SetTag("video.header.width", header.WidthSource);
                            packetActivity?.SetTag("video.header.height", header.HeightSource);
                            packetActivity?.SetTag("video.header.timestamp", header.PayloadPts);

                            if (!session.Pts.HasValue)
                            {
                                session.Pts = header.PayloadPts;
                            }
                            if (!session.WidthSource.HasValue)
                            {
                                session.WidthSource = header.WidthSource;
                            }
                            if (!session.HeightSource.HasValue)
                            {
                                session.HeightSource = header.HeightSource;
                            }

                            if (header != null && stream.DataAvailable)
                            {
                                try
                                {
                                    byte[] payload = (byte[])Array.CreateInstance(typeof(byte), header.PayloadSize);
                                    
                                    readStart = 0;
                                    do
                                    {
                                        ret = await stream.ReadAsync(payload, readStart, header.PayloadSize - readStart);
                                        readStart += ret;
                                    } while (readStart < header.PayloadSize);

                                    frameCount++;
                                    
                                    if (header.PayloadType == 0)
                                    {
                                        using var videoActivity = AirPlayTelemetry.CreateSessionActivity(
                                            "MirroringVideoProcessing",
                                            ActivityKind.Producer,
                                            sessionId: _sessionId);
                                            
                                        videoActivity?.SetTag("video.frame.number", frameCount);
                                        videoActivity?.SetTag("video.frame.size", payload.Length);
                                        
                                        var stopwatch = Stopwatch.StartNew();
                                        
                                        using (var decryptActivity = AirPlayTelemetry.CreateSessionActivity(
                                            "DecryptVideoData",
                                            ActivityKind.Internal,
                                            sessionId: _sessionId))
                                        {
                                            DecryptVideoData(payload, out byte[] output);
                                            decryptActivity?.SetTag("video.decrypt.output_size", output.Length);
                                            decryptActivity?.SetStatus(ActivityStatusCode.Ok);
                                            
                                            using (var processActivity = AirPlayTelemetry.CreateSessionActivity(
                                                "ProcessVideoFrame",
                                                ActivityKind.Internal,
                                                sessionId: _sessionId))
                                            {
                                                processActivity?.SetTag("video.resolution", $"{session.WidthSource.Value}x{session.HeightSource.Value}");
                                                processActivity?.SetTag("video.timestamp", session.Pts.Value);
                                                
                                                ProcessVideo(output, session.SpsPps, session.Pts.Value, session.WidthSource.Value, session.HeightSource.Value);
                                                processActivity?.SetStatus(ActivityStatusCode.Ok);
                                            }
                                        }
                                        
                                        stopwatch.Stop();
                                        
                                        // Record metrics
                                        AirPlayTelemetry.VideoFramesProcessed.Add(1);
                                        AirPlayTelemetry.VideoFrameProcessingTime.Record(stopwatch.Elapsed.TotalMilliseconds);
                                        
                                        videoActivity?.SetTag("video.processing.time_ms", stopwatch.ElapsedMilliseconds);
                                        videoActivity?.SetStatus(ActivityStatusCode.Ok);
                                    }
                                    else if (header.PayloadType == 1)
                                    {
                                        using var spsPpsActivity = AirPlayTelemetry.CreateSessionActivity(
                                            "MirroringVideoCodecData",
                                            ActivityKind.Internal,
                                            sessionId: _sessionId);
                                            
                                        ProcessSpsPps(payload, out byte[] spsPps);
                                        session.SpsPps = spsPps;
                                        
                                        spsPpsActivity?.SetTag("video.codec.sps_pps_size", spsPps?.Length ?? 0);
                                        spsPpsActivity?.SetStatus(ActivityStatusCode.Ok);
                                    }
                                    else
                                    {
                                        packetActivity?.SetTag("video.header.unknown_type", header.PayloadType);
                                    }
                                }
                                catch (Exception e)
                                {
                                    packetActivity?.AddException(e);
                                    Console.WriteLine(e);
                                }
                            }

                            await Task.Delay(10);

                            // Save current session
                            await SessionManager.Current.CreateOrUpdateSessionAsync(_sessionId, session);
                            
                            packetActivity?.SetTag("video.frame_count", frameCount);
                            packetActivity?.SetStatus(ActivityStatusCode.Ok);
                        }
                    }

                    // Fix issue #24
                    await Task.Delay(1);
                    readStart = 0;
                    header = null;
                    headerBuffer = new byte[128];
                } while (client.Connected && stream.CanRead && !cancellationToken.IsCancellationRequested);

                activity?.AddEvent(new ActivityEvent("MirroringStreamClosed"));
                activity?.SetTag("video.total_frames_processed", frameCount);
                Console.WriteLine($"Closing mirroring connection..");
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private void DecryptVideoData(byte[] videoData, out byte[] output)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "MirroringListener.DecryptVideoData", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            activity?.SetTag("video.decrypt.input_size", videoData.Length);
            activity?.SetTag("video.decrypt.next_count", _nextDecryptCount);
                
            try
            {
                if (_nextDecryptCount > 0)
                {
                    for (int i = 0; i < _nextDecryptCount; i++)
                    {
                        videoData[i] = (byte)(videoData[i] ^ _og[(16 - _nextDecryptCount) + i]);
                    }
                }

                int encryptlen = ((videoData.Length - _nextDecryptCount) / 16) * 16;
                activity?.SetTag("video.decrypt.encrypt_length", encryptlen);
                
                _aesCtrDecrypt.ProcessBytes(videoData, _nextDecryptCount, encryptlen, videoData, _nextDecryptCount);
                Array.Copy(videoData, _nextDecryptCount, videoData, _nextDecryptCount, encryptlen);

                int restlen = (videoData.Length - _nextDecryptCount) % 16;
                int reststart = videoData.Length - restlen;
                
                activity?.SetTag("video.decrypt.rest_length", restlen);
                activity?.SetTag("video.decrypt.rest_start", reststart);
                
                _nextDecryptCount = 0;
                
                if (restlen > 0)
                {
                    Array.Fill(_og, (byte)0);
                    Array.Copy(videoData, reststart, _og, 0, restlen);
                    _aesCtrDecrypt.ProcessBytes(_og, 0, 16, _og, 0);
                    Array.Copy(_og, 0, videoData, reststart, restlen);
                    _nextDecryptCount = 16 - restlen;
                    
                    activity?.SetTag("video.decrypt.next_count_updated", _nextDecryptCount);
                }

                output = new byte[videoData.Length];
                Array.Copy(videoData, 0, output, 0, videoData.Length);
                
                // Release video data
                videoData = null;
                
                activity?.SetTag("video.decrypt.output_size", output.Length);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private void InitAesCtrCipher(byte[] aesKey, byte[] ecdhShared, string streamConnectionId)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "MirroringListener.InitCipher", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            try
            {
                byte[] eaesKey = Utilities.Hash(aesKey, ecdhShared);

                byte[] skey = Encoding.UTF8.GetBytes($"{AIR_PLAY_STREAM_KEY}{streamConnectionId}");
                byte[] hash1 = Utilities.Hash(skey, Utilities.CopyOfRange(eaesKey, 0, 16));

                byte[] siv = Encoding.UTF8.GetBytes($"{AIR_PLAY_STREAM_IV}{streamConnectionId}");
                byte[] hash2 = Utilities.Hash(siv, Utilities.CopyOfRange(eaesKey, 0, 16));

                byte[] decryptAesKey = new byte[16];
                byte[] decryptAesIV = new byte[16];
                Array.Copy(hash1, 0, decryptAesKey, 0, 16);
                Array.Copy(hash2, 0, decryptAesIV, 0, 16);

                var keyParameter = ParameterUtilities.CreateKeyParameter("AES", decryptAesKey);
                var cipherParameters = new ParametersWithIV(keyParameter, decryptAesIV, 0, decryptAesIV.Length);

                _aesCtrDecrypt.Init(false, cipherParameters);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private void ProcessVideo(byte[] payload, byte[] spsPps, long pts, int widthSource, int heightSource)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "MirroringListener.ProcessVideo", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            activity?.SetTag("video.payload.size", payload.Length);
            activity?.SetTag("video.sps_pps.size", spsPps?.Length ?? 0);
            activity?.SetTag("video.pts", pts);
            activity?.SetTag("video.width", widthSource);
            activity?.SetTag("video.height", heightSource);
            
            try
            {
                int nalu_size = 0;
                
                while (nalu_size < payload.Length)
                {
                    int nc_len = (payload[nalu_size + 3] & 0xFF) | ((payload[nalu_size + 2] & 0xFF) << 8) | 
                                ((payload[nalu_size + 1] & 0xFF) << 16) | ((payload[nalu_size] & 0xFF) << 24);
                    
                    if (nc_len > 0)
                    {
                        payload[nalu_size] = 0;
                        payload[nalu_size + 1] = 0;
                        payload[nalu_size + 2] = 0;
                        payload[nalu_size + 3] = 1;
                        nalu_size += nc_len + 4;
                    }
                    
                    if (payload.Length - nc_len > 4)
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, "Invalid NALU length");
                        return;
                    }
                }

                if (spsPps != null && spsPps.Length != 0)
                {
                    var h264Data = new H264Data();
                    h264Data.FrameType = payload[4] & 0x1f;
                    
                    activity?.SetTag("video.frame.type", h264Data.FrameType);
                    activity?.SetTag("video.frame.keyframe", h264Data.FrameType == 5);
                    
                    if (h264Data.FrameType == 5)  // Keyframe
                    {
                        using var keyframeActivity = AirPlayTelemetry.CreateSessionActivity(
                            "VideoKeyframeProcessing",
                            ActivityKind.Internal,
                            sessionId: _sessionId);
                            
                        var payloadOut = (byte[])Array.CreateInstance(typeof(byte), payload.Length + spsPps.Length);
                        Array.Copy(spsPps, 0, payloadOut, 0, spsPps.Length);
                        Array.Copy(payload, 0, payloadOut, spsPps.Length, payload.Length);
                        
                        h264Data.Data = payloadOut;
                        h264Data.Length = payload.Length + spsPps.Length;
                        
                        keyframeActivity?.SetTag("video.keyframe.output_size", h264Data.Length);
                        keyframeActivity?.SetStatus(ActivityStatusCode.Ok);
                        
                        // Release payload
                        payload = null;
                    }
                    else
                    {
                        h264Data.Data = payload;
                        h264Data.Length = payload.Length;
                    }

                    h264Data.Pts = pts;
                    h264Data.Width = widthSource;
                    h264Data.Height = heightSource;

                    _receiver.OnData(h264Data);
                    
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                else 
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "No SPS/PPS available");
                }
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private void ProcessSpsPps(byte[] payload, out byte[] spsPps)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "MirroringListener.ProcessSpsPps", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            activity?.SetTag("video.codec.payload_size", payload.Length);
            
            try
            {
                var h264 = new H264Codec();
                h264.Version = payload[0];
                h264.ProfileHigh = payload[1];
                h264.Compatibility = payload[2];
                h264.Level = payload[3];
                h264.Reserved6AndNal = payload[4];
                h264.Reserved3AndSps = payload[5];
                h264.LengthOfSps = (short)(((payload[6] & 255) << 8) + (payload[7] & 255));
                
                activity?.SetTag("video.codec.version", h264.Version);
                activity?.SetTag("video.codec.profile_high", h264.ProfileHigh);
                activity?.SetTag("video.codec.level", h264.Level);
                activity?.SetTag("video.codec.sps_length", h264.LengthOfSps);
                
                var sequence = new byte[h264.LengthOfSps];
                Array.Copy(payload, 8, sequence, 0, h264.LengthOfSps);
                h264.SequenceParameterSet = sequence;
                
                h264.NumberOfPps = payload[h264.LengthOfSps + 8];
                h264.LengthOfPps = (short)(((payload[h264.LengthOfSps + 9] & 2040) + payload[h264.LengthOfSps + 10]) & 255);
                
                activity?.SetTag("video.codec.pps_length", h264.LengthOfPps);
                
                var picture = new byte[h264.LengthOfPps];
                Array.Copy(payload, h264.LengthOfSps + 11, picture, 0, h264.LengthOfPps);
                h264.PictureParameterSet = picture;

                if (h264.LengthOfSps + h264.LengthOfPps < 102400)
                {
                    var spsPpsLen = h264.LengthOfSps + h264.LengthOfPps + 8;
                    spsPps = new byte[spsPpsLen];
                    
                    spsPps[0] = 0;
                    spsPps[1] = 0;
                    spsPps[2] = 0;
                    spsPps[3] = 1;
                    Array.Copy(h264.SequenceParameterSet, 0, spsPps, 4, h264.LengthOfSps);
                    
                    spsPps[h264.LengthOfSps + 4] = 0;
                    spsPps[h264.LengthOfSps + 5] = 0;
                    spsPps[h264.LengthOfSps + 6] = 0;
                    spsPps[h264.LengthOfSps + 7] = 1;
                    Array.Copy(h264.PictureParameterSet, 0, spsPps, h264.LengthOfSps + 8, h264.LengthOfPps);
                    
                    activity?.SetTag("video.codec.sps_pps_length", spsPpsLen);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "SPS/PPS too large");
                    spsPps = null;
                }
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }
    }
}

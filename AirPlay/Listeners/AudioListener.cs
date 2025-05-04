using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Models.Configs;
using AirPlay.Models.Enums;
using AirPlay.Services.Implementations;
using AirPlay.Telemetry;
using AirPlay.Utils;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace AirPlay.Listeners
{
    public class AudioListener : BaseUdpListener
    {
        public const int RAOP_PACKET_LENGTH = 50000;
        public const int RAOP_BUFFER_LENGTH = 1024; //512;
        public const ulong OFFSET_1900_TO_1970 = 2208988800UL;

        private readonly IRtspReceiver _receiver;
        private readonly string _sessionId;
        private IBufferedCipher _aesCbcDecrypt;
        private readonly OmgHax _omgHax = new OmgHax();

        private IDecoder _decoder;
        private ulong _sync_time;
        private ulong _sync_timestamp;
        private ushort _controlSequenceNumber = 0;
        private RaopBuffer _raopBuffer;
        private Socket _cSocket;

        private readonly CodecLibrariesConfig _clConfig;
        private readonly DumpConfig _dConfig;

        public AudioListener(IRtspReceiver receiver, string sessionId, ushort cport, ushort dport, CodecLibrariesConfig clConfig, DumpConfig dConfig) : base(cport, dport)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.Constructor", 
                ActivityKind.Internal,
                sessionId: sessionId);

            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _clConfig = clConfig ?? throw new ArgumentNullException(nameof(clConfig));
            _dConfig = dConfig ?? throw new ArgumentNullException(nameof(dConfig));

            activity?.SetTag("audio.control.port", cport);
            activity?.SetTag("audio.data.port", dport);

            _raopBuffer = RaopBufferInit();
            _aesCbcDecrypt = CipherUtilities.GetCipher("AES/CBC/NoPadding");
        }

        public override async Task OnRawCSocketAsync(Socket cSocket, CancellationToken cancellationToken)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.ControlSocketProcessing", 
                ActivityKind.Consumer,
                sessionId: _sessionId);
                
            activity?.AddEvent(new ActivityEvent("AudioControlSocketInitialized"));
            Console.WriteLine("Initializing recevie audio control from socket..");

            _cSocket = cSocket;

            // Get session by active-remove header value
            var session = await SessionManager.Current.GetSessionAsync(_sessionId);

            // If we have not decripted session AesKey
            if (session.DecryptedAesKey == null)
            {
                using var decryptActivity = AirPlayTelemetry.CreateSessionActivity(
                    "AudioListener.DecryptAesKey",
                    ActivityKind.Internal,
                    sessionId: _sessionId);
                    
                byte[] decryptedAesKey = new byte[16];
                _omgHax.DecryptAesKey(session.KeyMsg, session.AesKey, decryptedAesKey);
                session.DecryptedAesKey = decryptedAesKey;
                
                decryptActivity?.SetStatus(ActivityStatusCode.Ok);
            }

            await SessionManager.Current.CreateOrUpdateSessionAsync(_sessionId, session);

            var packet = new byte[RAOP_PACKET_LENGTH];
            var packetProcessingCount = 0;

            do
            {
                try
                {
                    var cret = cSocket.Receive(packet, 0, RAOP_PACKET_LENGTH, SocketFlags.None, out SocketError error);
                    if (error != SocketError.Success)
                    {
                        continue;
                    }

                    packetProcessingCount++;
                    
                    using var packetActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AudioControlPacketProcessing",
                        ActivityKind.Internal,
                        sessionId: _sessionId);
                    
                    packetActivity?.SetTag("audio.control.packet.number", packetProcessingCount);
                    packetActivity?.SetTag("audio.control.packet.size", cret);

                    var mem = new MemoryStream(packet);
                    using (var reader = new BinaryReader(mem))
                    {
                        mem.Position = 1;
                        int type_c = reader.ReadByte() & ~0x80;
                        
                        packetActivity?.SetTag("audio.control.packet.type", type_c);

                        if (type_c == 0x56)
                        {
                            using var dataActivity = AirPlayTelemetry.CreateSessionActivity(
                                "AudioControlDataProcessing",
                                ActivityKind.Internal,
                                sessionId: _sessionId);
                                
                            InitAesCbcCipher(session.DecryptedAesKey, session.EcdhShared, session.AesIv);
                            
                            mem.Position = 4;
                            var data = reader.ReadBytes(cret - 4);
                            
                            dataActivity?.SetTag("audio.control.data.size", data.Length);
                            
                            var stopwatch = Stopwatch.StartNew();
                            var ret = RaopBufferQueue(_raopBuffer, data, (ushort)data.Length, session);
                            stopwatch.Stop();
                            
                            dataActivity?.SetTag("audio.buffer.queue.result", ret);
                            dataActivity?.SetTag("audio.buffer.queue.duration_ms", stopwatch.ElapsedMilliseconds);
                            
                            if (ret >= 0)
                            {
                                // ERROR
                                dataActivity?.SetStatus(ActivityStatusCode.Error, "Error queueing audio control data");
                            }
                            else
                            {
                                dataActivity?.SetStatus(ActivityStatusCode.Ok);
                            }
                        }
                        else if (type_c == 0x54)
                        {
                            using var syncActivity = AirPlayTelemetry.CreateSessionActivity(
                                "AudioControlSyncProcessing",
                                ActivityKind.Internal,
                                sessionId: _sessionId);
                                
                            /**
                                * packetlen = 20
                                * bytes	description
                                8	RTP header without SSRC
                                8	current NTP time
                                4	RTP timestamp for the next audio packet
                                */
                            mem.Position = 8;
                            ulong ntp_time = (((ulong)reader.ReadInt32()) * 1000000UL) + ((((ulong)reader.ReadInt32()) * 1000000UL) / Int32.MaxValue);
                            uint rtp_timestamp = (uint)((packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7]);
                            uint next_timestamp = (uint)((packet[16] << 24) | (packet[17] << 16) | (packet[18] << 8) | packet[19]);

                            syncActivity?.SetTag("audio.sync.ntp_time", ntp_time);
                            syncActivity?.SetTag("audio.sync.rtp_timestamp", rtp_timestamp);
                            syncActivity?.SetTag("audio.sync.next_timestamp", next_timestamp);

                            _sync_time = ntp_time - OFFSET_1900_TO_1970 * 1000000UL;
                            _sync_timestamp = rtp_timestamp;
                            
                            syncActivity?.SetStatus(ActivityStatusCode.Ok);
                        }
                        else
                        {
                            Console.WriteLine("Unknown packet");
                            packetActivity?.SetTag("audio.control.packet.unknown", true);
                            packetActivity?.SetStatus(ActivityStatusCode.Error, "Unknown audio control packet type");
                        }
                    }

                    Array.Fill<byte>(packet, 0);
                    packetActivity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (Exception ex)
                {
                    activity?.AddException(ex);
                }
            } while (!cancellationToken.IsCancellationRequested);

            activity?.AddEvent(new ActivityEvent("AudioControlSocketClosed"));
            Console.WriteLine("Closing audio control socket..");
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        public override async Task OnRawDSocketAsync(Socket dSocket, CancellationToken cancellationToken)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.DataSocketProcessing", 
                ActivityKind.Consumer,
                sessionId: _sessionId);
                
            activity?.AddEvent(new ActivityEvent("AudioDataSocketInitialized"));
            Console.WriteLine("Initializing recevie audio data from socket..");

            // Get current session
            var session = await SessionManager.Current.GetSessionAsync(_sessionId);

            // If we have not decripted session AesKey
            if (session.DecryptedAesKey == null)
            {
                using var decryptActivity = AirPlayTelemetry.CreateSessionActivity(
                    "AudioListener.DecryptAesKey",
                    ActivityKind.Internal,
                    sessionId: _sessionId);
                    
                byte[] decryptedAesKey = new byte[16];
                _omgHax.DecryptAesKey(session.KeyMsg, session.AesKey, decryptedAesKey);
                session.DecryptedAesKey = decryptedAesKey;
                
                decryptActivity?.SetStatus(ActivityStatusCode.Ok);
            }

            // Initialize decoder
            using (var decoderActivity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.DecoderInitialization", 
                ActivityKind.Internal,
                sessionId: _sessionId))
            {
                decoderActivity?.SetTag("audio.format", session.AudioFormat.ToString());
                InitializeDecoder(session.AudioFormat);
                decoderActivity?.SetStatus(ActivityStatusCode.Ok);
            }

            await SessionManager.Current.CreateOrUpdateSessionAsync(_sessionId, session);

            var packet = new byte[RAOP_PACKET_LENGTH];
            var packetProcessingCount = 0;
            var framesProcessed = 0;

            do
            {
                try
                {
                    var dret = dSocket.Receive(packet, 0, RAOP_PACKET_LENGTH, SocketFlags.None, out SocketError error);
                    if (error != SocketError.Success)
                    {
                        continue;
                    }

                    packetProcessingCount++;
                    
                    using var packetActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AudioDataPacketProcessing",
                        ActivityKind.Internal,
                        sessionId: _sessionId);
                    
                    packetActivity?.SetTag("audio.data.packet.number", packetProcessingCount);
                    packetActivity?.SetTag("audio.data.packet.size", dret);

                    // RTP payload type
                    int type_d = packet[1] & ~0x80;
                    packetActivity?.SetTag("audio.data.packet.type", type_d);

                    if (packet.Length >= 12)
                    {
                        InitAesCbcCipher(session.DecryptedAesKey, session.EcdhShared, session.AesIv);

                        bool no_resend = false;
                        int buf_ret;
                        byte[] audiobuf;
                        int audiobuflen = 0;
                        uint timestamp = 0;
                        
                        var stopwatch = Stopwatch.StartNew();
                        
                        // Queue the data packet
                        using (var queueActivity = AirPlayTelemetry.CreateSessionActivity(
                            "AudioDataBufferQueue",
                            ActivityKind.Internal,
                            sessionId: _sessionId))
                        {
                            buf_ret = RaopBufferQueue(_raopBuffer, packet, (ushort)dret, session);
                            queueActivity?.SetTag("audio.buffer.queue.result", buf_ret);
                            queueActivity?.SetStatus(ActivityStatusCode.Ok);
                        }

                        // Dequeue audio data from buffer
                        using (var dequeueActivity = AirPlayTelemetry.CreateSessionActivity(
                            "AudioDataBufferDequeue",
                            ActivityKind.Internal,
                            sessionId: _sessionId))
                        {
                            dequeueActivity?.SetTag("audio.buffer.first_seq", _raopBuffer.FirstSeqNum);
                            dequeueActivity?.SetTag("audio.buffer.last_seq", _raopBuffer.LastSeqNum);
                            
                            var frameCount = 0;
                        
                            // Dequeue all frames in queue
                            while ((audiobuf = RaopBufferDequeue(_raopBuffer, ref audiobuflen, ref timestamp, no_resend)) != null)
                            {
                                frameCount++;
                                
                                if (audiobuf.Length == 0)
                                {
                                    continue; // Skip empty buffers
                                }

                                framesProcessed++;
                                
                                using var frameActivity = AirPlayTelemetry.CreateSessionActivity(
                                    "AudioFrameProcessing",
                                    ActivityKind.Producer,
                                    sessionId: _sessionId);
                                    
                                frameActivity?.SetTag("audio.frame.number", framesProcessed);
                                frameActivity?.SetTag("audio.frame.size", audiobuf.Length);
                                frameActivity?.SetTag("audio.frame.timestamp", timestamp);
                                
                                var pcmData = new PcmData();
                                pcmData.Length = 960;
                                pcmData.Data = audiobuf;
                                pcmData.Pts = (ulong)(timestamp - _sync_timestamp) * 1000000UL / 44100 + _sync_time;
                                
                                frameActivity?.SetTag("audio.frame.pts", pcmData.Pts);
                                
                                _receiver.OnPCMData(pcmData);
                                
                                // Record metrics
                                AirPlayTelemetry.AudioPacketsProcessed.Add(1);
                                
                                frameActivity?.SetStatus(ActivityStatusCode.Ok);
                            }
                            
                            dequeueActivity?.SetTag("audio.buffer.frames_dequeued", frameCount);
                            dequeueActivity?.SetStatus(ActivityStatusCode.Ok);
                        }

                        // Handle possible resend requests
                        if (!no_resend)
                        {
                            using var resendActivity = AirPlayTelemetry.CreateSessionActivity(
                                "AudioDataHandleResends",
                                ActivityKind.Internal,
                                sessionId: _sessionId);
                                
                            RaopBufferHandleResends(_raopBuffer, _cSocket, _controlSequenceNumber);
                            resendActivity?.SetStatus(ActivityStatusCode.Ok);
                        }
                        
                        stopwatch.Stop();
                        packetActivity?.SetTag("audio.packet.processing_time_ms", stopwatch.ElapsedMilliseconds);
                        
                        // Record metrics for packet processing time
                        AirPlayTelemetry.AudioPacketProcessingTime.Record(stopwatch.Elapsed.TotalMilliseconds);
                    }

                    packet = new byte[RAOP_PACKET_LENGTH];
                    packetActivity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (Exception ex)
                {
                    activity?.AddException(ex);
                }
            } while (!cancellationToken.IsCancellationRequested);

            activity?.AddEvent(new ActivityEvent("AudioDataSocketClosed"));
            activity?.SetTag("audio.frames.total_processed", framesProcessed);
            Console.WriteLine($"Closing audio data socket..");
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        public Task FlushAsync(int nextSequence)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.Flush", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            activity?.SetTag("audio.flush.next_sequence", nextSequence);
            
            RaopBufferFlush(_raopBuffer, nextSequence);
            
            activity?.SetStatus(ActivityStatusCode.Ok);
            return Task.CompletedTask;
        }

        private void InitAesCbcCipher(byte[] aesKey, byte[] ecdhShared, byte[] aesIv)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.InitCipher", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            try 
            {
                byte[] hash = Utilities.Hash(aesKey, ecdhShared);
                byte[] eaesKey = Utilities.CopyOfRange(hash, 0, 16);

                var keyParameter = ParameterUtilities.CreateKeyParameter("AES", eaesKey);
                var cipherParameters = new ParametersWithIV(keyParameter, aesIv, 0, aesIv.Length);

                _aesCbcDecrypt.Init(false, cipherParameters);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private RaopBuffer RaopBufferInit()
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.BufferInit", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            var audio_buffer_size = 480 * 4;
            var raop_buffer = new RaopBuffer();

            raop_buffer.BufferSize = audio_buffer_size * RAOP_BUFFER_LENGTH;
            raop_buffer.Buffer = new byte[raop_buffer.BufferSize];

            activity?.SetTag("audio.buffer.size", raop_buffer.BufferSize);
            activity?.SetTag("audio.buffer.length", RAOP_BUFFER_LENGTH);
            activity?.SetTag("audio.buffer.entry_size", audio_buffer_size);

            for (int i=0; i < RAOP_BUFFER_LENGTH; i++) {
		        var entry = raop_buffer.Entries[i];
                entry.AudioBufferSize = audio_buffer_size;
		        entry.AudioBufferLen = 0;
		        entry.AudioBuffer = (byte[]) raop_buffer.Buffer.Skip(i).Take(audio_buffer_size).ToArray();

                raop_buffer.Entries[i] = entry;
            }

            raop_buffer.IsEmpty = true;
            activity?.SetStatus(ActivityStatusCode.Ok);
	        return raop_buffer;
        }

        public int RaopBufferQueue(RaopBuffer raop_buffer, byte[] data, ushort datalen, Session session)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.BufferQueue", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            activity?.SetTag("audio.queue.data_length", datalen);

            try
            {
                int encryptedlen;
                RaopBufferEntry entry;

                /* Check packet data length is valid */
                if (datalen < 12 || datalen > RAOP_PACKET_LENGTH)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Invalid packet data length");
                    return -1;
                }

                var seqnum = (ushort)((data[2] << 8) | data[3]);
                activity?.SetTag("audio.queue.sequence", seqnum);
                
                if (datalen == 16 && data[12] == 0x0 && data[13] == 0x68 && data[14] == 0x34 && data[15] == 0x0)
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return 0;
                }

                // Ignore, old
                if (!raop_buffer.IsEmpty && seqnum < raop_buffer.FirstSeqNum && seqnum != 0)
                {
                    activity?.SetTag("audio.queue.ignored_old", true);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return 0;
                }

                /* Check that there is always space in the buffer, otherwise flush */
                if (raop_buffer.FirstSeqNum + RAOP_BUFFER_LENGTH < seqnum || seqnum == 0)
                {
                    activity?.AddEvent(new ActivityEvent("BufferFlushRequired"));
                    RaopBufferFlush(raop_buffer, seqnum);
                }

                entry = raop_buffer.Entries[seqnum % RAOP_BUFFER_LENGTH];
                if (entry.Available && entry.SeqNum == seqnum)
                {
                    /* Packet resent, we can safely ignore */
                    activity?.SetTag("audio.queue.resent_packet", true);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return 0;
                }

                entry.Flags = data[0];
                entry.Type = data[1];
                entry.SeqNum = seqnum;

                entry.TimeStamp = (uint)((data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7]);
                entry.SSrc = (uint)((data[8] << 24) | (data[9] << 16) | (data[10] << 8) | data[11]);
                entry.Available = true;

                int payloadsize = datalen - 12;
                activity?.SetTag("audio.queue.payload_size", payloadsize);
                
                var raw = new byte[payloadsize];

                encryptedlen = payloadsize / 16 * 16;
                activity?.SetTag("audio.queue.encrypted_length", encryptedlen);

                if (encryptedlen > 0)
                {
                    _aesCbcDecrypt.ProcessBytes(data, 12, encryptedlen, data, 12);
                    Array.Copy(data, 12, raw, 0, encryptedlen);
                }

                Array.Copy(data, 12 + encryptedlen, raw, encryptedlen, payloadsize - encryptedlen);

#if DUMP
                /* RAW -> DUMP */
                var fPath = Path.Combine(_dConfig.Path, "frames/");
                File.WriteAllBytes($"{fPath}raw_{seqnum}", raw);
#endif
                /* RAW -> PCM */
                var stopwatch = Stopwatch.StartNew();
                
                var length = _decoder.GetOutputStreamLength();
                var output = new byte[length];

                activity?.SetTag("audio.queue.output_length", length);

                var res = _decoder.DecodeFrame(raw, ref output, length);
                
                stopwatch.Stop();
                activity?.SetTag("audio.decoding.time_ms", stopwatch.ElapsedMilliseconds);
                
                if (res != 0)
                {
                    output = new byte[length];
                    activity?.SetTag("audio.decode.error_code", res);
                    activity?.SetTag("audio.decoder.type", _decoder.Type);
                    Console.WriteLine($"Decoding error. Decoder: {_decoder.Type} Code: {res}");
                }

#if DUMP
                var pPath = Path.Combine(_dConfig.Path, "pcm/");
                Console.WriteLine($"RES: {res}");
                Console.WriteLine($"PCM: {output.Length}");
                Console.WriteLine($"LNG: {length}");
                File.WriteAllBytes($"{pPath}raw_{seqnum}", output);
#endif
                Array.Copy(output, 0, entry.AudioBuffer, 0, output.Length);
                entry.AudioBufferLen = output.Length;

                /* Update the raop_buffer seqnums */
                if (raop_buffer.IsEmpty)
                {
                    raop_buffer.FirstSeqNum = seqnum;
                    raop_buffer.LastSeqNum = seqnum;
                    raop_buffer.IsEmpty = false;
                    
                    activity?.AddEvent(new ActivityEvent("BufferInitialized"));
                }

                if (raop_buffer.LastSeqNum < seqnum)
                {
                    raop_buffer.LastSeqNum = seqnum;
                }

                // Update entries
                raop_buffer.Entries[seqnum % RAOP_BUFFER_LENGTH] = entry;

                activity?.SetStatus(ActivityStatusCode.Ok);
                return 1;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        public byte[] RaopBufferDequeue(RaopBuffer raop_buffer, ref int length, ref uint pts, bool noResend)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.BufferDequeue", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            activity?.SetTag("audio.dequeue.no_resend", noResend);

            try
            {
                short buflen;
                RaopBufferEntry entry;

                /* Calculate number of entries in the current buffer */
                buflen = (short)(raop_buffer.LastSeqNum - raop_buffer.FirstSeqNum + 1);
                activity?.SetTag("audio.buffer.length", buflen);

                /* Cannot dequeue from empty buffer */
                if (raop_buffer.IsEmpty || buflen <= 0)
                {
                    activity?.SetTag("audio.buffer.empty", true);
                    activity?.SetStatus(ActivityStatusCode.Ok, "Buffer empty");
                    return null;
                }

                /* Get the first buffer entry for inspection */
                entry = raop_buffer.Entries[raop_buffer.FirstSeqNum % RAOP_BUFFER_LENGTH];
                activity?.SetTag("audio.buffer.first_sequence", raop_buffer.FirstSeqNum);
                activity?.SetTag("audio.buffer.entry_available", entry.Available);

                if (noResend)
                {
                    activity?.AddEvent(new ActivityEvent("NoResendDequeue"));
                    
                    /* If we do no resends, always return the first entry */
                    entry.Available = false;

                    /* Return entry audio buffer */
                    length = entry.AudioBufferLen;
                    pts = entry.TimeStamp;
                    entry.AudioBufferLen = 0;

                    raop_buffer.Entries[raop_buffer.FirstSeqNum % RAOP_BUFFER_LENGTH] = entry;
                    raop_buffer.FirstSeqNum += 1;

                    activity?.SetTag("audio.dequeue.length", length);
                    activity?.SetTag("audio.dequeue.timestamp", pts);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    
                    return entry.AudioBuffer;
                }
                else if (!entry.Available)
                {
                    /* Check how much we have space left in the buffer */
                    if (buflen < RAOP_BUFFER_LENGTH)
                    {
                        activity?.AddEvent(new ActivityEvent("WaitForResend"));
                        
                        /* Return nothing and hope resend gets on time */
                        length = entry.AudioBufferSize;
                        
                        activity?.SetTag("audio.dequeue.empty_buffer_size", length);
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        
                        return Array.Empty<byte>();
                    }
                    
                    /* Risk of buffer overrun, return empty buffer */
                    activity?.AddEvent(new ActivityEvent("BufferOverrunRisk"));
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    
                    return Array.Empty<byte>();
                }

                /* Update buffer and validate entry */
                if (!entry.Available)
                {
                    activity?.AddEvent(new ActivityEvent("EntryNotAvailable"));
                    
                    /* Return an empty audio buffer to skip audio */
                    length = entry.AudioBufferSize;
                    
                    activity?.SetTag("audio.dequeue.empty_buffer_size", length);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    
                    return Array.Empty<byte>();
                }
                
                entry.Available = false;

                /* Return entry audio buffer */
                length = entry.AudioBufferLen;
                pts = entry.TimeStamp;
                entry.AudioBufferLen = 0;

                raop_buffer.Entries[raop_buffer.FirstSeqNum % RAOP_BUFFER_LENGTH] = entry;
                raop_buffer.FirstSeqNum += 1;

                activity?.SetTag("audio.dequeue.length", length);
                activity?.SetTag("audio.dequeue.timestamp", pts);
                activity?.SetStatus(ActivityStatusCode.Ok);
                
                return entry.AudioBuffer.Take(length).ToArray();
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private void RaopBufferFlush(RaopBuffer raop_buffer, int next_seq)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.BufferFlush", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            activity?.SetTag("audio.flush.next_sequence", next_seq);

            try
            {
                for (int i = 0; i < RAOP_BUFFER_LENGTH; i++)
                {
                    raop_buffer.Entries[i].Available = false;
                    raop_buffer.Entries[i].AudioBufferLen = 0;
                }
                
                raop_buffer.IsEmpty = true;
                
                if (next_seq > 0 && next_seq < 0xffff)
                {
                    raop_buffer.FirstSeqNum = (ushort)next_seq;
                    raop_buffer.LastSeqNum = (ushort)(next_seq - 1);
                    
                    activity?.SetTag("audio.buffer.first_seq_after_flush", raop_buffer.FirstSeqNum);
                    activity?.SetTag("audio.buffer.last_seq_after_flush", raop_buffer.LastSeqNum);
                }
                
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private void RaopBufferHandleResends(RaopBuffer raop_buffer, Socket cSocket, ushort control_seqnum)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.HandleResends", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            try
            {
                RaopBufferEntry entry;

                if (Utilities.SeqNumCmp(raop_buffer.FirstSeqNum, raop_buffer.LastSeqNum) < 0)
                {
                    int seqnum, count;

                    for (seqnum = raop_buffer.FirstSeqNum; Utilities.SeqNumCmp(seqnum, raop_buffer.LastSeqNum) < 0; seqnum++)
                    {
                        entry = raop_buffer.Entries[seqnum % RAOP_BUFFER_LENGTH];
                        if (entry.Available)
                        {
                            break;
                        }
                    }
                    
                    if (Utilities.SeqNumCmp(seqnum, raop_buffer.FirstSeqNum) == 0)
                    {
                        activity?.SetTag("audio.resend.no_resends_needed", true);
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        return;
                    }
                    
                    count = Utilities.SeqNumCmp(seqnum, raop_buffer.FirstSeqNum);
                    
                    activity?.SetTag("audio.resend.first_seq", raop_buffer.FirstSeqNum);
                    activity?.SetTag("audio.resend.count", count);
                    
                    RaopRtpResendCallback(cSocket, control_seqnum, raop_buffer.FirstSeqNum, (ushort)count);
                }
                
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private int RaopRtpResendCallback(Socket cSocket, ushort control_seqnum, ushort seqnum, ushort count)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.ResendCallback", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            activity?.SetTag("audio.resend.control_sequence", control_seqnum);
            activity?.SetTag("audio.resend.sequence", seqnum);
            activity?.SetTag("audio.resend.count", count);

            try
            {
                var packet = new byte[8];
                ushort ourseqnum;

                ourseqnum = control_seqnum++;

                /* Fill the request buffer */
                packet[0] = 0x80;
                packet[1] = 0x55|0x80;
                packet[2] = (byte)(ourseqnum >> 8);
                packet[3] = (byte)ourseqnum;
                packet[4] = (byte)(seqnum >> 8);
                packet[5] = (byte)seqnum;
                packet[6] = (byte)(count >> 8);
                packet[7] = (byte)count;

                var ret = cSocket.Send(packet, 0, packet.Length, SocketFlags.None);
                activity?.SetTag("audio.resend.bytes_sent", ret);
                
                if (ret == -1) {
                    activity?.SetStatus(ActivityStatusCode.Error, "Failed to send resend request");
                    Console.WriteLine("Resend packet - failed to send request");
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private void InitializeDecoder(AudioFormat audioFormat)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AudioListener.InitializeDecoder", 
                ActivityKind.Internal,
                sessionId: _sessionId);
                
            activity?.SetTag("audio.decoder.format", audioFormat.ToString());

            try
            {
                if (_decoder != null)
                {
                    activity?.SetTag("audio.decoder.already_initialized", true);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    return;
                }

                if (audioFormat == AudioFormat.ALAC)
                {
                    var frameLength = 352;
                    var numChannels = 2;
                    var bitDepth = 16;
                    var sampleRate = 44100;
                    
                    activity?.SetTag("audio.decoder.type", "ALAC");
                    activity?.SetTag("audio.decoder.frame_length", frameLength);
                    activity?.SetTag("audio.decoder.channels", numChannels);
                    activity?.SetTag("audio.decoder.bit_depth", bitDepth);
                    activity?.SetTag("audio.decoder.sample_rate", sampleRate);

                    _decoder = new ALACDecoder(_clConfig.ALACLibPath);
                    _decoder.Config(sampleRate, numChannels, bitDepth, frameLength);
                }
                else if (audioFormat == AudioFormat.AAC)
                {
                    var frameLength = 1024;
                    var numChannels = 2;
                    var bitDepth = 16;
                    var sampleRate = 44100;
                    
                    activity?.SetTag("audio.decoder.type", "AAC-MAIN");
                    activity?.SetTag("audio.decoder.frame_length", frameLength);
                    activity?.SetTag("audio.decoder.channels", numChannels);
                    activity?.SetTag("audio.decoder.bit_depth", bitDepth);
                    activity?.SetTag("audio.decoder.sample_rate", sampleRate);

                    _decoder = new AACDecoder(_clConfig.AACLibPath, TransportType.TT_MP4_RAW, AudioObjectType.AOT_AAC_MAIN, 1);
                    _decoder.Config(sampleRate, numChannels, bitDepth, frameLength);
                }
                else if(audioFormat == AudioFormat.AAC_ELD)
                {
                    var ex = new Exception("AAC-ELD is not supported currently.");
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.AddException(ex);
                    throw ex;
                }
                else
                {
                    activity?.SetTag("audio.decoder.type", "PCM");
                    _decoder = new PCMDecoder();
                }
                
                activity?.SetStatus(ActivityStatusCode.Ok);
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

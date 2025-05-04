using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.DmapTagged;
using AirPlay.Models;
using AirPlay.Models.Configs;
using AirPlay.Models.Enums;
using AirPlay.Services.Implementations;
using AirPlay.Telemetry;
using AirPlay.Utils;
using Claunia.PropertyList;

namespace AirPlay.Listeners
{
    public class AirTunesListener : BaseTcpListener
    {
        private readonly IRtspReceiver _receiver;
        private readonly ushort _airTunesPort;
        private readonly ushort _airPlayPort;
        private readonly byte[] _publicKey;
        private readonly byte[] _expandedPrivateKey;
        private readonly byte[] _fpHeader = new byte[] { 0x46, 0x50, 0x4c, 0x59, 0x03, 0x01, 0x04, 0x00, 0x00, 0x00, 0x00, 0x14 };
        private readonly byte[][] _replyMessage = new byte[][]
        {
            new byte[] { 0x46,0x50,0x4c,0x59,0x03,0x01,0x02,0x00,0x00,0x00,0x00,0x82,0x02,0x00,0x0f,0x9f,0x3f,0x9e,0x0a,0x25,0x21,0xdb,0xdf,0x31,0x2a,0xb2,0xbf,0xb2,0x9e,0x8d,0x23,0x2b,0x63,0x76,0xa8,0xc8,0x18,0x70,0x1d,0x22,0xae,0x93,0xd8,0x27,0x37,0xfe,0xaf,0x9d,0xb4,0xfd,0xf4,0x1c,0x2d,0xba,0x9d,0x1f,0x49,0xca,0xaa,0xbf,0x65,0x91,0xac,0x1f,0x7b,0xc6,0xf7,0xe0,0x66,0x3d,0x21,0xaf,0xe0,0x15,0x65,0x95,0x3e,0xab,0x81,0xf4,0x18,0xce,0xed,0x09,0x5a,0xdb,0x7c,0x3d,0x0e,0x25,0x49,0x09,0xa7,0x98,0x31,0xd4,0x9c,0x39,0x82,0x97,0x34,0x34,0xfa,0xcb,0x42,0xc6,0x3a,0x1c,0xd9,0x11,0xa6,0xfe,0x94,0x1a,0x8a,0x6d,0x4a,0x74,0x3b,0x46,0xc3,0xa7,0x64,0x9e,0x44,0xc7,0x89,0x55,0xe4,0x9d,0x81,0x55,0x00,0x95,0x49,0xc4,0xe2,0xf7,0xa3,0xf6,0xd5,0xba},
            new byte[] { 0x46,0x50,0x4c,0x59,0x03,0x01,0x02,0x00,0x00,0x00,0x00,0x82,0x02,0x01,0xcf,0x32,0xa2,0x57,0x14,0xb2,0x52,0x4f,0x8a,0xa0,0xad,0x7a,0xf1,0x64,0xe3,0x7b,0xcf,0x44,0x24,0xe2,0x00,0x04,0x7e,0xfc,0x0a,0xd6,0x7a,0xfc,0xd9,0x5d,0xed,0x1c,0x27,0x30,0xbb,0x59,0x1b,0x96,0x2e,0xd6,0x3a,0x9c,0x4d,0xed,0x88,0xba,0x8f,0xc7,0x8d,0xe6,0x4d,0x91,0xcc,0xfd,0x5c,0x7b,0x56,0xda,0x88,0xe3,0x1f,0x5c,0xce,0xaf,0xc7,0x43,0x19,0x95,0xa0,0x16,0x65,0xa5,0x4e,0x19,0x39,0xd2,0x5b,0x94,0xdb,0x64,0xb9,0xe4,0x5d,0x8d,0x06,0x3e,0x1e,0x6a,0xf0,0x7e,0x96,0x56,0x16,0x2b,0x0e,0xfa,0x40,0x42,0x75,0xea,0x5a,0x44,0xd9,0x59,0x1c,0x72,0x56,0xb9,0xfb,0xe6,0x51,0x38,0x98,0xb8,0x02,0x27,0x72,0x19,0x88,0x57,0x16,0x50,0x94,0x2a,0xd9,0x46,0x68,0x8a},
            new byte[] { 0x46,0x50,0x4c,0x59,0x03,0x01,0x02,0x00,0x00,0x00,0x00,0x82,0x02,0x02,0xc1,0x69,0xa3,0x52,0xee,0xed,0x35,0xb1,0x8c,0xdd,0x9c,0x58,0xd6,0x4f,0x16,0xc1,0x51,0x9a,0x89,0xeb,0x53,0x17,0xbd,0x0d,0x43,0x36,0xcd,0x68,0xf6,0x38,0xff,0x9d,0x01,0x6a,0x5b,0x52,0xb7,0xfa,0x92,0x16,0xb2,0xb6,0x54,0x82,0xc7,0x84,0x44,0x11,0x81,0x21,0xa2,0xc7,0xfe,0xd8,0x3d,0xb7,0x11,0x9e,0x91,0x82,0xaa,0xd7,0xd1,0x8c,0x70,0x63,0xe2,0xa4,0x57,0x55,0x59,0x10,0xaf,0x9e,0x0e,0xfc,0x76,0x34,0x7d,0x16,0x40,0x43,0x80,0x7f,0x58,0x1e,0xe4,0xfb,0xe4,0x2c,0xa9,0xde,0xdc,0x1b,0x5e,0xb2,0xa3,0xaa,0x3d,0x2e,0xcd,0x59,0xe7,0xee,0xe7,0x0b,0x36,0x29,0xf2,0x2a,0xfd,0x16,0x1d,0x87,0x73,0x53,0xdd,0xb9,0x9a,0xdc,0x8e,0x07,0x00,0x6e,0x56,0xf8,0x50,0xce},
            new byte[] { 0x46,0x50,0x4c,0x59,0x03,0x01,0x02,0x00,0x00,0x00,0x00,0x82,0x02,0x03,0x90,0x01,0xe1,0x72,0x7e,0x0f,0x57,0xf9,0xf5,0x88,0x0d,0xb1,0x04,0xa6,0x25,0x7a,0x23,0xf5,0xcf,0xff,0x1a,0xbb,0xe1,0xe9,0x30,0x45,0x25,0x1a,0xfb,0x97,0xeb,0x9f,0xc0,0x01,0x1e,0xbe,0x0f,0x3a,0x81,0xdf,0x5b,0x69,0x1d,0x76,0xac,0xb2,0xf7,0xa5,0xc7,0x08,0xe3,0xd3,0x28,0xf5,0x6b,0xb3,0x9d,0xbd,0xe5,0xf2,0x9c,0x8a,0x17,0xf4,0x81,0x48,0x7e,0x3a,0xe8,0x63,0xc6,0x78,0x32,0x54,0x22,0xe6,0xf7,0x8e,0x16,0x6d,0x18,0xaa,0x7f,0xd6,0x36,0x25,0x8b,0xce,0x28,0x72,0x6f,0x66,0x1f,0x73,0x88,0x93,0xce,0x44,0x31,0x1e,0x4b,0xe6,0xc0,0x53,0x51,0x93,0xe5,0xef,0x72,0xe8,0x68,0x62,0x33,0x72,0x9c,0x22,0x7d,0x82,0x0c,0x99,0x94,0x45,0xd8,0x92,0x46,0xc8,0xc3,0x59}
        };
        private readonly CodecLibrariesConfig _codecConfig;
        private readonly DumpConfig _dumpConfig;

        public AirTunesListener(IRtspReceiver receiver, ushort port, ushort airPlayPort, CodecLibrariesConfig codecConfig, DumpConfig dumpConfig) : base(port)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity("AirTunesListener.Constructor", ActivityKind.Internal);
            
            _airTunesPort = port;
            _airPlayPort = airPlayPort;
            _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
            _codecConfig = codecConfig ?? throw new ArgumentNullException(nameof(codecConfig));
            _dumpConfig = dumpConfig ?? throw new ArgumentNullException(nameof(dumpConfig));

            activity?.SetTag("airplay.port", airPlayPort);
            activity?.SetTag("airtunes.port", port);

            // First time that we instantiate AirPlayListener we must create a ED25519 KeyPair
            // var seed = new byte[32];
            // RNGCryptoServiceProvider.Create().GetBytes(seed);
            var seed = Enumerable.Range(0, 32).Select(r => (byte)r).ToArray();
            
            using (var keyGenActivity = AirPlayTelemetry.CreateSessionActivity("AirTunesListener.KeyGeneration", ActivityKind.Internal))
            {
                Chaos.NaCl.Ed25519.KeyPairFromSeed(out byte[] publicKey, out byte[] expandedPrivateKey, seed);
                _publicKey = publicKey;
                _expandedPrivateKey = expandedPrivateKey;
                
                keyGenActivity?.SetTag("key.public_length", publicKey.Length);
                keyGenActivity?.SetStatus(ActivityStatusCode.Ok);
            }
            
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        public override async Task OnDataReceivedAsync(Request request, Response response, CancellationToken cancellationToken)
        {
            // Get session by active-remote header value
            var sessionId = request.Headers["Active-Remote"];
            
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AirTunesListener.ProcessRequest", 
                ActivityKind.Consumer, 
                sessionId: sessionId);
                
            activity?.SetTag("request.type", request.Type.ToString());
            activity?.SetTag("request.path", request.Path);
            activity?.SetTag("request.body_length", request.Body?.Length ?? 0);

            try
            {
                var session = await SessionManager.Current.GetSessionAsync(sessionId);

                if (request.Type == RequestType.GET && "/info".Equals(request.Path, StringComparison.OrdinalIgnoreCase))
                {
                    using var infoActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessInfoRequest", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    var dict = new Dictionary<string, object>();
                    dict.Add("features", 61379444727);
                    dict.Add("name", "airserver");
                    dict.Add("displays", new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            { "primaryInputDevice", 1 },
                            { "rotation", true },
                            { "widthPhysical", 0 },
                            { "edid", "AP///////wAGEBOuhXxiyAoaAQS1PCJ4IA8FrlJDsCYOT1QAAAABAQEBAQEBAQEBAQEBAQEBAAAAEAAAAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAA/ABpTWFjCiAgICAgICAgAAAAAAAAAAAAAAAAAAAAAAAAAqBwE3kDAAMAFIBuAYT/E58AL4AfAD8LUQACAAQAf4EY+hAAAQEAEnYx/Hj7/wIQiGLT+vj4/v//AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADHkHATeQMAAwFQU+wABP8PnwAvAB8A/whBAAIABABM0AAE/w6fAC8AHwBvCD0AAgAEAMyRAAR/DJ8ALwAfAAcHMwACAAQAVV4ABP8JnwAvAB8AnwUoAAIABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB+Q" },
                            { "widthPixels", 1920.0 },
                            { "uuid", "061013ae-7b0f-4305-984b-974f677a150b" },
                            { "heightPhysical", 0 },
                            { "features", 30 },
                            { "heightPixels", 1080.0 },
                            { "overscanned", false }
                        }
                    });
                    dict.Add("audioFormats", new List<Dictionary<string, object>>
                    {
                        {
                            new Dictionary<string, object> {
                                { "type", 100 },
                                { "audioInputFormats", 67108860 },
                                { "audioOutputFormats", 67108860 }
                            }
                        },
                        {
                            new Dictionary<string, object> {
                                { "type", 101 },
                                { "audioInputFormats", 67108860 },
                                { "audioOutputFormats", 67108860 }
                            }
                        }
                    });
                    dict.Add("vv", 2);
                    dict.Add("statusFlags", 4);
                    dict.Add("keepAliveLowPower", true);
                    dict.Add("sourceVersion", "220.68");
                    dict.Add("pk", "29fbb183a58b466e05b9ab667b3c429d18a6b785637333d3f0f3a34baa89f45c");
                    dict.Add("keepAliveSendStatsAsBody", true);
                    dict.Add("deviceID", "78:7B:8A:BD:C9:4D");
                    dict.Add("model", "AppleTV5,3");
                    dict.Add("audioLatencies", new List<Dictionary<string, object>>
                    {
                        {
                            new Dictionary<string, object> {
                                { "outputLatencyMicros", 0 },
                                { "type", 100 },
                                { "audioType", "default" },
                                { "inputLatencyMicros", 0 }
                            }
                        },
                        {
                            new Dictionary<string, object> {
                                { "outputLatencyMicros", 0 },
                                { "type", 101 },
                                { "audioType", "default" },
                                { "inputLatencyMicros", 0 }
                            }
                        }
                    });
                    dict.Add("macAddress", "78:7B:8A:BD:C9:4D");

                    var output = default(byte[]);

                    // OLD CODE using homemade plist writer
                    //using (var outputStream = new MemoryStream())
                    //{
                    //    var plistWriter = new BinaryPlistWriter();
                    //    plistWriter.WriteObject(outputStream, dict, false);
                    //    outputStream.Seek(0, SeekOrigin.Begin);

                    //    output = outputStream.ToArray();
                    //}

                    // New Code using plist-cli
                    var binaryPlist = NSObject.Wrap(dict);
                    var plistBytes = BinaryPropertyListWriter.WriteToArray(binaryPlist);

                    response.Headers.Add("Content-Type", "application/x-apple-binary-plist");
                    await response.WriteAsync(plistBytes, 0, plistBytes.Length).ConfigureAwait(false);
                    
                    infoActivity?.SetTag("response.content_type", "application/x-apple-binary-plist");
                    infoActivity?.SetTag("response.size", plistBytes.Length);
                    infoActivity?.SetStatus(ActivityStatusCode.Ok);
                }

                if (request.Type == RequestType.POST && "/pair-setup".Equals(request.Path, StringComparison.OrdinalIgnoreCase))
                {
                    using var pairSetupActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessPairSetupRequest", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    // Return our 32 bytes public key
                    response.Headers.Add("Content-Type", "application/octet-stream");
                    await response.WriteAsync(_publicKey, 0, _publicKey.Length).ConfigureAwait(false);
                    
                    pairSetupActivity?.SetTag("response.key_size", _publicKey.Length);
                    pairSetupActivity?.SetStatus(ActivityStatusCode.Ok);
                }

                if (request.Type == RequestType.POST && "/pair-verify".Equals(request.Path, StringComparison.OrdinalIgnoreCase))
                {
                    using var pairVerifyActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessPairVerifyRequest", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    using (var mem = new MemoryStream(request.Body))
                    using (var reader = new BinaryReader(mem))
                    {
                        var flag = reader.ReadByte();
                        pairVerifyActivity?.SetTag("pair_verify.flag", flag);
                        
                        if (flag > 0)
                        {
                            using var step1Activity = AirPlayTelemetry.CreateSessionActivity(
                                "PairVerify.Step1", 
                                ActivityKind.Internal, 
                                sessionId: sessionId);
                                
                            reader.ReadBytes(3);
                            session.EcdhTheirs = reader.ReadBytes(32);
                            session.EdTheirs = reader.ReadBytes(32);
                            
                            step1Activity?.SetTag("ecdh_theirs.length", session.EcdhTheirs.Length);
                            step1Activity?.SetTag("ed_theirs.length", session.EdTheirs.Length);
                            
                            // Generate our key pairs
                            (var ecdhPrivateKey, session.EcdhOurs) = NaCl.Curve25519XSalsa20Poly1305.KeyPair();
                            session.EcdhShared = NaCl.Curve25519.ScalarMultiplication(ecdhPrivateKey, session.EcdhTheirs);
                            
                            step1Activity?.SetTag("ecdh_ours.length", session.EcdhOurs.Length);
                            step1Activity?.SetTag("ecdh_shared.length", session.EcdhShared.Length);
                            
                            // Initialize cipher with shared key
                            var aesCtr128Encrypt = Utilities.InitializeChiper(session.EcdhShared);
                            
                            // Sign data using our private key
                            byte[] dataToSign = new byte[64];
                            Array.Copy(session.EcdhOurs, 0, dataToSign, 0, 32);
                            Array.Copy(session.EcdhTheirs, 0, dataToSign, 32, 32);
                            
                            var signature = Chaos.NaCl.Ed25519.Sign(dataToSign, _expandedPrivateKey);
                            step1Activity?.SetTag("signature.length", signature.Length);
                            
                            // Encrypt signature
                            byte[] encryptedSignature = aesCtr128Encrypt.DoFinal(signature);
                            
                            // Prepare response
                            byte[] output = new byte[session.EcdhOurs.Length + encryptedSignature.Length];
                            Array.Copy(session.EcdhOurs, 0, output, 0, session.EcdhOurs.Length);
                            Array.Copy(encryptedSignature, 0, output, session.EcdhOurs.Length, encryptedSignature.Length);
                            
                            step1Activity?.SetTag("response.length", output.Length);
                            
                            response.Headers.Add("Content-Type", "application/octet-stream");
                            await response.WriteAsync(output, 0, output.Length).ConfigureAwait(false);
                            
                            step1Activity?.SetStatus(ActivityStatusCode.Ok);
                        }
                        else
                        {
                            using var step2Activity = AirPlayTelemetry.CreateSessionActivity(
                                "PairVerify.Step2", 
                                ActivityKind.Internal, 
                                sessionId: sessionId);
                                
                            reader.ReadBytes(3);
                            var signature = reader.ReadBytes(64);
                            step2Activity?.SetTag("signature.length", signature.Length);
                            
                            var aesCtr128Encrypt = Utilities.InitializeChiper(session.EcdhShared);
                            
                            var signatureBuffer = new byte[64];
                            signatureBuffer = aesCtr128Encrypt.ProcessBytes(signatureBuffer);
                            signatureBuffer = aesCtr128Encrypt.DoFinal(signature);
                            
                            byte[] messageBuffer = new byte[64];
                            Array.Copy(session.EcdhTheirs, 0, messageBuffer, 0, 32);
                            Array.Copy(session.EcdhOurs, 0, messageBuffer, 32, 32);
                            
                            session.PairVerified = Chaos.NaCl.Ed25519.Verify(signatureBuffer, messageBuffer, session.EdTheirs);
                            step2Activity?.SetTag("pair_verified", session.PairVerified);
                            
                            Console.WriteLine($"PairVerified: {session.PairVerified}");
                            
                            step2Activity?.SetStatus(ActivityStatusCode.Ok);
                        }
                        
                        pairVerifyActivity?.SetTag("pair.verified", session.PairVerified);
                        pairVerifyActivity?.SetStatus(ActivityStatusCode.Ok);
                    }
                }

                if (request.Type == RequestType.POST && "/fp-setup".Equals(request.Path, StringComparison.OrdinalIgnoreCase))
                {
                    using var fpSetupActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessFairPlaySetup", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    // If session is not paired, something gone wrong.
                    if (!session.PairCompleted)
                    {
                        fpSetupActivity?.SetTag("pair.completed", false);
                        fpSetupActivity?.SetStatus(ActivityStatusCode.Error, "Session not paired");
                        response.StatusCode = StatusCode.UNAUTHORIZED;
                    }
                    else
                    {
                        var body = request.Body;
                        fpSetupActivity?.SetTag("body.length", body.Length);
                        
                        if (body.Length == 16)
                        {
                            using var setupStep1Activity = AirPlayTelemetry.CreateSessionActivity(
                                "FairPlaySetup.Step1", 
                                ActivityKind.Internal, 
                                sessionId: sessionId);
                                
                            // Response must be 142 bytes
                            var mode = body[14];
                            setupStep1Activity?.SetTag("fairplay.mode", mode);
                            
                            if (body[4] != 0x03)
                            {
                                // Unsupported fairplay version
                                var errorMsg = $"Unsupported fairplay version: {body[4]}";
                                Console.WriteLine(errorMsg);
                                setupStep1Activity?.SetStatus(ActivityStatusCode.Error, errorMsg);
                                return;
                            }
                            
                            // Get mode and send correct reply message
                            mode = body[14];
                            var output = _replyMessage[mode];
                            
                            setupStep1Activity?.SetTag("response.length", output.Length);
                            
                            response.Headers.Add("Content-Type", "application/octet-stream");
                            await response.WriteAsync(output, 0, output.Length);
                            
                            setupStep1Activity?.SetStatus(ActivityStatusCode.Ok);
                        }
                        else if (body.Length == 164)
                        {
                            using var setupStep2Activity = AirPlayTelemetry.CreateSessionActivity(
                                "FairPlaySetup.Step2", 
                                ActivityKind.Internal, 
                                sessionId: sessionId);
                                
                            // Response 32 bytes
                            if (body[4] != 0x03)
                            {
                                // Unsupported fairplay version
                                var errorMsg = $"Unsupported fairplay version: {body[4]}";
                                Console.WriteLine(errorMsg);
                                setupStep2Activity?.SetStatus(ActivityStatusCode.Error, errorMsg);
                                return;
                            }
                            
                            var keyMsg = new byte[164];
                            Array.Copy(body, 0, keyMsg, 0, 164);
                            session.KeyMsg = keyMsg;
                            
                            var data = body.Skip(144).ToArray();
                            var output = new byte[32];
                            Array.Copy(_fpHeader, 0, output, 0, 12);
                            Array.Copy(data, 0, output, 12, 20);
                            
                            setupStep2Activity?.SetTag("key_msg.length", keyMsg.Length);
                            setupStep2Activity?.SetTag("response.length", output.Length);
                            
                            response.Headers.Add("Content-Type", "application/octet-stream");
                            await response.WriteAsync(output, 0, output.Length);
                            
                            setupStep2Activity?.SetStatus(ActivityStatusCode.Ok);
                        }
                        else
                        {
                            // Unsupported fairplay version
                            var errorMsg = "Unsupported fairplay version";
                            Console.WriteLine(errorMsg);
                            fpSetupActivity?.SetStatus(ActivityStatusCode.Error, errorMsg);
                            return;
                        }
                        
                        fpSetupActivity?.SetStatus(ActivityStatusCode.Ok);
                    }
                }

                if (request.Type == RequestType.SETUP)
                {
                    using var setupActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessSetupRequest", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    // If session is not ready, something gone wrong.
                    if (!session.FairPlaySetupCompleted)
                    {
                        setupActivity?.SetStatus(ActivityStatusCode.Error, "FairPlay not ready");
                        Console.WriteLine("FairPlay not ready. Something gone wrong.");
                        response.StatusCode = StatusCode.BADREQUEST;
                    }
                    else
                    {
                        using (var mem = new MemoryStream(request.Body))
                        {
                            var nsDict = PropertyListParser.Parse(mem) as NSDictionary;
                            var plistDict = nsDict.ToDictionary();

                            setupActivity?.SetTag("plist.keys", string.Join(",", plistDict.Keys));
                            
                            if (plistDict.ContainsKey("streams"))
                            {
                                // Always one foreach request
                                var stream = (Dictionary<string, object>)((object[])plistDict["streams"].ToObject())[0];
                                short type = Convert.ToInt16((int)stream["type"]);
                                
                                setupActivity?.SetTag("stream.type", type);
                                
                                // If screen Mirroring
                                if (type == 110)
                                {
                                    using var mirrorSetupActivity = AirPlayTelemetry.CreateSessionActivity(
                                        "SetupMirroringStream", 
                                        ActivityKind.Internal, 
                                        sessionId: sessionId);
                                        
                                    session.StreamConnectionId = unchecked((ulong)(System.Int64)stream["streamConnectionID"]).ToString();
                                    mirrorSetupActivity?.SetTag("stream.connection_id", session.StreamConnectionId);
                                    
                                    // Set video data port
                                    var streams = new Dictionary<string, List<Dictionary<string, object>>>()
                                    {
                                        {
                                            "streams",
                                            new List<Dictionary<string, object>>
                                            {
                                                {
                                                    new Dictionary<string, object>
                                                    {
                                                        { "type", 110 },
                                                        { "dataPort", _airPlayPort }
                                                    }
                                                }
                                            }
                                        }
                                    };

                                    var binaryPlist = NSObject.Wrap(streams);
                                    var plistBytes = BinaryPropertyListWriter.WriteToArray(binaryPlist);
                                    
                                    mirrorSetupActivity?.SetTag("response.data_port", _airPlayPort);
                                    mirrorSetupActivity?.SetTag("response.size", plistBytes.Length);
                                    
                                    response.Headers.Add("Content-Type", "application/x-apple-binary-plist");
                                    await response.WriteAsync(plistBytes, 0, plistBytes.Length).ConfigureAwait(false);
                                    
                                    mirrorSetupActivity?.SetStatus(ActivityStatusCode.Ok);
                                }
                                // If audio session
                                if (type == 96)
                                {
                                    using var audioSetupActivity = AirPlayTelemetry.CreateSessionActivity(
                                        "SetupAudioStream", 
                                        ActivityKind.Internal, 
                                        sessionId: sessionId);
                                        
                                    if (stream.ContainsKey("audioFormat"))
                                    {
                                        var audioFormat = (int)stream["audioFormat"];
                                        session.AudioFormat = (AudioFormat)audioFormat;
                                        var description = GetAudioFormatDescription(audioFormat);
                                        
                                        audioSetupActivity?.SetTag("audio.format", audioFormat);
                                        audioSetupActivity?.SetTag("audio.description", description);
                                        
                                        Console.WriteLine($"Audio type: {description}");
                                    }
                                    
                                    if (stream.ContainsKey("controlPort"))
                                    {
                                        // Use this port to request resend lost packet? (remote port)
                                        var controlPort = Convert.ToUInt16((int)stream["controlPort"]);
                                        audioSetupActivity?.SetTag("audio.control_port", controlPort);
                                    }
                                    
                                    // Set audio data port
                                    var streams = new Dictionary<string, List<Dictionary<string, object>>>()
                                    {
                                        {
                                            "streams",
                                            new List<Dictionary<string, object>>
                                            {
                                                {
                                                    new Dictionary<string, object>
                                                    {
                                                        { "type", 96 },
                                                        { "controlPort", 7002 },
                                                        { "dataPort", 7003 }
                                                    }
                                                }
                                            }
                                        }
                                    };

                                    // The NSObject.Wrap method cant handle these nested dicts and lists very well, so lets wrap and nest them ourselves.
                                    var streamDict = new Dictionary<string, object>
                                    {
                                        { "type", 96 },
                                        { "controlPort", 7002 },
                                        { "dataPort", 7003 }
                                    };
                                    
                                    audioSetupActivity?.SetTag("response.control_port", 7002);
                                    audioSetupActivity?.SetTag("response.data_port", 7003);
                                    
                                    var streamDictNsObj = NSObject.Wrap(streamDict);
                                    var streamsList = new NSArray(streamDictNsObj);
                                    var streamsDict = new NSDictionary(1)
                                    {
                                        { "streams", streamsList }
                                    };
                                    var plistBytes = BinaryPropertyListWriter.WriteToArray(streamsDict);
                                    
                                    audioSetupActivity?.SetTag("response.size", plistBytes.Length);
                                    
                                    response.Headers.Add("Content-Type", "application/x-apple-binary-plist");
                                    await response.WriteAsync(plistBytes, 0, plistBytes.Length).ConfigureAwait(false);
                                    
                                    audioSetupActivity?.SetStatus(ActivityStatusCode.Ok);
                                }
                            }
                            else
                            {
                                using var setupParamsActivity = AirPlayTelemetry.CreateSessionActivity(
                                    "SetupSessionParameters", 
                                    ActivityKind.Internal, 
                                    sessionId: sessionId);
                                    
                                // Read ekey and eiv used to decode video and audio data
                                if (plistDict.ContainsKey("et"))
                                {
                                    // plist-cil only handles int and long from NSNumber, converting to short from int should be OK.
                                    var et = Convert.ToInt16((int)plistDict["et"].ToObject());
                                    setupParamsActivity?.SetTag("encryption.type", et);
                                    Console.WriteLine($"ET: {et}");
                                }
                                
                                if (plistDict.ContainsKey("ekey"))
                                {
                                    session.AesKey = (byte[])plistDict["ekey"].ToObject();
                                    setupParamsActivity?.SetTag("encryption.key_length", session.AesKey.Length);
                                }
                                
                                if (plistDict.ContainsKey("eiv"))
                                {
                                    session.AesIv = (byte[])plistDict["eiv"].ToObject();
                                    setupParamsActivity?.SetTag("encryption.iv_length", session.AesIv.Length);
                                }
                                
                                if (plistDict.ContainsKey("isScreenMirroringSession"))
                                {
                                    session.MirroringSession = (bool)plistDict["isScreenMirroringSession"].ToObject();
                                    setupParamsActivity?.SetTag("session.is_mirroring", session.MirroringSession);
                                }
                                
                                if (plistDict.ContainsKey("timingPort"))
                                {
                                    // Use this port to send heartbeat (remote port)
                                    var timingPort = Convert.ToUInt16((int)plistDict["timingPort"].ToObject());
                                    setupParamsActivity?.SetTag("timing.port", timingPort);
                                }
                                
                                var dict = new Dictionary<string, object>()
                                {
                                    // Original code used ushort (appropriate for ports) but this isnt actually
                                    // differentiable from int in the plist format.
                                    { "timingPort", (int)_airTunesPort },
                                    { "eventPort", (int)_airTunesPort }
                                };

                                var binaryPlist = NSObject.Wrap(dict);
                                var output = BinaryPropertyListWriter.WriteToArray(binaryPlist);
                                
                                setupParamsActivity?.SetTag("response.timing_port", _airTunesPort);
                                setupParamsActivity?.SetTag("response.event_port", _airTunesPort);
                                setupParamsActivity?.SetTag("response.size", output.Length);
                                
                                response.Headers.Add("Content-Type", "application/x-apple-binary-plist");
                                await response.WriteAsync(output, 0, output.Length).ConfigureAwait(false);
                                
                                setupParamsActivity?.SetStatus(ActivityStatusCode.Ok);
                            }

                            // Initialize listeners based on session state
                            if (session.FairPlayReady && session.MirroringSessionReady && session.MirroringListener == null)
                            {
                                using var startMirroringActivity = AirPlayTelemetry.CreateSessionActivity(
                                    "StartMirroringListener", 
                                    ActivityKind.Producer, 
                                    sessionId: sessionId);
                                    
                                // Start 'MirroringListener' (handle H264 data received from iOS/macOS)
                                try
                                {
                                    var mirroring = new MirroringListener(_receiver, session.SessionId, _airPlayPort);
                                    await mirroring.StartAsync(cancellationToken).ConfigureAwait(false);
                                    session.MirroringListener = mirroring;
                                    
                                    startMirroringActivity?.SetTag("mirroring.port", _airPlayPort);
                                    startMirroringActivity?.SetStatus(ActivityStatusCode.Ok);
                                }
                                catch (Exception ex)
                                {
                                    startMirroringActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                                    startMirroringActivity?.AddException(ex);
                                    throw;
                                }
                            }

                            if (session.FairPlayReady && (!session.MirroringSession.HasValue || !session.MirroringSession.Value))
                            {
                                using var startStreamingActivity = AirPlayTelemetry.CreateSessionActivity(
                                    "StartStreamingListener", 
                                    ActivityKind.Producer, 
                                    sessionId: sessionId);
                                    
                                // Start 'StreamingListener' (handle streaming url)
                                try
                                {
                                    var streaming = new StreamingListener(_receiver, session.SessionId, _expandedPrivateKey, _airPlayPort);
                                    await streaming.StartAsync(cancellationToken).ConfigureAwait(false);
                                    session.StreamingListener = streaming;
                                    
                                    startStreamingActivity?.SetTag("streaming.port", _airPlayPort);
                                    startStreamingActivity?.SetStatus(ActivityStatusCode.Ok);
                                }
                                catch (Exception ex)
                                {
                                    startStreamingActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                                    startStreamingActivity?.AddException(ex);
                                    throw;
                                }
                            }

                            if (session.FairPlayReady && session.AudioSessionReady && session.AudioControlListener == null)
                            {
                                using var startAudioActivity = AirPlayTelemetry.CreateSessionActivity(
                                    "StartAudioListener", 
                                    ActivityKind.Producer, 
                                    sessionId: sessionId);
                                    
                                // Start 'AudioListener' (handle PCM/AAC/ALAC data received from iOS/macOS
                                try
                                {
                                    var control = new AudioListener(_receiver, session.SessionId, 7002, 7003, _codecConfig, _dumpConfig);
                                    await control.StartAsync(cancellationToken).ConfigureAwait(false);
                                    session.AudioControlListener = control;
                                    
                                    startAudioActivity?.SetTag("audio.control_port", 7002);
                                    startAudioActivity?.SetTag("audio.data_port", 7003);
                                    startAudioActivity?.SetStatus(ActivityStatusCode.Ok);
                                }
                                catch (Exception ex)
                                {
                                    startAudioActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                                    startAudioActivity?.AddException(ex);
                                    throw;
                                }
                            }
                        }
                        
                        setupActivity?.SetStatus(ActivityStatusCode.Ok);
                    }
                }

                if (request.Type == RequestType.GET_PARAMETER)
                {
                    using var getParamActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessGetParameter", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    var data = Encoding.ASCII.GetString(request.Body);
                    getParamActivity?.SetTag("parameter.name", data.Trim());
                    
                    if (data.Equals("volume\r\n"))
                    {
                        var output = Encoding.ASCII.GetBytes("volume: 1.000000\r\n");
                        
                        getParamActivity?.SetTag("volume.value", "1.000000");
                        
                        response.Headers.Add("Content-Type", "text/parameters");
                        await response.WriteAsync(output, 0, output.Length).ConfigureAwait(false);
                    }
                    
                    getParamActivity?.SetStatus(ActivityStatusCode.Ok);
                }

                if (request.Type == RequestType.RECORD)
                {
                    using var recordActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessRecordRequest", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    response.Headers.Add("Audio-Latency", "0"); // 11025
                    // response.Headers.Add("Audio-Jack-Status", "connected; type=analog");
                    
                    recordActivity?.SetTag("audio.latency", 0);
                    recordActivity?.SetStatus(ActivityStatusCode.Ok);
                }

                if (request.Type == RequestType.SET_PARAMETER)
                {
                    using var setParamActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessSetParameter", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    if (request.Headers.ContainsKey("Content-Type"))
                    {
                        var contentType = request.Headers["Content-Type"];
                        setParamActivity?.SetTag("content.type", contentType);
                        
                        if (contentType.Equals("text/parameters", StringComparison.OrdinalIgnoreCase))
                        {
                            var body = Encoding.ASCII.GetString(request.Body);
                            setParamActivity?.SetTag("parameter.body", body);
                            
                            var keyPair = body.Split(":", StringSplitOptions.RemoveEmptyEntries).Select(b => b.Trim(' ', '\r', '\n')).ToArray();
                            if (keyPair.Length == 2)
                            {
                                var key = keyPair[0];
                                var val = keyPair[1];
                                
                                setParamActivity?.SetTag("parameter.key", key);
                                setParamActivity?.SetTag("parameter.value", val);
                                
                                if (key.Equals("volume", StringComparison.OrdinalIgnoreCase))
                                {
                                    // request.Body contains 'volume: N.NNNNNN'
                                    var volume = decimal.Parse(val);
                                    _receiver.OnSetVolume(volume);
                                    
                                    setParamActivity?.SetTag("volume.value", volume);
                                }
                                else if (key.Equals("progress", StringComparison.OrdinalIgnoreCase))
                                {
                                    var pVals = val.Split("/", StringSplitOptions.RemoveEmptyEntries);
                                    var start = long.Parse(pVals[0]);
                                    var current = long.Parse(pVals[1]);
                                    var end = long.Parse(pVals[2]);
                                    
                                    setParamActivity?.SetTag("progress.start", start);
                                    setParamActivity?.SetTag("progress.current", current);
                                    setParamActivity?.SetTag("progress.end", end);
                                    
                                    // DO SOMETHING W/ PROGRESS
                                }
                            }
                        }
                        else if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
                        {
                            var image = request.Body;
                            setParamActivity?.SetTag("image.size", image.Length);
                            // DO SOMETHING W/ IMAGE
                        }
                        else if (contentType.Equals("application/x-dmap-tagged", StringComparison.OrdinalIgnoreCase))
                        {
                            var dmap = new DMapTagged();
                            var output = dmap.Decode(request.Body);
                            
                            setParamActivity?.SetTag("dmap.decoded", output != null);
                        }
                    }
                    
                    setParamActivity?.SetStatus(ActivityStatusCode.Ok);
                }

                if (request.Type == RequestType.OPTIONS)
                {
                    using var optionsActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessOptionsRequest", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    response.Headers.Add("Public", "SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, ANNOUNCE");
                    
                    optionsActivity?.SetTag("public.methods", "SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER, ANNOUNCE");
                    optionsActivity?.SetStatus(ActivityStatusCode.Ok);
                }

                if (request.Type == RequestType.ANNOUNCE)
                {
                    using var announceActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessAnnounceRequest", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    // Nothing to do here yet
                    
                    announceActivity?.SetStatus(ActivityStatusCode.Ok);
                }

                if (request.Type == RequestType.FLUSH)
                {
                    using var flushActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessFlushRequest", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    int next_seq = -1;
                    if (request.Headers.ContainsKey("RTP-Info"))
                    {
                        var rtpinfo = request.Headers["RTP-Info"];
                        flushActivity?.SetTag("rtp_info", rtpinfo);
                        
                        if (!string.IsNullOrWhiteSpace(rtpinfo))
                        {
                            Console.WriteLine($"Flush with RTP-Info: {rtpinfo}");
                            var r = new Regex(@"seq\=([^;]*)");
                            var m = r.Match(rtpinfo);
                            
                            if (m.Success)
                            {
                                next_seq = int.Parse(m.Groups[1].Value);
                                flushActivity?.SetTag("next_seq", next_seq);
                            }
                        }
                    }
                    
                    try 
                    {
                        await session.AudioControlListener.FlushAsync(next_seq);
                        flushActivity?.SetStatus(ActivityStatusCode.Ok);
                    }
                    catch (Exception ex)
                    {
                        flushActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        flushActivity?.AddException(ex);
                        throw;
                    }
                }

                if (request.Type == RequestType.TEARDOWN)
                {
                    using var teardownActivity = AirPlayTelemetry.CreateSessionActivity(
                        "AirTunesListener.ProcessTeardownRequest", 
                        ActivityKind.Internal, 
                        sessionId: sessionId);
                        
                    using (var mem = new MemoryStream(request.Body))
                    {
                        var plist = BinaryPropertyListParser.Parse(mem) as NSDictionary;
                        if (plist.ContainsKey("streams"))
                        {
                            // Always one foreach request
                            var stream = (Dictionary<string, object>)((object[])plist["streams"].ToObject()).Last();
                            var type = Convert.ToInt16((int)stream["type"]);
                            
                            teardownActivity?.SetTag("stream.type", type);
                            
                            // If screen Mirroring
                            if (type == 110)
                            {
                                // Stop mirroring session
                                try
                                {
                                    await session.MirroringListener.StopAsync();
                                    teardownActivity?.SetTag("mirroring.stopped", true);
                                }
                                catch (Exception ex)
                                {
                                    teardownActivity?.SetTag("mirroring.stopped", false);
                                    teardownActivity?.AddException(ex);
                                }
                            }
                            // If audio session
                            if (type == 96)
                            {
                                // Stop audio session
                                try
                                {
                                    await session.AudioControlListener.StopAsync();
                                    teardownActivity?.SetTag("audio.stopped", true);
                                }
                                catch (Exception ex)
                                {
                                    teardownActivity?.SetTag("audio.stopped", false);
                                    teardownActivity?.AddException(ex);
                                }
                            }
                        }
                    }
                    
                    teardownActivity?.SetStatus(ActivityStatusCode.Ok);
                }
                
                // Request w/ path '/feedback' must return 200 OK w/out response body
                // So we can do nothing here..
                
                // Save current session
                await SessionManager.Current.CreateOrUpdateSessionAsync(sessionId, session);
                
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        private string GetAudioFormatDescription(int format)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "AirTunesListener.GetAudioFormatDescription", 
                ActivityKind.Internal);
                
            activity?.SetTag("audio.format", format);
            
            string formatDescription;
            switch (format)
            {
                case 0x40000:
                    formatDescription = "96 AppleLossless, 96 352 0 16 40 10 14 2 255 0 0 44100";
                    activity?.SetTag("audio.codec", "ALAC");
                    break;
                case 0x400000:
                    formatDescription = "96 mpeg4-generic/44100/2, 96 mode=AAC-main; constantDuration=1024";
                    activity?.SetTag("audio.codec", "AAC-main");
                    break;
                case 0x1000000:
                    formatDescription = "96 mpeg4-generic/44100/2, 96 mode=AAC-eld; constantDuration=480";
                    activity?.SetTag("audio.codec", "AAC-eld");
                    break;
                default:
                    formatDescription = "Unknown: " + format;
                    activity?.SetTag("audio.codec", "Unknown");
                    break;
            }
            
            activity?.SetStatus(ActivityStatusCode.Ok);
            return formatDescription;
        }
    }
}

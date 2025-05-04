using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Listeners;
using AirPlay.Models;
using AirPlay.Models.Configs;
using AirPlay.Telemetry;
using Makaretu.Dns;
using Microsoft.Extensions.Options;

namespace AirPlay
{
    public class AirPlayReceiver : IRtspReceiver, IAirPlayReceiver
    {
        public event EventHandler<decimal> OnSetVolumeReceived;
        public event EventHandler<H264Data> OnH264DataReceived;
        public event EventHandler<PcmData> OnPCMDataReceived;

        public const string AirPlayType = "_airplay._tcp";
        public const string AirTunesType = "_raop._tcp";

        private MulticastService _mdns = null;
        private AirTunesListener _airTunesListener = null;
        private readonly string _instance;
        private readonly ushort _airTunesPort;
        private readonly ushort _airPlayPort;
        private readonly string _deviceId;

        public AirPlayReceiver(IOptions<AirPlayReceiverConfig> aprConfig, IOptions<CodecLibrariesConfig> codecConfig, IOptions<DumpConfig> dumpConfig)
        {
            using var activity = AirPlayTelemetry.CreateActivity("AirPlayReceiver.Constructor", ActivityKind.Internal);
            
            try
            {
                _airTunesPort = aprConfig?.Value?.AirTunesPort ?? 5000;
                _airPlayPort = aprConfig?.Value?.AirPlayPort ?? 7000;
                _deviceId = aprConfig?.Value?.DeviceMacAddress ?? "11:22:33:44:55:66";
                _instance = aprConfig?.Value?.Instance ?? throw new ArgumentNullException("apr.instance");
                var clConfig = codecConfig?.Value ?? throw new ArgumentNullException(nameof(codecConfig));
                var dConfig = dumpConfig?.Value ?? throw new ArgumentNullException(nameof(dumpConfig));
                
                // Add configuration details as tags for tracing
                activity?.SetTag("airplay.config.instance", _instance);
                activity?.SetTag("airplay.config.deviceId", _deviceId);
                activity?.SetTag("airplay.config.tunesPort", _airTunesPort);
                activity?.SetTag("airplay.config.playPort", _airPlayPort);
                
                _airTunesListener = new AirTunesListener(this, _airTunesPort, _airPlayPort, clConfig, dConfig);
                
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        public async Task StartListeners(CancellationToken cancellationToken)
        {
            using var activity = AirPlayTelemetry.CreateActivity("AirPlayReceiver.StartListeners", ActivityKind.Producer);
            
            activity?.SetTag("airplay.port", _airPlayPort);
            activity?.SetTag("airtunes.port", _airTunesPort);
            
            try
            {
                var sw = Stopwatch.StartNew();
                await _airTunesListener.StartAsync(cancellationToken).ConfigureAwait(false);
                sw.Stop();
                
                activity?.SetTag("startup.duration_ms", sw.ElapsedMilliseconds);
                activity?.AddEvent(new ActivityEvent("ListenersStarted"));
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        public Task StartMdnsAsync()
        {
            using var activity = AirPlayTelemetry.CreateActivity("AirPlayReceiver.StartMdns", ActivityKind.Producer);
            
            try
            {
                if (string.IsNullOrWhiteSpace(_deviceId))
                {
                    var ex = new ArgumentNullException(nameof(_deviceId));
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.AddException(ex);
                    throw ex;
                }
                
                var rDeviceId = new Regex("^(([0-9a-fA-F][0-9a-fA-F]):){5}([0-9a-fA-F][0-9a-fA-F])$");
                var mDeviceId = rDeviceId.Match(_deviceId);
                
                if (!mDeviceId.Success)
                {
                    var ex = new ArgumentException("Device id must be a mac address", nameof(_deviceId));
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.AddException(ex);
                    throw ex;
                }
                
                var deviceIdInstance = string.Join(string.Empty, mDeviceId.Groups[2].Captures) + mDeviceId.Groups[3].Value;
                activity?.SetTag("mdns.device_id_instance", deviceIdInstance);
                
                _mdns = new MulticastService();
                var sd = new ServiceDiscovery(_mdns);
                
                // Track discovered IP addresses
                using (var ipDiscoveryActivity = AirPlayTelemetry.CreateActivity("NetworkIPDiscovery", ActivityKind.Internal))
                {
                    foreach (var ip in MulticastService.GetIPAddresses())
                    {
                        Console.WriteLine($"IP address {ip}");
                        ipDiscoveryActivity?.AddEvent(new ActivityEvent(
                            "IPAddressDiscovered", 
                            tags: new ActivityTagsCollection { { "address", ip.ToString() } }));
                    }
                    ipDiscoveryActivity?.SetStatus(ActivityStatusCode.Ok);
                }
                
                // Monitor network interface discovery
                _mdns.NetworkInterfaceDiscovered += (s, e) =>
                {
                    using var interfaceActivity = AirPlayTelemetry.CreateActivity("NetworkInterfaceDiscovered", ActivityKind.Internal);
                    
                    try
                    {
                        interfaceActivity?.SetTag("network.interfaces_count", e.NetworkInterfaces.Count());
                        
                        foreach (var nic in e.NetworkInterfaces)
                        {
                            Console.WriteLine($"NIC '{nic.Name}'");
                            interfaceActivity?.AddEvent(new ActivityEvent(
                                "NICDiscovered", 
                                tags: new ActivityTagsCollection { 
                                    { "network.nic.name", nic.Name },
                                    { "network.nic.speed", nic.Speed },
                                    { "network.nic.supports_multicast", nic.SupportsMulticast }
                                }));
                        }
                        
                        interfaceActivity?.SetStatus(ActivityStatusCode.Ok);
                    }
                    catch (Exception ex)
                    {
                        interfaceActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        interfaceActivity?.AddException(ex);
                    }
                };
                
                // Add AirTunes service advertisement
                using (var airTunesActivity = AirPlayTelemetry.CreateActivity("AirTunes.ServiceAdvertisement", ActivityKind.Producer))
                {
                    try
                    {
                        // Internally 'ServiceProfile' create the SRV record
                        var airTunes = new ServiceProfile($"{deviceIdInstance}@{_instance}", AirTunesType, _airTunesPort);
                        airTunes.AddProperty("ch", "2");
                        airTunes.AddProperty("cn", "1,2"); // 0=pcm, 1=alac, 2=aac, 3=aac-eld (not supported here)
                        airTunes.AddProperty("et", "0,3,5"); // 0=none, 1=rsa (airport express), 3=fairplay, 4=MFiSAP, 5=fairplay SAPv2.5
                        airTunes.AddProperty("md", "0,1,2"); // 0=text, 1=artwork, 2=progress
                        airTunes.AddProperty("sr", "44100"); // sample rate
                        airTunes.AddProperty("ss", "16"); // bitdepth
                        airTunes.AddProperty("da", "true"); // unk
                        airTunes.AddProperty("sv", "false"); // unk
                        airTunes.AddProperty("ft", "0x5A7FDE40,0x1C"); // originally "0x5A7FFFF7,0x1E" https://openairplay.github.io/airplay-spec/features.html
                        airTunes.AddProperty("am", "AppleTV5,3");
                        airTunes.AddProperty("pk", "29fbb183a58b466e05b9ab667b3c429d18a6b785637333d3f0f3a34baa89f45e");
                        airTunes.AddProperty("sf", "0x4");
                        airTunes.AddProperty("tp", "UDP");
                        airTunes.AddProperty("vn", "65537");
                        airTunes.AddProperty("vs", "220.68");
                        airTunes.AddProperty("vv", "2");
                        
                        airTunesActivity?.SetTag("service.name", $"{deviceIdInstance}@{_instance}");
                        airTunesActivity?.SetTag("service.type", AirTunesType);
                        airTunesActivity?.SetTag("service.port", _airTunesPort);
                        
                        sd.Advertise(airTunes);
                        airTunesActivity?.AddEvent(new ActivityEvent("ServiceAdvertised"));
                        airTunesActivity?.SetStatus(ActivityStatusCode.Ok);
                    }
                    catch (Exception ex)
                    {
                        airTunesActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        airTunesActivity?.AddException(ex);
                        throw;
                    }
                }
                
                // Add AirPlay service advertisement
                using (var airPlayActivity = AirPlayTelemetry.CreateActivity("AirPlay.ServiceAdvertisement", ActivityKind.Producer))
                {
                    try
                    {
                        /*
                         * ch	2	audio channels: stereo
                         * cn	0,1,2,3	audio codecs
                         * et	0,3,5	supported encryption types
                         * md	0,1,2	supported metadata types
                         * pw	false	does the speaker require a password?
                         * sr	44100	audio sample rate: 44100 Hz
                         * ss	16	audio sample size: 16-bit
                         */
                        // Internally 'ServiceProfile' create the SRV record
                        var airPlay = new ServiceProfile(_instance, AirPlayType, _airPlayPort);
                        airPlay.AddProperty("deviceid", _deviceId);
                        airPlay.AddProperty("features", "0x5A7FDE40,0x1C"); // originally "0x5A7FFFF7,0x1E" https://openairplay.github.io/airplay-spec/features.html
                        airPlay.AddProperty("flags", "0x4");
                        airPlay.AddProperty("model", "AppleTV5,3");
                        airPlay.AddProperty("pk", "29fbb183a58b466e05b9ab667b3c429d18a6b785637333d3f0f3a34baa89f45e");
                        airPlay.AddProperty("pi", "aa072a95-0318-4ec3-b042-4992495877d3");
                        airPlay.AddProperty("srcvers", "220.68");
                        airPlay.AddProperty("vv", "2");
                        
                        airPlayActivity?.SetTag("service.name", _instance);
                        airPlayActivity?.SetTag("service.type", AirPlayType);
                        airPlayActivity?.SetTag("service.port", _airPlayPort);
                        
                        sd.Advertise(airPlay);
                        airPlayActivity?.AddEvent(new ActivityEvent("ServiceAdvertised"));
                        airPlayActivity?.SetStatus(ActivityStatusCode.Ok);
                    }
                    catch (Exception ex)
                    {
                        airPlayActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                        airPlayActivity?.AddException(ex);
                        throw;
                    }
                }
                
                _mdns.Start();
                activity?.AddEvent(new ActivityEvent("MDNSServiceStarted"));
                activity?.SetStatus(ActivityStatusCode.Ok);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        public void OnSetVolume(decimal volume)
        {
            using var activity = AirPlayTelemetry.CreateActivity("AirPlayReceiver.SetVolume", ActivityKind.Consumer);
            
            activity?.SetTag("media.volume", volume);
            activity?.AddEvent(new ActivityEvent("VolumeChangeReceived"));
            
            try
            {
                OnSetVolumeReceived?.Invoke(this, volume);
                activity?.SetStatus(ActivityStatusCode.Ok);
                
                // Record volume metric
                AirPlayTelemetry.SetCurrentVolume((double)volume);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        public void OnData(H264Data data)
        {
            using var activity = AirPlayTelemetry.CreateActivity("AirPlayReceiver.ProcessVideoData", ActivityKind.Consumer);
            
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                // Add video frame information to the activity
                activity?.SetTag("video.frame.type", data.FrameType);
                activity?.SetTag("video.frame.is_keyframe", data.FrameType == 5);
                activity?.SetTag("video.resolution", $"{data.Width}x{data.Height}");
                activity?.SetTag("video.frame.size", data.Length);
                activity?.SetTag("video.frame.timestamp", data.Pts);
                
                OnH264DataReceived?.Invoke(this, data);
                
                stopwatch.Stop();
                activity?.SetTag("processing.duration_ms", stopwatch.ElapsedMilliseconds);
                
                // Record metrics
                AirPlayTelemetry.VideoFramesProcessed.Add(1);
                AirPlayTelemetry.VideoFrameProcessingTime.Record(stopwatch.Elapsed.TotalMilliseconds);
                AirPlayTelemetry.VideoFrameSize.Record(data.Length);
                
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                activity?.SetTag("processing.duration_ms", stopwatch.ElapsedMilliseconds);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }

        public void OnPCMData(PcmData data)
        {
            using var activity = AirPlayTelemetry.CreateActivity("AirPlayReceiver.ProcessAudioData", ActivityKind.Consumer);
            
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                // Add audio data information to the activity
                activity?.SetTag("audio.data.length", data.Length);
                activity?.SetTag("audio.data.timestamp", data.Pts);
                activity?.SetTag("audio.data.size", data.Data?.Length ?? 0);
                
                OnPCMDataReceived?.Invoke(this, data);
                
                stopwatch.Stop();
                activity?.SetTag("processing.duration_ms", stopwatch.ElapsedMilliseconds);
                
                // Record metrics
                AirPlayTelemetry.AudioPacketsProcessed.Add(1);
                AirPlayTelemetry.AudioPacketProcessingTime.Record(stopwatch.Elapsed.TotalMilliseconds);
                AirPlayTelemetry.AudioPacketSize.Record(data.Data?.Length ?? 0);
                
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                activity?.SetTag("processing.duration_ms", stopwatch.ElapsedMilliseconds);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
        }
    }
}
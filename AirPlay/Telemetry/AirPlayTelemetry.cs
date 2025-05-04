using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace AirPlay.Telemetry
{
    public static class AirPlayTelemetry
    {
        /// <summary>
        /// The main ActivitySource for AirPlay library.
        /// </summary>
        public static readonly ActivitySource ActivitySource = new ActivitySource("AirPlay", GetVersion());

        /// <summary>
        /// Meter for monitoring audio and video metrics.
        /// </summary>
        public static readonly Meter AirPlayMeter = new Meter("AirPlay.Media", GetVersion());
        
        /// <summary>
        /// Meter specifically for session and connection metrics.
        /// </summary>
        public static readonly Meter ConnectionMeter = new Meter("AirPlay.Connections", GetVersion());

        /// <summary>
        /// Counter for tracking the number of audio packets processed.
        /// </summary>
        public static readonly Counter<long> AudioPacketsProcessed = AirPlayMeter.CreateCounter<long>("audio.packets.processed", "Packets", "Number of audio packets processed");

        /// <summary>
        /// Counter for tracking the number of video frames processed.
        /// </summary>
        public static readonly Counter<long> VideoFramesProcessed = AirPlayMeter.CreateCounter<long>("video.frames.processed", "Frames", "Number of video frames processed");

        /// <summary>
        /// Histogram for tracking audio packet processing time.
        /// </summary>
        public static readonly Histogram<double> AudioPacketProcessingTime = AirPlayMeter.CreateHistogram<double>("audio.processing.time", "ms", "Time taken to process audio packets");

        /// <summary>
        /// Histogram for tracking video frame processing time.
        /// </summary>
        public static readonly Histogram<double> VideoFrameProcessingTime = AirPlayMeter.CreateHistogram<double>("video.processing.time", "ms", "Time taken to process video frames");
        
        /// <summary>
        /// Histogram for tracking audio packet sizes.
        /// </summary>
        public static readonly Histogram<long> AudioPacketSize = AirPlayMeter.CreateHistogram<long>("audio.packet.size", "bytes", "Size of audio packets in bytes");
        
        /// <summary>
        /// Histogram for tracking video frame sizes.
        /// </summary>
        public static readonly Histogram<long> VideoFrameSize = AirPlayMeter.CreateHistogram<long>("video.frame.size", "bytes", "Size of video frames in bytes");
        
        /// <summary>
        /// Gauge for tracking the current volume level.
        /// </summary>
        public static readonly ObservableGauge<double> CurrentVolume = AirPlayMeter.CreateObservableGauge(
            "media.volume", 
            () => new Measurement<double>[] { new(_lastVolumeValue, new KeyValuePair<string, object>[] { new("media.type", "audio") }) },
            "level",
            "Current volume level (0.0-1.0)");
        
        private static double _lastVolumeValue = 1.0;
        
        /// <summary>
        /// Gauge for tracking the current number of active sessions.
        /// </summary>
        public static readonly ObservableGauge<int> ActiveSessions = ConnectionMeter.CreateObservableGauge(
            "sessions.active",
            () => new Measurement<int>[] { new(_activeSessionCount) },
            "sessions", 
            "Number of active AirPlay sessions");
        
        private static int _activeSessionCount = 0;
        
        /// <summary>
        /// Counter for tracking connection attempts.
        /// </summary>
        public static readonly Counter<long> ConnectionAttempts = ConnectionMeter.CreateCounter<long>("connection.attempts", "connections", "Number of connection attempts");
        
        /// <summary>
        /// Counter for tracking successful connections.
        /// </summary>
        public static readonly Counter<long> ConnectionSuccesses = ConnectionMeter.CreateCounter<long>("connection.successes", "connections", "Number of successful connections");
        
        /// <summary>
        /// Counter for tracking failed connections.
        /// </summary>
        public static readonly Counter<long> ConnectionFailures = ConnectionMeter.CreateCounter<long>("connection.failures", "connections", "Number of failed connections");

        /// <summary>
        /// Creates a new Activity for general operations.
        /// </summary>
        /// <param name="name">Name of the activity</param>
        /// <param name="kind">Kind of activity (default: Internal)</param>
        /// <param name="parentId">Optional parent activity ID</param>
        /// <param name="tags">Optional additional tags</param>
        /// <returns>A started Activity or null if disabled</returns>
        public static Activity CreateActivity(string name, ActivityKind kind = ActivityKind.Internal, 
            string parentId = null, 
            IEnumerable<KeyValuePair<string, object>> tags = null,
            [CallerMemberName] string callerName = "", [CallerFilePath] string callerFile = "")
        {
            var activity = ActivitySource.StartActivity(name, kind, parentId);
            
            if (activity != null)
            {
                activity.SetTag("code.function", callerName);
                activity.SetTag("code.filepath", callerFile);

                // Add any additional tags
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        activity.SetTag(tag.Key, tag.Value);
                    }
                }
            }

            return activity;
        }
        
        /// <summary>
        /// Creates a new Activity for session-related operations.
        /// </summary>
        /// <param name="name">Name of the activity</param>
        /// <param name="kind">Kind of activity (default: Internal)</param>
        /// <param name="sessionId">Optional session ID to include as a tag</param>
        /// <param name="parentId">Optional parent activity ID</param>
        /// <param name="tags">Optional additional tags</param>
        /// <returns>A started Activity or null if disabled</returns>
        public static Activity CreateSessionActivity(string name, ActivityKind kind = ActivityKind.Internal, 
            string sessionId = null, string parentId = null, 
            IEnumerable<KeyValuePair<string, object>> tags = null,
            [CallerMemberName] string callerName = "", [CallerFilePath] string callerFile = "")
        {
            var activity = ActivitySource.StartActivity(name, kind, parentId);
            
            if (activity != null)
            {
                // Add common tags
                if (!string.IsNullOrEmpty(sessionId))
                {
                    activity.SetTag("airplay.session.id", sessionId);
                }

                activity.SetTag("code.function", callerName);
                activity.SetTag("code.filepath", callerFile);

                // Add any additional tags
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        activity.SetTag(tag.Key, tag.Value);
                    }
                }
            }

            return activity;
        }
        
        /// <summary>
        /// Updates the current volume value for metrics.
        /// </summary>
        public static void SetCurrentVolume(double volume)
        {
            _lastVolumeValue = Math.Clamp(volume, 0, 1);
        }
        
        /// <summary>
        /// Updates the number of active sessions for metrics.
        /// </summary>
        public static void SetSessionCount(int count)
        {
            _activeSessionCount = count;
        }

        private static string GetVersion()
        {
            return typeof(AirPlayTelemetry).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        }
    }
}
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using AirPlay.Models;
using AirPlay.Models.Enums;
using AirPlay.Telemetry;

namespace AirPlay.Services.Implementations
{
    public class SessionManager
    {
        private static SessionManager _current = null;
        private ConcurrentDictionary<string, Session> _sessions;

        public static SessionManager Current => _current ?? (_current = new SessionManager());

        private SessionManager()
        {
            using var activity = AirPlayTelemetry.CreateActivity("SessionManager.Initialize", ActivityKind.Internal);
            
            _sessions = new ConcurrentDictionary<string, Session>();
            
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        public Task<Session> GetSessionAsync(string key)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "SessionManager.GetSession", 
                ActivityKind.Internal,
                sessionId: key);
            
            _sessions.TryGetValue(key, out Session _session);
            bool isNewSession = _session == null;
            
            activity?.SetTag("session.exists", !isNewSession);
            
            var session = _session ?? new Session(key);
            
            if (isNewSession)
            {
                activity?.AddEvent(new ActivityEvent("NewSessionCreated"));
            }
            
            activity?.SetStatus(ActivityStatusCode.Ok);
            return Task.FromResult(session);
        }

        public Task CreateOrUpdateSessionAsync(string key, Session session)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "SessionManager.CreateOrUpdateSession", 
                ActivityKind.Internal,
                sessionId: key);
                
            bool exists = _sessions.ContainsKey(key);
            activity?.SetTag("session.operation", exists ? "update" : "create");
            
            try
            {
                _sessions.AddOrUpdate(key, session, (k, old) =>
                {
                    var s = new Session(k)
                    {
                        EcdhOurs = session.EcdhOurs ?? old.EcdhOurs,
                        EcdhTheirs = session.EcdhTheirs ?? old.EcdhTheirs,
                        EdTheirs = session.EdTheirs ?? old.EdTheirs,
                        EcdhShared = session.EcdhShared ?? old.EcdhShared,
                        PairVerified = session.PairVerified ?? old.PairVerified,
                        AesKey = session.AesKey ?? old.AesKey,
                        AesIv = session.AesIv ?? old.AesIv,
                        StreamConnectionId = session.StreamConnectionId ?? old.StreamConnectionId,
                        KeyMsg = session.KeyMsg ?? old.KeyMsg,
                        DecryptedAesKey = session.DecryptedAesKey ?? old.DecryptedAesKey,
                        MirroringListener = session.MirroringListener ?? old.MirroringListener,
                        AudioControlListener = session.AudioControlListener ?? old.AudioControlListener,
                        StreamingListener = session.StreamingListener ?? old.StreamingListener,
                        SpsPps = session.SpsPps ?? old.SpsPps,
                        Pts = session.Pts ?? old.Pts,
                        WidthSource = session.WidthSource ?? old.WidthSource,
                        HeightSource = session.HeightSource ?? old.HeightSource,
                        MirroringSession = session.MirroringSession ?? old.MirroringSession,
                        AudioFormat = session.AudioFormat == AudioFormat.Unknown ? old.AudioFormat : session.AudioFormat
                    };
                    
                    using var updateActivity = AirPlayTelemetry.CreateSessionActivity(
                        "SessionManager.SessionUpdated", 
                        ActivityKind.Internal,
                        sessionId: k);
                        
                    updateActivity?.SetTag("session.pair_verified", s.PairVerified ?? false);
                    updateActivity?.SetTag("session.fairplay_ready", s.FairPlayReady);
                    updateActivity?.SetTag("session.mirroring_ready", s.MirroringSessionReady);
                    updateActivity?.SetTag("session.audio_ready", s.AudioSessionReady);
                    updateActivity?.SetTag("session.has_key_msg", s.KeyMsg != null);
                    updateActivity?.SetTag("session.has_audio_format", s.AudioFormat != AudioFormat.Unknown);
                    
                    if (s.AudioFormat != AudioFormat.Unknown)
                    {
                        updateActivity?.SetTag("session.audio_format", s.AudioFormat.ToString());
                    }
                    
                    if (s.MirroringSession.HasValue)
                    {
                        updateActivity?.SetTag("session.is_mirroring", s.MirroringSession.Value);
                    }
                    
                    if (s.WidthSource.HasValue && s.HeightSource.HasValue)
                    {
                        updateActivity?.SetTag("session.resolution", $"{s.WidthSource}x{s.HeightSource}");
                    }
                    
                    updateActivity?.SetStatus(ActivityStatusCode.Ok);
                    return s;
                });
                
                activity?.SetStatus(ActivityStatusCode.Ok);
                
                // Record session metrics
                AirPlayTelemetry.SetSessionCount(count: _sessions.Count);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
            
            return Task.CompletedTask;
        }
        
        public Task RemoveSessionAsync(string key)
        {
            using var activity = AirPlayTelemetry.CreateSessionActivity(
                "SessionManager.RemoveSession", 
                ActivityKind.Internal,
                sessionId: key);
                
            try
            {
                bool removed = _sessions.TryRemove(key, out _);
                activity?.SetTag("session.removed", removed);
                activity?.SetStatus(ActivityStatusCode.Ok);
                
                // Record session metrics
                AirPlayTelemetry.SetSessionCount(_sessions.Count);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                throw;
            }
            
            return Task.CompletedTask;
        }
        
        public Task<int> GetActiveSessionCountAsync()
        {
            using var activity = AirPlayTelemetry.CreateActivity("SessionManager.GetActiveSessionCount", ActivityKind.Internal);
            
            var count = _sessions.Count;
            activity?.SetTag("sessions.count", count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            
            return Task.FromResult(count);
        }
    }
}

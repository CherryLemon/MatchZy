using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;

namespace MatchZy
{
    public partial class MatchZy
    {
        public string demoPath = "MatchZy/";
        public string demoNameFormat = "{TIME}_{MATCH_ID}_{MAP}_{TEAM1}_vs_{TEAM2}";
        public string demoUploadURL = "";
        public string demoUploadHeaderKey = "";
        public string demoUploadHeaderValue = "";

        public string activeDemoFile = "";

        public bool isDemoRecording = false;
        public bool isDemoRecordingEnabled = true;

        public CounterStrikeSharp.API.Modules.Timers.Timer? demoStartTimer = null;

        /// <summary>
        /// Starts the demo recording at the moment the match goes live.
        /// GOTV records its broadcast, which trails the game by tv_delay, so recording
        /// immediately would put the last tv_delay seconds of warmup/knife round at the
        /// head of the demo. We wait out the delay first, mirroring what HandleMatchEnd
        /// already does when it stops the recording.
        /// </summary>
        public void StartDemoRecordingAfterTvDelay()
        {
            if (isDemoRecording)
            {
                Log("[StartDemoRecordingAfterTvDelay] Demo recording is already in progress.");
                return;
            }
            CancelPendingDemoRecording();
            if (!isDemoRecordingEnabled)
            {
                Log("[StartDemoRecordingAfterTvDelay] Demo recording is disabled.");
                return;
            }

            int tvDelay = GetTvDelay();
            if (tvDelay <= 0)
            {
                Log("[StartDemoRecordingAfterTvDelay] GOTV reports no broadcast delay, starting demo recording immediately.");
                StartDemoRecording();
                return;
            }

            long matchId = liveMatchId;
            int mapNumber = matchConfig.CurrentMapNumber;
            Log($"[StartDemoRecordingAfterTvDelay] GOTV broadcast is {tvDelay}s behind, starting demo recording in {tvDelay}s so that the demo begins at the first live round.");
            demoStartTimer = AddTimer(tvDelay, () =>
            {
                demoStartTimer = null;
                if (!isMatchLive || liveMatchId != matchId || matchConfig.CurrentMapNumber != mapNumber)
                {
                    Log($"[StartDemoRecordingAfterTvDelay] Match {matchId} (map {mapNumber}) is not live anymore, not starting demo recording.");
                    return;
                }
                StartDemoRecording();
            });
        }

        public void CancelPendingDemoRecording()
        {
            demoStartTimer?.Kill();
            demoStartTimer = null;
        }

        public void StartDemoRecording()
        {
            if (!isDemoRecordingEnabled)
            {
                Log("[StartDemoRecording] Demo recording is disabled.");
                return;
            }
            if (isDemoRecording)
            {
                Log("[StartDemoRecording] Demo recording is already in progress.");
                return;
            }
            string demoFileName = FormatCvarValue(demoNameFormat.Replace(" ", "_")) + ".dem";
            try
            {
                string? directoryPath = Path.GetDirectoryName(Path.Join(Server.GameDirectory + "/csgo/", demoPath));
                if (directoryPath != null)
                {
                    if (!Directory.Exists(directoryPath))
                    {
                        Directory.CreateDirectory(directoryPath);
                    }
                }
                string tempDemoPath = demoPath == "" ? demoFileName : demoPath + demoFileName;
                activeDemoFile = tempDemoPath;
                Log($"[StartDemoRecoding] Starting demo recording, path: {tempDemoPath}");
                Server.ExecuteCommand($"tv_record {tempDemoPath}");
                isDemoRecording = true;
            }
            catch (Exception ex)
            {
                Log($"[StartDemoRecording - FATAL] Error: {ex.Message}. Starting demo recording with path. Name: {demoFileName}");
                // This is to avoid demo loss in any case of exception
                Server.ExecuteCommand($"tv_record {demoFileName}");
                isDemoRecording = true;
            }

        }

        public void StopDemoRecording(float delay, string demoFile, long liveMatchId, int currentMapNumber)
        {
            // A map that ended within tv_delay of going live never reached its
            // pending tv_record, so there is nothing to stop here. Dropping the
            // stale activeDemoFile also stops the previous map's demo from being
            // uploaded a second time under this map number.
            CancelPendingDemoRecording();
            if (!isDemoRecording || demoFile == "")
            {
                Log("[StopDemoRecording] No demo recording is active, nothing to stop or upload.");
                activeDemoFile = "";
                return;
            }
            Log($"[StopDemoRecording] Going to stop demorecording in {delay}s");
            string demoPath = Path.Join(Server.GameDirectory + "/csgo/", demoFile);
            activeDemoFile = "";
            (int t1score, int t2score) = GetTeamsScore();
            int roundNumber = t1score + t2score;
            AddTimer(delay, () =>
            {
                if (isDemoRecording)
                {
                    Server.ExecuteCommand($"tv_stoprecord");
                }
                isDemoRecording = false;
                AddTimer(15, () =>
                {
                    Task.Run(async () =>
                    {
                        await UploadFileAsync(demoPath, demoUploadURL, demoUploadHeaderKey, demoUploadHeaderValue, liveMatchId, currentMapNumber, roundNumber);
                    });
                });
            });
        }

        public int GetTvDelay()
        {
            bool tvEnable = ConVar.Find("tv_enable")!.GetPrimitiveValue<bool>();
            if (!tvEnable) return 0;

            bool tvEnable1 = ConVar.Find("tv_enable1")!.GetPrimitiveValue<bool>();
            int tvDelay = ConVar.Find("tv_delay")!.GetPrimitiveValue<int>();

            if (!tvEnable1) return tvDelay;
            int tvDelay1 = ConVar.Find("tv_delay1")!.GetPrimitiveValue<int>();

            if (tvDelay < tvDelay1) return tvDelay1;
            return tvDelay;
        }

        [ConsoleCommand("get5_demo_upload_header_key", "If defined, a custom HTTP header with this name is added to the HTTP requests for demos")]
        [ConsoleCommand("matchzy_demo_upload_header_key", "If defined, a custom HTTP header with this name is added to the HTTP requests for demos")]
        public void DemoUploadHeaderKeyCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string header = command.ArgByIndex(1).Trim();

            if (header != "") demoUploadHeaderKey = header;
        }

        [ConsoleCommand("get5_demo_upload_header_value", "If defined, the value of the custom header added to the demos sent over HTTP")]
        [ConsoleCommand("matchzy_demo_upload_header_value", "If defined, the value of the custom header added to the demos sent over HTTP")]
        public void DemoUploadHeaderValueCommand(CCSPlayerController? player, CommandInfo command)
        {
            if (player != null) return;
            string headerValue = command.ArgByIndex(1).Trim();

            if (headerValue != "") demoUploadHeaderValue = headerValue;
        }
    }
}

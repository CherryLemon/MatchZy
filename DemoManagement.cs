using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

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

        public static string ResolveDemoFilePath(string gameDirectory, string directory, string fileName)
        {
            // Team/map names form part of the file name, never console syntax or
            // a directory. Keep the configured directory separate from the name.
            foreach (char character in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', '"', ';', '\r', '\n' }))
                fileName = fileName.Replace(character, '_');
            return Path.GetFullPath(Path.Combine(directory, fileName), Path.Combine(gameDirectory, "csgo"));
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
                // Source 2's relative write search path may point at addons/metamod
                // after an engine update. Recording and upload must share one
                // absolute path, independent of that search path and process cwd.
                string tempDemoPath = ResolveDemoFilePath(Server.GameDirectory, demoPath, demoFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(tempDemoPath)!);
                activeDemoFile = tempDemoPath;
                Log($"[StartDemoRecording] Starting demo recording, path: {tempDemoPath}");
                Server.ExecuteCommand($"tv_record \"{tempDemoPath}\"");
                isDemoRecording = true;
            }
            catch (Exception ex)
            {
                Log($"[StartDemoRecording - FATAL] Error: {ex.Message}. Starting demo recording with path. Name: {demoFileName}");
                // This is to avoid demo loss in any case of exception
                activeDemoFile = ResolveDemoFilePath(Server.GameDirectory, "", demoFileName);
                Server.ExecuteCommand($"tv_record \"{activeDemoFile}\"");
                isDemoRecording = true;
            }

        }

        public void StopDemoRecording(float delay, string demoFile, long liveMatchId, int currentMapNumber)
        {
            // A map that ended within tv_delay of going live never reached its
            // pending tv_record -- but everything it played is still sitting in
            // the GOTV buffer, so we start recording now and let the flush below
            // drain those rounds into the demo. Without this the map produces no
            // demo at all, and the backend keeps its instance attached waiting
            // for an upload that can never arrive.
            if (demoStartTimer != null && !isDemoRecording)
            {
                CancelPendingDemoRecording();
                Log("[StopDemoRecording] Map ended before the delayed recording started, recording the buffered GOTV broadcast instead.");
                StartDemoRecording();
                demoFile = activeDemoFile;
            }
            // Dropping the stale activeDemoFile stops the previous map's demo
            // from being uploaded a second time under this map number.
            CancelPendingDemoRecording();
            if (!isDemoRecording || demoFile == "")
            {
                Log("[StopDemoRecording] No demo recording is active, nothing to stop or upload.");
                activeDemoFile = "";
                return;
            }
            Log($"[StopDemoRecording] Going to stop demorecording in {delay}s");
            string demoPath = Path.GetFullPath(demoFile, Path.Combine(Server.GameDirectory, "csgo"));
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
                        await UploadDemoAsync(demoPath, liveMatchId, currentMapNumber, roundNumber);
                    });
                });
            });
        }

        /// <summary>
        /// Uploads a finished demo, preferring a direct upload to ThuCS object storage.
        /// The backend hands out a short-lived presigned URL, so a multi-hundred-MB demo
        /// never has to travel through the API process. Anything that goes wrong on that
        /// path -- an old backend, object storage disabled, an expired signature -- falls
        /// back to the legacy POST rather than losing the demo.
        /// </summary>
        public async Task UploadDemoAsync(string demoFilePath, long matchId, int mapNumber, int roundNumber)
        {
            string uploadApiUrl = demoUploadURL;
            string uploadHeaderKey = demoUploadHeaderKey;
            string uploadHeaderValue = demoUploadHeaderValue;
            async Task UploadThroughApi()
            {
                await UploadFileAsync(demoFilePath, uploadApiUrl, uploadHeaderKey, uploadHeaderValue, matchId, mapNumber, roundNumber);
            }

            if (uploadApiUrl == "" || !File.Exists(demoFilePath))
            {
                await UploadThroughApi();
                return;
            }

            try
            {
                string baseUrl = uploadApiUrl.TrimEnd('/');
                using HttpClient apiClient = new() { Timeout = TimeSpan.FromSeconds(30) };
                if (!string.IsNullOrEmpty(uploadHeaderKey) && !string.IsNullOrEmpty(uploadHeaderValue))
                {
                    apiClient.DefaultRequestHeaders.Add(uploadHeaderKey, uploadHeaderValue);
                }

                using HttpResponseMessage ticketResponse = await apiClient.PostAsJsonAsync(
                    $"{baseUrl}/presign", new { map_number = mapNumber });
                if (!ticketResponse.IsSuccessStatusCode)
                {
                    Log($"[UploadDemoAsync] Upload ticket request failed ({ticketResponse.StatusCode}), uploading through the API instead.");
                    await UploadThroughApi();
                    return;
                }

                using JsonDocument ticket = JsonDocument.Parse(await ticketResponse.Content.ReadAsStringAsync());
                JsonElement root = ticket.RootElement;
                string mode = root.TryGetProperty("mode", out JsonElement modeValue) ? modeValue.GetString() ?? "" : "";
                string uploadUrl = root.TryGetProperty("upload_url", out JsonElement urlValue) ? urlValue.GetString() ?? "" : "";
                if (mode != "direct" || uploadUrl == "")
                {
                    Log($"[UploadDemoAsync] Backend asked for a proxied upload (mode: {mode}).");
                    await UploadThroughApi();
                    return;
                }

                long demoSize = new FileInfo(demoFilePath).Length;
                long maxBytes = root.TryGetProperty("max_bytes", out JsonElement maxValue) && maxValue.TryGetInt64(out long parsedMax) ? parsedMax : 0;
                if (maxBytes > 0 && demoSize > maxBytes)
                {
                    // The presigned PUT cannot reject an oversized body itself, and
                    // pushing it only to have the backend refuse it wastes the transfer.
                    Log($"[UploadDemoAsync ERROR] Demo is {demoSize} bytes, over the {maxBytes} byte limit. Not uploading.");
                    return;
                }

                Log($"[UploadDemoAsync] Uploading {demoSize} bytes directly to object storage for matchId: {matchId} mapNumber: {mapNumber}.");
                bool stored;
                // A separate client: the presigned URL carries its own signature and
                // must not be sent with our match credential attached.
                // Finish the direct attempt and its proxy fallback before the
                // backend's 15-minute post-GOTV instance release deadline.
                using (HttpClient storageClient = new() { Timeout = TimeSpan.FromMinutes(10) })
                using (FileStream demoStream = File.OpenRead(demoFilePath))
                using (StreamContent body = new(demoStream))
                {
                    body.Headers.Add("Content-Type", "application/octet-stream");
                    using HttpResponseMessage putResponse = await storageClient.PutAsync(uploadUrl, body);
                    stored = putResponse.IsSuccessStatusCode;
                    if (!stored)
                    {
                        Log($"[UploadDemoAsync ERROR] Direct upload failed. Status code: {putResponse.StatusCode}");
                    }
                }
                if (!stored)
                {
                    await UploadThroughApi();
                    return;
                }

                using HttpResponseMessage completeResponse = await apiClient.PostAsJsonAsync(
                    $"{baseUrl}/complete", new { map_number = mapNumber });
                if (completeResponse.IsSuccessStatusCode)
                {
                    Log($"[UploadDemoAsync] Direct upload confirmed for matchId: {matchId} mapNumber: {mapNumber} roundNumber: {roundNumber}.");
                    return;
                }
                // The bytes are in storage but the backend did not accept them, so the
                // match still has no demo URL; the proxied path is the only way left.
                Log($"[UploadDemoAsync ERROR] Backend rejected the uploaded demo ({completeResponse.StatusCode}).");
                await UploadThroughApi();
            }
            catch (Exception e)
            {
                // Exception messages and COS error bodies may contain the
                // signed URL. Keep credentials out of game-server logs.
                Log($"[UploadDemoAsync FATAL] {e.GetType().Name}. Falling back to uploading through the API.");
                await UploadThroughApi();
            }
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
